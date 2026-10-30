using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Gua.Core;
using Gua.Testing;
using Gua.Testing.Recording;

namespace Gua.Playtest.GuaIntegration;

public sealed record GuardedRecordingCapture(GuaRecording? Recording, bool CaptureFailed);

/// <summary>One official GuaRecorder capture over a borrowed guarded click owner.
/// Only the initial synchronous owner call may dispatch. Capture never certifies a Goal.</summary>
public sealed class GuardedClickRecording
{
    private readonly GuardedUiClick click;
    private readonly string targetId;
    private readonly Func<string, bool> safeText;
    private readonly Func<JsonElement, JsonElement> project;
    private readonly int maxSnapshotBytes, maxSnapshotNodes, maxStringChars;
    private readonly CancellationTokenSource stop;
    private GuaRecorder? recorder;
    private Task<GuaActionEvent>? task;
    private GuardedUiReceipt? confirmedReceipt;
    private GuardedRecordingCapture? frozen;
    private readonly TaskCompletionSource startSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool unavailable;
    private int started, dispatchStarted;
    private int disposed;

    public GuardedClickRecording(GuardedUiClick click, string targetId, Func<string, bool> safeText,
        Func<JsonElement, JsonElement> project, int maxSnapshotBytes, int maxSnapshotNodes, int maxStringChars, CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(click); ArgumentNullException.ThrowIfNull(safeText); ArgumentNullException.ThrowIfNull(project);
        if (maxSnapshotBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
        if (maxSnapshotNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSnapshotNodes));
        if (maxStringChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxStringChars));
        this.click = click; this.targetId = targetId; this.safeText = safeText; this.project = project; this.maxSnapshotBytes = maxSnapshotBytes;
        this.maxSnapshotNodes = maxSnapshotNodes; this.maxStringChars = maxStringChars;
        stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
    }

    public GuardedUiReceipt Start(Action beforeDispatch, TimeSpan timeout, CancellationToken caller)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("RecordingAlreadyStarted");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            caller.ThrowIfCancellationRequested();
            void BeforeSend() { ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); beforeDispatch(); Volatile.Write(ref dispatchStarted, 1); }
            var context = new Context(this, BeforeSend, caller);
            try
            {
                // Validate actual target text before Gua creates its provisional/step buffers.
                if (targetId.Length is < 1 or > 128 || !safeText(targetId)) throw new InvalidDataException("RecordingTargetRefused");
                recorder = new GuaRecorder(context);
                task = recorder.ClickAsync(new(Id: targetId), timeout: timeout,
                    pollInterval: TimeSpan.FromMilliseconds(25), cancellationToken: stop.Token);
            }
            catch (Exception) { unavailable = true; }
            finally { context.CloseDispatch(); }
            // Preserve failures from the actual guarded operation, including caller token/stack.
            context.NativeFailure?.Throw();
            if (!context.ClickInvoked)
            {
                // No guarded attempt occurred, and the facade can no longer dispatch.
                // Capture preflight failure cannot replace the independent gameplay result.
                unavailable = true; stop.Cancel();
                return click.Click(targetId, BeforeSend, caller);
            }
            return click.Receipt ?? throw new InvalidOperationException("RecordingReceiptMissing");
        }
        finally { startSettled.TrySetResult(); }
    }

    /// <summary>Owner-supplied actual receipt; the artifact worker never mutates Run state.</summary>
    public void ObserveReceipt(GuardedUiReceipt receipt) => Volatile.Write(ref confirmedReceipt, receipt);

    /// <summary>Register as owned cleanup. Uses only its supplied finite share; no fresh timeout.</summary>
    public async ValueTask<bool> ReleaseAsync(CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) return true;
        // ClickAsync may synchronously block inside the native guarded send before
        // returning its Task. A null task is not proof that capture has stopped.
        if (Volatile.Read(ref started) != 0 && !startSettled.Task.IsCompleted)
        {
            stop.Cancel();
            try { await startSettled.Task.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _ = RetireAfterStartAsync();
                return false;
            }
        }
        if (task is null) { DisposeStop(); return true; }
        // A successful result can finish Gua's in-memory step from the SDK's retained
        // event even after the borrowed transport was disconnected by its own owner.
        if (Volatile.Read(ref confirmedReceipt)?.State != GuaRemoteDispatchState.Completed) stop.Cancel();
        try { await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stop.Cancel();
            _ = task.ContinueWith(t => { _ = t.Exception; DisposeStop(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
        catch (Exception) { /* Capture failure is classified separately from release. */ }
        DisposeStop(); return true;
    }
    private async Task RetireAfterStartAsync()
    {
        await startSettled.Task.ConfigureAwait(false);
        try { if (task is not null) await task.ConfigureAwait(false); }
        catch (Exception) { /* Observe late canceled/faulted capture, never adopt it. */ }
        finally { DisposeStop(); }
    }
    private void DisposeStop() { if (Interlocked.Exchange(ref disposed, 1) == 0) stop.Dispose(); }

    /// <summary>After cleanup: copied facts only, no host/Run reads or additional wait.</summary>
    public GuardedRecordingCapture Freeze()
    {
        if (frozen is not null) return frozen;
        if (Volatile.Read(ref started) != 0 && !startSettled.Task.IsCompleted)
            return frozen = new(null, true); // Unknown in-flight start, never assert unsent.
        var receipt = Volatile.Read(ref confirmedReceipt);
        if (receipt?.DispatchBegan != true && Volatile.Read(ref dispatchStarted) == 0) return frozen = new(null, false);
        if (unavailable || task?.IsCompletedSuccessfully != true || receipt is null || receipt.State != GuaRemoteDispatchState.Completed || receipt.Succeeded != true)
            return frozen = new(null, true);
        try
        {
            var captured = recorder!.Recording;
            var result = task.GetAwaiter().GetResult();
            if (captured.SchemaVersion != 1 || captured.Steps.Count != 1) return frozen = new(null, true);
            var step = captured.Steps[0];
            if (step.Action != GuaRecordedAction.click || step.Target?.Id != targetId || step.RequestId != receipt.RequestId ||
                step.PreRevision != receipt.Revision || step.PostRevision != result.Revision || step.Sensitive)
                return frozen = new(null, true);
            GuaRecordingFile.Validate(captured);
            return frozen = new(captured with { Steps = captured.Steps.ToArray() }, false);
        }
        catch (Exception) { return frozen = new(null, true); }
    }

    private sealed class Context(GuardedClickRecording owner, Action beforeDispatch, CancellationToken caller) : IGuaContext
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private int dispatchOpen = 1;
        internal bool ClickInvoked { get; private set; }
        internal ExceptionDispatchInfo? NativeFailure { get; private set; }
        internal void CloseDispatch() => Volatile.Write(ref dispatchOpen, 0);
        public string GetUiTreeJson()
        {
            var text = owner.click.ReadForRecording(c => c.GetUiTreeJson());
            using var raw = GuaJsonAdmission.Parse(text, owner.maxSnapshotBytes, owner.maxSnapshotNodes, owner.maxStringChars);
            var projected = owner.project(raw.RootElement);
            var safe = projected.GetRawText();
            if (Encoding.UTF8.GetByteCount(safe) > owner.maxSnapshotBytes) throw new InvalidDataException("RecordingSnapshotLimit");
            GuaJsonAdmission.Validate(Encoding.UTF8.GetBytes(safe), owner.maxSnapshotNodes, owner.maxStringChars);
            if (!GuaDistribution.ValidateJson("ui-tree.schema.json", safe))
                throw new InvalidDataException("RecordingSnapshotRefused");
            return safe;
        }
        public GuaQueryResult Query(GuaSelector selector)
        {
            if (selector != new GuaSelector(Id: owner.targetId)) throw new NotSupportedException("RecordingRequiresExplicitTarget");
            var result = owner.click.ReadForRecording(c => c.Query(selector));
            if (!result.Valid || result.Matches.Count != 1 || result.Matches[0].Id != owner.targetId ||
                result.Matches.Any(match => new[] { match.Id, match.Role, match.Label, match.ParentId }
                    .Any(text => text is not null && !owner.safeText(text))) ||
                result.Error is not null && !owner.safeText(result.Error))
                throw new InvalidDataException("RecordingQueryRefused");
            return result;
        }
        public GuaVersion GetVersion() => owner.click.ReadForRecording(c => c.GetVersion());
        public GuaContextStatus GetContextStatus() => owner.click.ReadForRecording(c => c.GetContextStatus());
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        {
            if (Volatile.Read(ref dispatchOpen) == 0 || Environment.CurrentManagedThreadId != ownerThread || ClickInvoked ||
                request.Action != GuaActionType.Click || request.NodeId != owner.targetId || request.Value is not null || request.Sensitive)
                throw new NotSupportedException("RecordingDispatchOutsideOwner");
            ClickInvoked = true;
            GuardedUiReceipt receipt;
            try { receipt = owner.click.Click(owner.targetId, beforeDispatch, caller); }
            catch (Exception error) { NativeFailure = ExceptionDispatchInfo.Capture(error); throw; }
            requestId = receipt.RequestId ?? 0;
            return receipt.State is GuaRemoteDispatchState.Enqueued or GuaRemoteDispatchState.Completed && requestId != 0
                ? GuaActionError.None : GuaActionError.Unsupported;
        }
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent result)
        {
            if (!owner.click.TryRecordingEvent(requestId, out result)) return false;
            // These actual event fields must not reach Recorder/implicit diagnostics when private.
            if (result.Sensitive || !owner.safeText(result.NodeId) || !owner.safeText(result.Value))
                throw new InvalidDataException("RecordingCompletionRefused");
            return true;
        }
        public GuaNodeState GetNodeState(string id) => throw new NotSupportedException("RecordingUsesExplicitQuery");
        public string FindNodeById(string id) => throw new NotSupportedException("RecordingUsesExplicitQuery");
        public string FindNodeByRole(string role, string? name = null) => throw new NotSupportedException("RecordingUsesExplicitQuery");
        public string FindNodeByText(string text) => throw new NotSupportedException("RecordingUsesExplicitQuery");
        public bool EnqueueClick(string id) => throw new NotSupportedException("RecordingRequiresCorrelation");
        public bool TryPollActionEvent(out GuaActionEvent result) => throw new NotSupportedException("RecordingRequiresCorrelation");
        public bool TryPollEvent(out GuaEvent result) => throw new NotSupportedException("RecordingCannotConsumeOtherEvents");
    }
}
