using System.Text.Json;
using Gua.Core;
using Gua.Runtime;

namespace Gua.Playtest.GuaIntegration;

public enum ActionAttemptStatus { Pending, Succeeded, Failed, Rejected, TimedOut, Aborted }
public enum ConfirmedActionStage { NotSent, DispatchAttempted, Enqueued, HostCompleted }
public sealed record ActionAttempt(string RunId, string ActionExecutionId, string ActionId,
    ulong SessionEpoch, ulong? RequestId, ActionAttemptStatus Status, ConfirmedActionStage Stage,
    int? GuaErrorCode = null, string Reason = "", JsonElement? Selector = null, string? RuntimeId = null);

/// <summary>Trusted local runtime input adapter. The runtime and process remain caller-owned.
/// Only this Run's input owner is released. There is no command tunnel, world mutation, reset,
/// planner confirmation or second scheduler. Timed segments use Gua.Testing.Recording separately.</summary>
public sealed class OwnedGameInput : IDisposable
{
    private readonly GuaRuntime runtime;
    private readonly GuaGameInputSession owner;
    private readonly GuaObservationProfile profile;
    private readonly string runId;
    private readonly int maxAttempts;
    private readonly Dictionary<string, ActionAttempt> attempts = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool disposed;

    public OwnedGameInput(GuaRuntime runtime, string runId, GuaObservationProfile profile, int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        this.runtime = runtime; this.runId = runId; this.profile = profile; this.maxAttempts = maxAttempts;
        GuaVersion.Parse(runtime.GetVersionJson()).EnsureCompatible("2", 1);
        if (!runtime.SupportsGuardedGameInput) throw new NotSupportedException("Guarded input is required.");
        owner = runtime.CreateGameInputSession(profile);
    }

    public ActionAttempt Send(string actionExecutionId, ulong expectedEpoch, ulong expectedActionRevision,
        GuaGameInputKind kind, GuaGameInputOperation operation, string target, object? value,
        TimeSpan? lease, Func<bool> authorizeNow, CancellationToken cancellationToken = default,
        double x = 0, double y = 0, int deviceIndex = 0)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(actionExecutionId);
            if (attempts.TryGetValue(actionExecutionId, out var existing)) return existing;
            if (attempts.Count >= maxAttempts) throw new InvalidOperationException("Action attempt limit reached.");
            var attempt = new ActionAttempt(runId, actionExecutionId, target, expectedEpoch, null,
                ActionAttemptStatus.Rejected, ConfirmedActionStage.NotSent);
            // Reserve before validation/dispatch. A rejected or uncertain attempt is consumed once.
            attempts.Add(actionExecutionId, attempt);
            if (kind == GuaGameInputKind.Cleanup || operation is GuaGameInputOperation.Reset or GuaGameInputOperation.ReleaseAll ||
                kind == GuaGameInputKind.Semantic && operation is not (GuaGameInputOperation.Press or GuaGameInputOperation.Set or GuaGameInputOperation.Release))
                return Save(attempt with { Reason = "operation-not-public" });
            if (profile is not (GuaObservationProfile.Debug or GuaObservationProfile.Player))
                return Save(attempt with { Reason = "profile-not-authorized" });
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!authorizeNow()) return Save(attempt with { Reason = "permission-denied" });
                // Native validates the current value schema, capabilities, epoch and action revision.
                // Confirmation-required actions are refused; trusted consent integration belongs to Runner.
                runtime.ValidateGameInput(profile, expectedEpoch, expectedActionRevision, kind, operation, target, value,
                    lease, x, y, deviceIndex, false, false);
                attempt = attempt with { Status = ActionAttemptStatus.Pending, Stage = ConfirmedActionStage.DispatchAttempted };
                Save(attempt);
                ulong request = owner.SendGuarded(expectedEpoch, expectedActionRevision, kind, operation, target, value,
                    lease, x, y, deviceIndex, false, false, () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!authorizeNow()) throw new InvalidOperationException("Permission revoked.");
                    });
                return Save(attempt with { RequestId = request, Stage = ConfirmedActionStage.Enqueued, Reason = "enqueued" });
            }
            catch (OperationCanceledException)
            { return Save(attempt with { Status = ActionAttemptStatus.Aborted, Reason = "cancelled-unconfirmed" }); }
            catch (InvalidOperationException)
            { return Save(attempt with { Status = attempt.Stage == ConfirmedActionStage.NotSent ? ActionAttemptStatus.Rejected : ActionAttemptStatus.Pending,
                Reason = attempt.Stage == ConfirmedActionStage.NotSent ? "gua-preflight-rejected" : "dispatch-unconfirmed" }); }
        }
    }

    public ActionAttempt Poll(string actionExecutionId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var attempt = attempts[actionExecutionId];
            if (attempt.Status != ActionAttemptStatus.Pending || attempt.RequestId is null || attempt.Reason != "enqueued") return attempt;
            try
            {
                if (owner.GetHealth().SessionEpoch != attempt.SessionEpoch)
                    return Save(attempt with { Reason = "stale-session-unconfirmed" });
                var result = owner.PollResult(attempt.RequestId.Value);
                if (!result.Completed) return attempt;
                if (result.RequestId != attempt.RequestId)
                    return Save(attempt with { Reason = "correlation-unconfirmed" });
                return Save(attempt with { Status = result.Succeeded == true ? ActionAttemptStatus.Succeeded : ActionAttemptStatus.Failed,
                    Stage = ConfirmedActionStage.HostCompleted, GuaErrorCode = result.ErrorCode, Reason = "host-completed" });
            }
            catch (InvalidOperationException) { return Save(attempt with { Reason = "completion-unconfirmed" }); }
        }
    }
    public ActionAttempt EndWait(string actionExecutionId, bool cancelled)
    {
        lock (gate)
        {
            var attempt = attempts[actionExecutionId];
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
                if (attempt.Status == ActionAttemptStatus.Pending) Save(attempt with { Status = ActionAttemptStatus.Aborted, Reason = "owner-closed-unconfirmed" });
            owner.Dispose();
        }
    }
}
