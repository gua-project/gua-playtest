using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>One trusted serialized owner. Compose PrepareAsync into RunExecutor's preparation callback.
/// Returned boundary has no dispatch authority until the execution owner establishes Running and evaluates it.</summary>
public sealed class HostPreparation
{
    private static readonly object LeaseLock = new();
    private static readonly HashSet<string> ActiveEndpoints = new(StringComparer.Ordinal);
    private readonly PreparationPolicy policy;
    private sealed class ReleaseClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan Elapsed => elapsed.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(FiniteOperation.DelayIndependentAsync(duration, token));
    }
    // Late self-release must neither read a frozen Run clock nor mutate its evidence from a provider continuation.
    private readonly IClock releaseClock = new ReleaseClock();
    private IClock preparationClock = null!;
    private Action<Exception>? recordException;
    private readonly IProcessLauncher launcher;
    private readonly IPreparationConnector connector;
    private readonly IPreparationTrace trace;
    private bool used;
    private int pendingReleases;
    private int pendingAcquisitions;
    public HostPreparation(PreparationPolicy policy, IClock clock, IProcessLauncher launcher,
        IPreparationConnector connector, IPreparationTrace trace)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(launcher); ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(trace);
        if (!Enum.IsDefined(policy.HostMode) || !Enum.IsDefined(policy.PlayMode) || !policy.Endpoint.IsAbsoluteUri ||
            policy.Endpoint.Scheme is not ("ws" or "wss") || policy.Endpoint.Port <= 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(policy.Endpoint.OriginalString, @"^wss?://(?:\[[^\]]+\]|[^/:]+):[0-9]+(?:/|$)") || !string.IsNullOrEmpty(policy.Endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(policy.ExpectedBuildId) || string.IsNullOrWhiteSpace(policy.RequiredProtocol) ||
            policy.Profile is not ("Player" or "Testing" or "Debug") || string.IsNullOrWhiteSpace(policy.Clock) ||
            !string.IsNullOrEmpty(policy.Endpoint.Query) || !string.IsNullOrEmpty(policy.Endpoint.Fragment) ||
            policy.Endpoint.OriginalString.Length > 4096 || policy.ExpectedBuildId.Length > 128 ||
            policy.RequiredProtocol.Length > 128 || policy.Clock.Length > 128 ||
            policy.Capabilities is null || policy.Capabilities.Count > 1000 || policy.Capabilities.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128) ||
            (policy.HostMode == HostMode.Launch) != (policy.Launch is not null) || policy.ConnectAttempts is < 1 or > 100)
            throw new ArgumentException("PreparationPolicyInvalid", nameof(policy));
        foreach (var duration in new[] { policy.OperationTimeout, policy.RetryDelay, policy.ShutdownTimeout })
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentException("PreparationLimitInvalid", nameof(policy));
        this.policy = policy with { Capabilities = policy.Capabilities.ToArray(),
            Launch = policy.Launch is null ? null : policy.Launch with { Arguments = policy.Launch.Arguments.ToArray() } };
        this.launcher = launcher; this.connector = connector; this.trace = trace;
    }

    public async ValueTask<PreparedHost> PrepareAsync(RunSession run, OwnedCleanup cleanup,
        IApprovedSetup? setup, IPreparationPlannerCheck? planner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(cleanup);
        if (run.State != Gua.Playtest.Core.Contracts.ExecutionState.Preparing) throw new InvalidOperationException("PreparationStateInvalid");
        var preparationDeadline = run.NextRealEvaluationAt;
        if (used) throw new InvalidOperationException("PreparationAlreadyUsed");
        used = true;
        preparationClock = run.AuthoritativeRealClock;
        recordException = run.RecordException;
        trace.Record(new(PreparationStage.Started, PreparationCode.Started));
        // This prevents collisions in this Runner process only, never claims a host/manual-input lifecycle lock.
        var key = policy.Endpoint.AbsoluteUri;
        lock (LeaseLock)
        {
            if (!ActiveEndpoints.Add(key)) Fail(PreparationStage.Ownership, PreparationCode.Busy);
        }
        try
        {
        IOwnedProcess? process = null;
        if (policy.HostMode == HostMode.Launch)
        {
            process = await Step(PreparationStage.Launch, preparationDeadline, async token =>
            {
                var owned = await launcher.LaunchAsync(policy.Launch!, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    await ReleaseUnregisteredAsync(owned.ShutdownAsync).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                try { RegisterRelease(cleanup, releaseToken => FiniteOperation.RunAsync(releaseClock,
                    policy.ShutdownTimeout, owned.ShutdownAsync, releaseToken)); }
                catch
                {
                    await ReleaseUnregisteredAsync(owned.ShutdownAsync).ConfigureAwait(false);
                    throw;
                }
                return owned;
            }, cancellationToken).ConfigureAwait(false);
        }
        IPreparationConnection? connection = null;
        for (var attempt = 0; attempt < policy.ConnectAttempts; attempt++)
        {
            if (process?.HasExited == true) Fail(PreparationStage.Launch, PreparationCode.ProcessExited);
            try
            {
                connection = await Step(PreparationStage.Connect, preparationDeadline, async token =>
                {
                    var acquired = await connector.ConnectAsync(policy.Endpoint, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        await ReleaseUnregisteredAsync(acquired.ReleaseAsync).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                    try { RegisterRelease(cleanup, acquired.ReleaseAsync, CleanupStage.InputRelease); }
                    catch
                    {
                        await ReleaseUnregisteredAsync(acquired.ReleaseAsync).ConfigureAwait(false);
                        throw;
                    }
                    return acquired;
                }, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (ConnectionNotReadyException) when (attempt + 1 < policy.ConnectAttempts)
            {
                await Step(PreparationStage.RetryDelay, preparationDeadline, async token =>
                {
                    var target = preparationClock.Elapsed + policy.RetryDelay;
                    await preparationClock.DelayAsync(policy.RetryDelay, token).ConfigureAwait(false);
                    var remaining = target - preparationClock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        // A provider wake is a hint. A bounded physical interval prevents a stalled
                        // or early-waking provider from spinning or dispatching another connection early.
                        await FiniteOperation.DelayIndependentAsync(remaining, token).ConfigureAwait(false);
                        if (preparationClock.Elapsed < target)
                            throw new ClockProviderException(new InvalidOperationException("RetryClockDidNotAdvance"));
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectionNotReadyException) { Fail(PreparationStage.Connect, PreparationCode.ConnectionFailed); }
        }
        var connected = connection!;
        var identity = await Step(PreparationStage.Identity, preparationDeadline, connected.IdentifyAsync, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(identity);
        if (setup is not null)
        {
            if (!setup.IsAuthorized(policy.HostMode)) Fail(PreparationStage.Setup, PreparationCode.SetupForbidden);
            var sourceOperations = setup.OperationIds;
            var allowedOperations = setup.AllowedOperationIds;
            if (sourceOperations is null || allowedOperations is null || setup.MaximumOperations is < 1 or > 1000 || sourceOperations.Count > setup.MaximumOperations ||
                setup.Timeout <= TimeSpan.Zero || setup.Timeout > TimeSpan.FromDays(1) ||
                sourceOperations.Count < 0)
                Fail(PreparationStage.Setup, PreparationCode.SetupForbidden);
            var operations = new string[sourceOperations.Count];
            for (var index = 0; index < operations.Length; index++)
            {
                var id = sourceOperations[index];
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !allowedOperations.Contains(id))
                    Fail(PreparationStage.Setup, PreparationCode.SetupForbidden);
                operations[index] = id;
            }
            var setupDeadline = Min(preparationDeadline, preparationClock.Elapsed + setup.Timeout);
            for (var index = 0; index < operations.Length; index++)
            {
                var currentOperations = setup.OperationIds; var currentAllowed = setup.AllowedOperationIds;
                if (!setup.IsAuthorized(policy.HostMode) || currentOperations is null || currentAllowed is null || currentOperations.Count != operations.Length ||
                    currentOperations[index] != operations[index] || !currentAllowed.Contains(operations[index]))
                    Fail(PreparationStage.Setup, PreparationCode.SetupForbidden);
                var receipt = await Step(PreparationStage.Setup, setupDeadline,
                    token => setup.ExecuteOperationAsync(index, connected, token), cancellationToken).ConfigureAwait(false);
                if (receipt != SetupReceipt.Confirmed)
                    Fail(PreparationStage.Setup, receipt == SetupReceipt.Failed ? PreparationCode.SetupFailed : PreparationCode.SetupUnconfirmed);
            }
            // Approved scene/save setup can establish a new epoch. Verify it rather than assuming old identity survived.
            identity = await Step(PreparationStage.Identity, preparationDeadline, connected.IdentifyAsync, cancellationToken).ConfigureAwait(false);
            ValidateIdentity(identity);
        }
        if (policy.PlayMode == PlayMode.Explore)
        {
            if (planner is null || !await Step(PreparationStage.Planner, preparationDeadline, planner.CheckAsync, cancellationToken).ConfigureAwait(false))
                Fail(PreparationStage.Planner, PreparationCode.PlannerUnavailable);
        }
        // Arm only after every readiness check. The provider must create a NEW synchronized capture for this request.
        var capture = run.ArmRunningBoundary();
        var boundary = await Step(PreparationStage.Synchronize, preparationDeadline,
            token => connected.SynchronizeAsync(capture.RequestId, token), cancellationToken).ConfigureAwait(false);
        if (boundary is null || boundary.Feed is null) Fail(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed);
        ValidateIdentity(boundary.CapturedIdentity);
        if (!boundary.Continuous || boundary.CapturedIdentity.SourceId != identity.SourceId || boundary.CapturedIdentity.Epoch != identity.Epoch)
            Fail(PreparationStage.Synchronize, PreparationCode.StaleObservation);
        if (!boundary.PreconditionsSatisfied) Fail(PreparationStage.Preconditions, PreparationCode.PreconditionsUnsatisfied);
        if (process?.HasExited == true) Fail(PreparationStage.Launch, PreparationCode.ProcessExited);
        CheckDeadline(preparationDeadline); cancellationToken.ThrowIfCancellationRequested();
        RunStartBoundary certificate;
        try
        {
            certificate = capture.Certify(boundary.CaptureRequestId, boundary.RealCapturedAt, boundary.Observation,
                boundary.SynchronizationEvidence, boundary.PreconditionsSatisfied);
        }
        catch (InvalidOperationException) { Fail(PreparationStage.Synchronize, PreparationCode.StaleObservation); throw; }
        trace.Record(new(PreparationStage.Ready, PreparationCode.Completed));
        var feed = process is null ? boundary.Feed : new ProcessObservationFeed(boundary.Feed, process, trace);
        return new(certificate, feed, boundary.CurrentRestorable);
        }
        finally
        {
            // Register last even on partial preparation failure: no second Run enters during resource shutdown.
            try { cleanup.Register(CleanupStage.ResourceRelease, _ =>
            {
                // Unknown release preserves the local exclusion; a new Run cannot reclaim an uncertain owner.
                if (Volatile.Read(ref pendingReleases) != 0 || Volatile.Read(ref pendingAcquisitions) != 0) return ValueTask.FromResult(false);
                lock (LeaseLock) ActiveEndpoints.Remove(key); return ValueTask.FromResult(true);
            }); }
            catch
            {
                if (Volatile.Read(ref pendingReleases) == 0 && Volatile.Read(ref pendingAcquisitions) == 0)
                { lock (LeaseLock) ActiveEndpoints.Remove(key); }
                throw;
            }
        }
    }

    private void ValidateIdentity(HostIdentity identity)
    {
        if (identity is null || identity.Protocol != policy.RequiredProtocol || identity.Profile != policy.Profile || identity.Clock != policy.Clock ||
            string.IsNullOrWhiteSpace(identity.SourceId) || string.IsNullOrWhiteSpace(identity.Epoch) ||
            (policy.RequireBuildAttestation && identity.AttestedGameBuildId is null) ||
            (identity.AttestedGameBuildId is not null && identity.AttestedGameBuildId != policy.ExpectedBuildId))
            Fail(PreparationStage.Identity, PreparationCode.IdentityMismatch);
        if (identity.Capabilities is null || !policy.Capabilities.All(identity.Capabilities.Contains)) Fail(PreparationStage.Identity, PreparationCode.CapabilityUnavailable);
        // Do not clear/reset pending input to conceal an invalid Strict start.
        if (policy.StrictStart && identity.HasOutstandingRequests) Fail(PreparationStage.Identity, PreparationCode.OutstandingRequests);
    }

    private void RegisterRelease(OwnedCleanup cleanup, Func<CancellationToken, ValueTask<bool>> release,
        CleanupStage stage = CleanupStage.ResourceRelease)
    {
        Interlocked.Increment(ref pendingReleases);
        try { cleanup.Register(stage, async token =>
        {
            var confirmed = await release(token).ConfigureAwait(false);
            if (confirmed) Interlocked.Decrement(ref pendingReleases);
            return confirmed;
        }); }
        catch { Interlocked.Decrement(ref pendingReleases); throw; }
    }

    private async ValueTask ReleaseUnregisteredAsync(Func<CancellationToken, ValueTask<bool>> release)
    {
        // A failed self-release is still an owned outstanding resource, even though cleanup registration closed.
        Interlocked.Increment(ref pendingReleases);
        if (await FiniteOperation.RunAsync(releaseClock, policy.ShutdownTimeout, release).ConfigureAwait(false))
            Interlocked.Decrement(ref pendingReleases);
    }

    private async ValueTask<T> Step<T>(PreparationStage stage, TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            var acquisition = stage is PreparationStage.Launch or PreparationStage.Connect;
            var operationDeadline = stage == PreparationStage.RetryDelay ? deadline : Min(deadline, preparationClock.Elapsed + policy.OperationTimeout);
            var result = await FiniteOperation.RunUntilAsync(preparationClock, operationDeadline, async token =>
            {
                if (preparationClock.Elapsed >= deadline) throw FiniteOperation.DeadlineReached("PreparationDeadlineReached");
                if (acquisition) Interlocked.Increment(ref pendingAcquisitions);
                try { return await action(token).ConfigureAwait(false); }
                finally { if (acquisition) Interlocked.Decrement(ref pendingAcquisitions); }
            }, cancellationToken, recordException).ConfigureAwait(false);
            trace.Record(new(stage, PreparationCode.Completed)); return result;
        }
        catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception)) { trace.Record(new(stage, PreparationCode.Timeout)); throw; }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
        { trace.Record(new(stage, PreparationCode.Cancelled)); throw; }
        catch (ConnectionNotReadyException) when (stage == PreparationStage.Connect) { throw; }
        catch (PreparationException) { throw; }
        catch (ClockProviderException) { throw; }
        catch (Exception exception)
        {
            var code = stage switch
            {
                PreparationStage.Launch => PreparationCode.LaunchFailed,
                PreparationStage.Connect => PreparationCode.ConnectionFailed,
                PreparationStage.Identity => PreparationCode.IdentityUnavailable,
                PreparationStage.Planner => PreparationCode.PlannerUnavailable,
                PreparationStage.Synchronize => PreparationCode.SynchronizationFailed,
                _ => PreparationCode.SetupFailed
            };
            trace.Record(new(stage, code)); throw new PreparationException(stage, code,
                exception is ProviderCancellationException ? exception.InnerException : exception);
        }
    }
    private void CheckDeadline(TimeSpan deadline)
    { if (preparationClock.Elapsed >= deadline) { trace.Record(new(PreparationStage.Started, PreparationCode.Timeout)); throw FiniteOperation.DeadlineReached("PreparationDeadlineReached"); } }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(PreparationStage stage, PreparationCode code)
    { trace.Record(new(stage, code)); throw new PreparationException(stage, code); }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
