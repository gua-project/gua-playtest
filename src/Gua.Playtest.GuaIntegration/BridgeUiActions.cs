using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

/// <summary>One owned Gua connection per Run. UI wire v1 has no atomic epoch guard;
/// the caller must serialize host lifecycle changes before calling Send. Otherwise use
/// this adapter only for observation/testing, never claim stale-ID-safe dispatch.</summary>
public sealed class BridgeUiActions : IDisposable
{
    private readonly GuaWebSocketContext context;
    private readonly string runId;
    private readonly int maxAttempts;
    private readonly Func<IDisposable>? enterHostLifecycleLease;
    private readonly Dictionary<string, ActionAttempt> attempts = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool disposed;
    public BridgeUiActions(string endpoint, string runId, TimeSpan requestTimeout, int maxAttempts,
        Func<IDisposable>? enterHostLifecycleLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (requestTimeout <= TimeSpan.Zero || maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        this.runId = runId; this.maxAttempts = maxAttempts;
        this.enterHostLifecycleLease = enterHostLifecycleLease;
        context = new(endpoint, requestTimeout);
    }
    public ActionAttempt Send(string executionId, JsonObject selector, GuaActionRequest request,
        ulong expectedEpoch, Func<bool> authorizeNow, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            if (attempts.TryGetValue(executionId, out var existing)) return existing;
            if (attempts.Count >= maxAttempts) throw new InvalidOperationException("Action attempt limit reached.");
            var attempt = new ActionAttempt(runId, executionId, request.Action.ToString(), expectedEpoch, null,
                ActionAttemptStatus.Rejected, ConfirmedActionStage.NotSent, Selector: JsonSerializer.SerializeToElement(selector));
            Save(attempt);
            // An external bridge offers no atomic compare-and-enqueue. Refuse dispatch unless
            // trusted host setup supplies a lease shared by its reset/replacement/frame writer.
            if (enterHostLifecycleLease is null) return Save(attempt with { Reason = "atomic-dispatch-unavailable" });
            using var lifecycleLease = enterHostLifecycleLease();
            if (lifecycleLease is null) return Save(attempt with { Reason = "atomic-dispatch-unavailable" });
            try
            {
                context.GetVersion().EnsureCompatible("2", 1);
                cancellationToken.ThrowIfCancellationRequested();
                if (!authorizeNow()) return Save(attempt with { Reason = "permission-denied" });
                var before = context.GetContextStatus();
                if (before.SessionEpoch != expectedEpoch) return Save(attempt with { Reason = "stale-session" });
                var resolution = context.Query(BridgeSelectors.Ui(selector));
                // Never select the first of multiple matches or disclose candidate IDs in feedback.
                if (!resolution.Valid || resolution.Matches.Count != 1)
                    return Save(attempt with { Reason = "target-unavailable" });
                string treeJson = context.GetUiTreeJson();
                if (!GuaDistribution.ValidateJson("ui-tree.schema.json", treeJson))
                    return Save(attempt with { Reason = "tree-unconfirmed" });
                using var treeDocument = JsonDocument.Parse(treeJson);
                var tree = treeDocument.RootElement;
                if (tree.GetProperty("revision").GetUInt64() != before.Revision ||
                    tree.TryGetProperty("sessionEpoch", out var treeEpoch) && treeEpoch.GetUInt64() != expectedEpoch ||
                    !BridgeSelectors.UiMatchesTree(selector, tree, resolution.Matches.Select(m => m.Id)))
                    return Save(attempt with { Reason = "stale-query" });
                var after = context.GetContextStatus();
                if (before.SessionEpoch != after.SessionEpoch || before.Revision != after.Revision)
                    return Save(attempt with { Reason = "changed-during-resolution" });
                cancellationToken.ThrowIfCancellationRequested();
                if (!authorizeNow()) return Save(attempt with { Reason = "permission-denied" });
                attempt = attempt with { RuntimeId = resolution.Matches[0].Id, Status = ActionAttemptStatus.Pending,
                    Stage = ConfirmedActionStage.DispatchAttempted };
                Save(attempt);
                var error = context.EnqueueAction(request with { NodeId = attempt.RuntimeId, RequestId = 0 }, out var id);
                // Gua's remote EnqueueAction collapses InvalidOperationException into InvalidArgument.
                // That generic result is ambiguous, rather than proof of remote rejection/nonexecution.
                if (error != GuaActionError.None)
                    return Save(attempt with { Status = error == GuaActionError.InvalidArgument ? ActionAttemptStatus.Pending : ActionAttemptStatus.Rejected,
                        GuaErrorCode = (int)error, Reason = error == GuaActionError.InvalidArgument ? "dispatch-unconfirmed" : "gua-rejected" });
                if (id == 0) return Save(attempt with { Status = ActionAttemptStatus.Pending, Reason = "dispatch-unconfirmed" });
                return Save(attempt with { RequestId = id, Stage = ConfirmedActionStage.Enqueued, Reason = "enqueued" });
            }
            catch (OperationCanceledException) { return Save(attempt with
                { Status = attempt.Stage == ConfirmedActionStage.NotSent ? ActionAttemptStatus.Aborted : ActionAttemptStatus.Pending,
                    Reason = attempt.Stage == ConfirmedActionStage.NotSent ? "cancelled-before-dispatch" : "dispatch-unconfirmed" }); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException or FormatException or OverflowException or System.Net.WebSockets.WebSocketException)
            { return Save(attempt with { Status = attempt.Stage == ConfirmedActionStage.NotSent ? ActionAttemptStatus.Rejected : ActionAttemptStatus.Pending,
                Reason = attempt.Stage == ConfirmedActionStage.NotSent ? "preflight-rejected" : "dispatch-unconfirmed" }); }
        }
    }
    public ActionAttempt Poll(string executionId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var attempt = attempts[executionId];
            if (attempt.Status != ActionAttemptStatus.Pending || attempt.RequestId is null) return attempt;
            // A poll may have consumed the remote completion before its reply was lost.
            // Preserve uncertainty until EndWait; another poll cannot restore that evidence.
            if (attempt.Reason != "enqueued") return attempt;
            try
            {
                if (context.GetContextStatus().SessionEpoch != attempt.SessionEpoch)
                    return Save(attempt with { Status = ActionAttemptStatus.Pending, Reason = "stale-session-unconfirmed" });
                if (!context.TryPollActionEvent(attempt.RequestId.Value, out var result)) return attempt;
                if (result.RequestId != attempt.RequestId || result.SessionEpoch != attempt.SessionEpoch || result.NodeId != attempt.RuntimeId)
                    return Save(attempt with { Status = ActionAttemptStatus.Pending, Reason = "correlation-unconfirmed" });
                return Save(attempt with { Status = result.Succeeded ? ActionAttemptStatus.Succeeded : ActionAttemptStatus.Failed,
                    Stage = ConfirmedActionStage.HostCompleted, GuaErrorCode = (int)result.Error, Reason = "host-completed" });
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            { return Save(attempt with { Status = ActionAttemptStatus.Pending, Reason = "completion-unconfirmed" }); }
        }
    }
    public ActionAttempt EndWait(string executionId, bool cancelled)
    {
        lock (gate)
        {
            var attempt = attempts[executionId];
            return attempt.Status == ActionAttemptStatus.Pending ? Save(attempt with
            { Status = cancelled ? ActionAttemptStatus.Aborted : ActionAttemptStatus.TimedOut, Reason = "completion-unconfirmed" }) : attempt;
        }
    }
    public IReadOnlyList<ActionAttempt> Attempts { get { lock (gate) return Array.AsReadOnly(attempts.Values.ToArray()); } }
    private ActionAttempt Save(ActionAttempt attempt) { attempts[attempt.ActionExecutionId] = attempt; return attempt; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            foreach (var attempt in attempts.Values.ToArray())
                if (attempt.Status == ActionAttemptStatus.Pending) Save(attempt with { Status = ActionAttemptStatus.Aborted, Reason = "connection-closed-unconfirmed" });
            context.Dispose();
        }
    }
}
