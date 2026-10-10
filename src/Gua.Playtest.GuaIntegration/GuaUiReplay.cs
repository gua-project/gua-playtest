using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Playtest.Core;
using Gua.Testing;
using Gua.Testing.Recording;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Gua's published UI scheduler and correlated completion reader. The supplied lifecycle
/// lease MUST serialize every host reset/replacement/frame writer, not only this connection.
/// Admission checks current definitions/profile/permission/secrets/context for the whole batch.
/// A host without that trusted lease cannot safely use public UI wire v1 to dispatch.</summary>
public sealed class GuaUiReplay : IReplayPlayback
{
    private readonly IGuaContext context;
    private readonly Func<IDisposable> lifecycleLease;
    private readonly Func<ReplayBatch, bool, ReplayCheck> admit;
    private readonly Func<string, string?>? secretResolver;
    private readonly TimeSpan pollInterval;
    public GuaUiReplay(IGuaContext context, Func<IDisposable> lifecycleLease,
        Func<ReplayBatch, bool, ReplayCheck> admit, TimeSpan pollInterval, Func<string, string?>? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(lifecycleLease); ArgumentNullException.ThrowIfNull(admit);
        if (pollInterval <= TimeSpan.Zero || pollInterval > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        this.context = context; this.lifecycleLease = lifecycleLease; this.admit = admit;
        this.pollInterval = pollInterval; this.secretResolver = secretResolver;
    }
    public ReplayCheck Check(ReplayBatch batch, bool starting = true)
    {
        GuaRecording recording;
        try
        {
            recording = Parse(batch);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException)
        { return ReplayCheck.Unsupported; }
        if (batch.Timing is not ("recorded" or "conditionSynchronized") ||
            recording.Steps.Any(s => s.CoordinateFallback is not null || batch.Timing == "recorded" && s.WaitCondition is not null))
            return ReplayCheck.Unsupported;
        return admit(batch, starting);
    }
    public ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken token)
        => new(Task.Run(async () =>
        {
            var recording = Parse(batch);
            var queued = new QueuedContext(context, lifecycleLease, calls, recording, () => admit(batch, false), token);
            try
            {
                await GuaReplayer.ReplayAsync(queued, recording, new GuaReplayOptions
                {
                    TimingMode = batch.Timing == "recorded" ? GuaReplayTimingMode.PreserveDelays : GuaReplayTimingMode.PreferConditions,
                    ActionTimeout = batch.Timeout, PollInterval = pollInterval,
                    SecretResolver = secretResolver, FailOnCoordinateFallback = true
                }, token).ConfigureAwait(false);
                return new ReplayReceipt(ReplayReceiptStatus.Succeeded, queued.CompletedSteps, true);
            }
            catch (ReplayDispatchClosedException)
            {
                // UI playback awaits each correlated result before requesting the next action.
                return new ReplayReceipt(ReplayReceiptStatus.Succeeded, queued.CompletedSteps, true);
            }
            catch (GuaActionException exception)
            {
                return new ReplayReceipt(exception.Kind is GuaActionFailureKind.Failed or GuaActionFailureKind.Rejected
                    ? ReplayReceiptStatus.Failed : ReplayReceiptStatus.Unconfirmed, queued.CompletedSteps, true, exception);
            }
        }, token));
    private static GuaRecording Parse(ReplayBatch batch)
    {
        var recording = JsonSerializer.Deserialize<GuaRecording>(batch.RecordingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("ReplayRecordingInvalid");
        GuaRecordingFile.Validate(recording);
        if (recording.Steps.Count != batch.Count || batch.OffsetBaseMilliseconds < 0 || batch.Timeout <= TimeSpan.Zero)
            throw new InvalidDataException("ReplayRecordingInvalid");
        recording = recording with { Steps = recording.Steps.Select(s => s with
        { RelativeMilliseconds = checked(s.RelativeMilliseconds - batch.OffsetBaseMilliseconds) }).ToArray() };
        GuaRecordingFile.Validate(recording); return recording;
    }
    private sealed class QueuedContext(IGuaContext context, Func<IDisposable> lease, IReplayCalls calls,
        GuaRecording recording, Func<ReplayCheck> admit, CancellationToken token) : IGuaContext
    {
        private int next;
        private readonly HashSet<ulong> completed = [];
        private readonly Dictionary<ulong, (ulong Epoch, string? Node)> pending = [];
        public int CompletedSteps => completed.Count;
        private T Read<T>(Func<T> callback) => calls.ReadAsync(() =>
        {
            using var held = lease() ?? throw new InvalidOperationException("ReplayLifecycleLeaseRequired");
            return callback();
        }, token).AsTask().GetAwaiter().GetResult();
        public string GetUiTreeJson() => Read(context.GetUiTreeJson);
        public GuaVersion GetVersion() => Read(context.GetVersion);
        public GuaContextStatus GetContextStatus() => Read(context.GetContextStatus);
        public GuaNodeState GetNodeState(string id) => Read(() => context.GetNodeState(id));
        public string FindNodeById(string id) => Read(() => context.FindNodeById(id));
        public string FindNodeByRole(string role, string? name = null) => Read(() => context.FindNodeByRole(role, name));
        public string FindNodeByText(string text) => Read(() => context.FindNodeByText(text));
        public GuaQueryResult Query(GuaSelector selector) => Read(() => context.Query(selector));
        public bool EnqueueClick(string id) => throw new NotSupportedException("ReplayUsesCorrelatedActions");
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        {
            var index = next;
            var result = calls.SendAsync(index, beforeSend =>
            {
                using var held = lease() ?? throw new InvalidOperationException("ReplayLifecycleLeaseRequired");
                context.GetVersion().EnsureCompatible("2", 1);
                var before = context.GetContextStatus();
                var target = recording.Steps[index].Target!;
                // Re-resolve the SAME recorded selector under the shared lifecycle lease.
                // Gua Get() enforces one match; there is no first-match or coordinate repair.
                string? id = null;
                if (!target.CurrentFocus)
                {
                    var query = GuaAssertions.Query(context);
                    if (target.Id is { } recordedId) query = query.ById(recordedId);
                    else
                    {
                        query = query.ByRole(target.Role!, target.Name);
                        if (target.Scope is { } scope) query = query.Within(scope);
                    }
                    id = query.Get().Id;
                    var selector = new JsonObject();
                    if (target.Id is { } exactId) selector["id"] = new JsonObject { ["value"] = exactId };
                    else
                    {
                        selector["role"] = new JsonObject { ["value"] = target.Role };
                        if (target.Name is { } name) selector["name"] = new JsonObject { ["value"] = name };
                        if (target.Scope is { } parent) selector["scope"] = new JsonObject { ["parentId"] = parent };
                    }
                    var treeJson = context.GetUiTreeJson();
                    if (!GuaDistribution.ValidateJson("ui-tree.schema.json", treeJson)) throw new InvalidOperationException("ReplayTreeInvalid");
                    using var document = JsonDocument.Parse(treeJson);
                    var tree = document.RootElement;
                    if (tree.GetProperty("revision").GetUInt64() != before.Revision ||
                        tree.GetProperty("sessionEpoch").GetUInt64() != before.SessionEpoch ||
                        !BridgeSelectors.UiMatchesTree(selector, tree, [id])) throw new InvalidOperationException("ReplayQueryChanged");
                }
                var after = context.GetContextStatus();
                if (before.SessionEpoch != after.SessionEpoch || before.Revision != after.Revision)
                    throw new InvalidOperationException("ReplayContextChanged");
                if (admit() != ReplayCheck.Approved) throw new InvalidOperationException("ReplayAdmissionChanged");
                beforeSend();
                var error = context.EnqueueAction(request with { NodeId = id, RequestId = 0 }, out var actualId);
                if (error == GuaActionError.None && actualId != 0) pending.Add(actualId, (after.SessionEpoch, id));
                return new ReplaySend<(GuaActionError, ulong)>((error, actualId), error == GuaActionError.None && actualId != 0);
            }, token).AsTask().GetAwaiter().GetResult();
            next++; requestId = result.Item2; return result.Item1;
        }
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
        {
            var result = Read(() =>
            {
                if (!pending.TryGetValue(requestId, out var expected) || context.GetContextStatus().SessionEpoch != expected.Epoch)
                    throw new InvalidOperationException("ReplaySessionChanged");
                var found = context.TryPollActionEvent(requestId, out var action);
                if (found && (action.RequestId != requestId || action.SessionEpoch != expected.Epoch || action.NodeId != expected.Node))
                    throw new InvalidOperationException("ReplayCorrelationInvalid");
                return (found, action);
            });
            e = result.action;
            if (result.found)
            {
                if (e.RequestId != requestId) throw new InvalidOperationException("ReplayCorrelationInvalid");
                completed.Add(requestId);
            }
            return result.found;
        }
        public bool TryPollActionEvent(out GuaActionEvent e) => throw new NotSupportedException("ReplayRequiresRequestId");
        public bool TryPollEvent(out GuaEvent e) => throw new NotSupportedException("ReplayCannotConsumeOtherEvents");
    }
}
