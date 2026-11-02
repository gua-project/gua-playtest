using System.Text.Json;
using Gua.Core;
using Gua.Playtest.Core;
using Gua.Testing;
using Gua.Testing.Recording;

namespace Gua.Playtest.GuaIntegration;

/// <summary>One explicit UI click through Gua's scheduler and remote atomic guard.
/// Borrows the prepared owner; never supplies a local substitute for a host lifecycle lease.</summary>
public sealed class GuaGuardedClickReplay(Func<GuardedUiClick> owner, Action verifyOwner,
    Action<GuardedUiReceipt> record, TimeSpan pollInterval) : IReplayPlayback
{
    private int played;
    /// <summary>Payload/timing shape only; grants no prepared-host or dispatch authority.</summary>
    public static bool SupportsRecording(ReplayBatch batch)
    {
        try { _ = Parse(batch); return true; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or OverflowException)
        { return false; }
    }
    public ReplayCheck Check(ReplayBatch batch, bool starting = true)
    {
        if (!SupportsRecording(batch) || pollInterval <= TimeSpan.Zero || pollInterval > TimeSpan.FromSeconds(1)) return ReplayCheck.Unsupported;
        verifyOwner(); return ReplayCheck.Approved;
    }
    private static GuaRecording Parse(ReplayBatch batch)
    {
        var recording = JsonSerializer.Deserialize<GuaRecording>(batch.RecordingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("ReplayRecordingInvalid");
        GuaRecordingFile.Validate(recording);
        if (batch.BeforeStep != 0 || batch.Count != 1 || batch.OffsetBaseMilliseconds != 0 ||
            batch.Timing != "conditionSynchronized" || batch.Timeout <= TimeSpan.Zero || batch.Timeout > TimeSpan.FromDays(1) || recording.Steps.Count != 1)
            throw new InvalidDataException("GuardedReplayUnsupported");
        var step = recording.Steps[0];
        if (step.Action != GuaRecordedAction.click || step.Target?.Id is not { Length: > 0 and <= 128 } id ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            step.Target.CurrentFocus || step.Target.Role is not null || step.Target.Name is not null || step.Target.Scope is not null ||
            step.Sensitive || step.SecretKey is not null || step.Value is not null || step.WaitCondition is not null || step.CoordinateFallback is not null ||
            step.DeltaX != 0 || step.DeltaY != 0 || step.ScrollUnit != 0 || step.Modifiers != 0 ||
            step.RelativeMilliseconds < 0 || step.RelativeMilliseconds >= batch.Timeout.TotalMilliseconds)
            throw new InvalidDataException("GuardedReplayUnsupported");
        return recording;
    }
    public ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken token)
    {
        if (Interlocked.Exchange(ref played, 1) != 0) throw new InvalidOperationException("GuardedReplayAlreadyUsed");
        return new(Task.Run(async () =>
        {
            var recording = Parse(batch);
            if (pollInterval <= TimeSpan.Zero || pollInterval > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
            var context = new Context(owner(), calls, verifyOwner, record, recording.Steps[0].Target!.Id!, token);
            try
            {
                await GuaReplayer.ReplayAsync(context, recording, new GuaReplayOptions
                { TimingMode = GuaReplayTimingMode.PreferConditions, ActionTimeout = batch.Timeout,
                    PollInterval = pollInterval, FailOnCoordinateFallback = true }, token).ConfigureAwait(false);
                return new ReplayReceipt(ReplayReceiptStatus.Succeeded, context.Completed ? 1 : 0, true);
            }
            catch (ReplayDispatchClosedException) { return new ReplayReceipt(ReplayReceiptStatus.Succeeded, 0, true); }
            catch (GuaActionException error)
            {
                var status = context.Uncertain || error.Kind is not (GuaActionFailureKind.Failed or GuaActionFailureKind.Rejected)
                    ? ReplayReceiptStatus.Unconfirmed : ReplayReceiptStatus.Failed;
                return new ReplayReceipt(status, status == ReplayReceiptStatus.Failed && context.Completed ? 1 : 0, true, error);
            }
            catch (Exception error) when (error is not OperationCanceledException && context.Uncertain)
            { return new ReplayReceipt(ReplayReceiptStatus.Unconfirmed, 0, true, error); }
        }, token));
    }

    private sealed class Context(GuardedUiClick click, IReplayCalls calls, Action verify,
        Action<GuardedUiReceipt> record, string target, CancellationToken token) : IGuaContext
    {
        private int sent;
        public bool Completed { get; private set; }
        public bool Uncertain => click.Receipt?.State == GuaRemoteDispatchState.Uncertain;
        private T Read<T>(Func<IGuaContext, T> read) => calls.ReadAsync(() =>
        { verify(); return click.ReadForRecording(read); }, token).AsTask().GetAwaiter().GetResult();
        public GuaVersion GetVersion() => Read(c => c.GetVersion());
        public GuaContextStatus GetContextStatus() => Read(c => c.GetContextStatus());
        public string GetUiTreeJson() => Read(c => c.GetUiTreeJson());
        public GuaQueryResult Query(GuaSelector selector)
        {
            if (selector != new GuaSelector(Id: target)) throw new NotSupportedException("ReplayExplicitTargetRequired");
            return Read(c => c.Query(selector));
        }
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        {
            if (Interlocked.Exchange(ref sent, 1) != 0 || request.Action != GuaActionType.Click || request.NodeId != target ||
                request.Value is not null || request.Sensitive) throw new InvalidOperationException("ReplayDuplicateOrChangedSend");
            var receipt = calls.SendAsync(0, beforeSend =>
            {
                verify();
                var result = click.Click(target, beforeSend, token); record(result);
                return new ReplaySend<GuardedUiReceipt>(result,
                    result.State is GuaRemoteDispatchState.Enqueued or GuaRemoteDispatchState.Completed);
            }, token).AsTask().GetAwaiter().GetResult();
            requestId = receipt.RequestId ?? 0;
            return receipt.State is GuaRemoteDispatchState.Enqueued or GuaRemoteDispatchState.Completed && requestId != 0
                ? GuaActionError.None : GuaActionError.Unsupported;
        }
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent result)
        {
            var value = calls.ReadAsync(() =>
            {
                verify(); var receipt = click.Poll(); if (receipt.State != GuaRemoteDispatchState.Enqueued) record(receipt);
                var found = click.TryRecordingEvent(requestId, out var completion);
                return (found, completion);
            }, token).AsTask().GetAwaiter().GetResult();
            result = value.completion;
            if (value.found) Completed = true;
            return value.found;
        }
        public GuaNodeState GetNodeState(string id) => Read(c => c.GetNodeState(id));
        public string FindNodeById(string id) => throw new NotSupportedException("ReplayUsesExplicitQuery");
        public string FindNodeByRole(string role, string? name = null) => throw new NotSupportedException("ReplayUsesExplicitQuery");
        public string FindNodeByText(string text) => throw new NotSupportedException("ReplayUsesExplicitQuery");
        public bool EnqueueClick(string id) => throw new NotSupportedException("ReplayRequiresCorrelation");
        public bool TryPollActionEvent(out GuaActionEvent result) => throw new NotSupportedException("ReplayRequiresCorrelation");
        public bool TryPollEvent(out GuaEvent result) => throw new NotSupportedException("ReplayCannotConsumeOtherEvents");
    }
}
