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
    private Action<Exception>? recordProviderFailure;
    private readonly IProcessLauncher launcher;
    private readonly IPreparationConnector connector;
    private readonly IPreparationTrace trace;
    private readonly SemaphoreSlim traceGate = new(1, 1);
    private readonly List<Task> executionTraceWrites = [];
    private readonly object executionTraceGate = new();
    private TimeSpan preparationDeadline;
    private CancellationToken preparationCancellation;
    private int used;
    private int pendingReleases;
    private int pendingAcquisitions;
    private int pendingPreparationWork;
    private bool ownershipClosed;
    private IOwnedProcess? preparationProcess;
    private bool executionTraceOverflow;
    private bool diagnosticRegistrationClosed;
    private Task[] closedDiagnosticWrites = [];
    private bool diagnosticPreparationIncomplete;
    private int preparationInFlight;
    private Func<RunFailureException?>? initialLifecycleFailure;
    public HostPreparation(PreparationPolicy policy, IClock clock, IProcessLauncher launcher,
        IPreparationConnector connector, IPreparationTrace trace)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(launcher); ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(trace);
        if (!Enum.IsDefined(policy.HostMode) || !Enum.IsDefined(policy.PlayMode) || policy.Endpoint is null || !policy.Endpoint.IsAbsoluteUri ||
            policy.Endpoint.Scheme is not ("ws" or "wss") || policy.Endpoint.Port <= 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(policy.Endpoint.OriginalString, @"^wss?://(?:\[[^\]]+\]|[^/:]+):[0-9]+(?:/|$)") || !string.IsNullOrEmpty(policy.Endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(policy.ExpectedBuildId) || string.IsNullOrWhiteSpace(policy.RequiredProtocol) ||
            policy.Profile is not ("Player" or "Testing" or "Debug") || string.IsNullOrWhiteSpace(policy.Clock) ||
            !string.IsNullOrEmpty(policy.Endpoint.Query) || !string.IsNullOrEmpty(policy.Endpoint.Fragment) || policy.Launch is { Arguments: null } ||
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
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("PreparationAlreadyUsed");
        Interlocked.Exchange(ref preparationInFlight, 1);
        try { return await PrepareCoreAsync(run, cleanup, setup, planner, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref preparationInFlight, 0); }
    }
    private async ValueTask<PreparedHost> PrepareCoreAsync(RunSession run, OwnedCleanup cleanup,
        IApprovedSetup? setup, IPreparationPlannerCheck? planner, CancellationToken cancellationToken)
    {
        preparationDeadline = run.NextRealEvaluationAt;
        preparationCancellation = cancellationToken;
        preparationClock = run.AuthoritativeRealClock;
        recordException = run.PostException;
        recordProviderFailure = run.PostProviderException;
        // Closing this registration gate is owed even if the Diagnostics stage
        // cannot be invoked after a blocked primary snapshot consumes cleanup time.
        cleanup.Register(CleanupStage.OwnershipRelease, _ => ValueTask.FromResult(true), CloseDiagnosticRegistration);
        cleanup.Register(CleanupStage.Diagnostics, async _ =>
        {
            CloseDiagnosticRegistration();
            Task[] writes; lock (executionTraceGate) writes = closedDiagnosticWrites;
            await Task.WhenAll(writes).ConfigureAwait(false);
            lock (executionTraceGate) return !executionTraceOverflow && !diagnosticPreparationIncomplete;
        });
        await TraceAsync(new(PreparationStage.Started, PreparationCode.Started), required: true).ConfigureAwait(false);
        // This prevents collisions in this Runner process only, never claims a host/manual-input lifecycle lock.
        var key = policy.Endpoint.AbsoluteUri;
        bool acquiredLease;
        lock (LeaseLock) acquiredLease = ActiveEndpoints.Add(key);
        if (!acquiredLease) await FailAsync(PreparationStage.Ownership, PreparationCode.Busy);
        // Acquire cleanup authority before the first await can race the executor deadline.
        try { cleanup.Register(CleanupStage.OwnershipRelease, _ =>
        {
            lock (LeaseLock)
            {
                ownershipClosed = true;
                if (Volatile.Read(ref pendingReleases) != 0 || Volatile.Read(ref pendingAcquisitions) != 0 || Volatile.Read(ref pendingPreparationWork) != 0)
                    return ValueTask.FromResult(false);
                ActiveEndpoints.Remove(key); return ValueTask.FromResult(true);
            }
        }, () => { lock (LeaseLock) ownershipClosed = true; }); }
        catch
        {
            // No host/provider work can have started before this initial registration.
            lock (LeaseLock) { ownershipClosed = true; ActiveEndpoints.Remove(key); }
            throw;
        }
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
                // One continuous exact-handle watch survives the final preparation
                // trace and is sampled by the initial Running arbitration owner.
                var lifetimeCancellation = new CancellationTokenSource();
                try { RegisterRelease(cleanup, _ =>
                {
                    var faults = new List<Exception>();
                    try { FiniteOperation.CancelSafely(lifetimeCancellation, faults.Add); }
                    finally { lifetimeCancellation.Dispose(); }
                    if (faults.Count != 0) return ValueTask.FromException<bool>(new AggregateException(faults));
                    return ValueTask.FromResult(true);
                }); }
                catch { lifetimeCancellation.Dispose(); throw; }
                Task lifetime;
                try { lifetime = owned.WaitForExitAsync(lifetimeCancellation.Token).AsTask(); }
                catch (Exception exception) { lifetime = Task.FromException(exception); }
                _ = lifetime.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                initialLifecycleFailure = () =>
                {
                    if (!lifetime.IsCompleted) return null;
                    RunFailureException failure;
                    if (lifetime.IsCompletedSuccessfully)
                        failure = new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
                    else
                    {
                        try { lifetime.GetAwaiter().GetResult(); return null; }
                        catch (Exception exception) { failure = exception as RunFailureException ?? new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, exception, RunPhase.Execution); }
                    }
                    RecordDiagnosticTrace(new(PreparationStage.Launch, failure is PreparationException preparation ? preparation.Code : PreparationCode.LaunchFailed));
                    return failure;
                };
                preparationProcess = owned; return owned;
            }, cancellationToken).ConfigureAwait(false);
        }
        IPreparationConnection? connection = null;
        for (var attempt = 0; attempt < policy.ConnectAttempts; attempt++)
        {
            if (process is not null && await ReadProcessStatusAsync(process, RunPhase.Preparation, cancellationToken).ConfigureAwait(false)) await FailAsync(PreparationStage.Launch, PreparationCode.ProcessExited);
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
                await Step(PreparationStage.RetryDelay, preparationDeadline,
                    RetryDelayAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectionNotReadyException) { await FailAsync(PreparationStage.Connect, PreparationCode.ConnectionFailed); }
        }
        var connected = connection!;
        var identity = await Step(PreparationStage.Identity, preparationDeadline, connected.IdentifyAsync, cancellationToken).ConfigureAwait(false);
        await ValidateIdentityAsync(identity, preparationDeadline, cancellationToken).ConfigureAwait(false);
        if (setup is not null)
        {
            var setupStartedAt = preparationClock.Elapsed;
            var authority = await ReadSetupAsync(setup, preparationDeadline, cancellationToken).ConfigureAwait(false);
            var operations = authority.Operations;
            var setupDeadline = Min(preparationDeadline, setupStartedAt + authority.Timeout);
            if (preparationClock.Elapsed >= setupDeadline)
            {
                RecordDiagnosticTrace(new(PreparationStage.Setup, PreparationCode.Timeout));
                throw FiniteOperation.DeadlineReached("SetupDeadlineReached");
            }
            for (var index = 0; index < operations.Length; index++)
            {
                var current = await ReadSetupAsync(setup, setupDeadline, cancellationToken).ConfigureAwait(false);
                setupDeadline = Min(setupDeadline, setupStartedAt + current.Timeout);
                if (current.Operations.Length != operations.Length || current.Operations[index] != operations[index])
                    await FailAsync(PreparationStage.Setup, PreparationCode.SetupForbidden);
                var receipt = await Step(PreparationStage.Setup, setupDeadline,
                    token => setup.ExecuteOperationAsync(index, connected, token), cancellationToken).ConfigureAwait(false);
                if (receipt != SetupReceipt.Confirmed)
                    await FailAsync(PreparationStage.Setup, receipt == SetupReceipt.Failed ? PreparationCode.SetupFailed : PreparationCode.SetupUnconfirmed);
            }
            // Approved scene/save setup can establish a new epoch. Verify it rather than assuming old identity survived.
            identity = await Step(PreparationStage.Identity, preparationDeadline, connected.IdentifyAsync, cancellationToken).ConfigureAwait(false);
            await ValidateIdentityAsync(identity, preparationDeadline, cancellationToken).ConfigureAwait(false);
        }
        if (policy.PlayMode == PlayMode.Explore)
        {
            if (planner is null || !await Step(PreparationStage.Planner, preparationDeadline, planner.CheckAsync, cancellationToken).ConfigureAwait(false))
                await FailAsync(PreparationStage.Planner, PreparationCode.PlannerUnavailable);
        }
        // Arm only after every readiness check. The provider must create a NEW synchronized capture for this request.
        var capture = run.ArmRunningBoundary();
        var boundary = await Step(PreparationStage.Synchronize, preparationDeadline,
            token => connected.SynchronizeAsync(capture.RequestId, token), cancellationToken).ConfigureAwait(false);
        if (boundary is null || boundary.Feed is null) { await FailAsync(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed); throw new InvalidOperationException(); }
        await ValidateIdentityAsync(boundary.CapturedIdentity, preparationDeadline, cancellationToken).ConfigureAwait(false);
        if (!boundary.Continuous || boundary.CapturedIdentity.SourceId != identity.SourceId || boundary.CapturedIdentity.Epoch != identity.Epoch)
            await FailAsync(PreparationStage.Synchronize, PreparationCode.StaleObservation);
        if (!boundary.PreconditionsSatisfied) await FailAsync(PreparationStage.Preconditions, PreparationCode.PreconditionsUnsatisfied);
        if (process is not null && await ReadProcessStatusAsync(process, RunPhase.Preparation, cancellationToken).ConfigureAwait(false)) await FailAsync(PreparationStage.Launch, PreparationCode.ProcessExited);
        await CheckDeadlineAsync(preparationDeadline); cancellationToken.ThrowIfCancellationRequested();
        RunStartBoundary certificate;
        try
        {
            certificate = capture.Certify(boundary.CaptureRequestId, boundary.RealCapturedAt, boundary.Observation,
                boundary.SynchronizationEvidence, boundary.PreconditionsSatisfied);
        }
        catch (InvalidOperationException exception) when (!run.HasPendingClockRejection)
        {
            RecordDiagnosticTrace(new(PreparationStage.Synchronize, PreparationCode.StaleObservation));
            throw new PreparationException(PreparationStage.Synchronize, PreparationCode.StaleObservation, exception);
        }
        await TraceAsync(new(PreparationStage.Ready, PreparationCode.Completed), required: true).ConfigureAwait(false);
        certificate.InitialLifecycleFailure = initialLifecycleFailure;
        var feed = new ProcessObservationFeed(boundary.Feed, process, token => process is null ? ValueTask.FromResult(false) : ReadProcessStatusAsync(process, RunPhase.Execution, token),
            RecordDiagnosticTrace, run.PostException, run.PostProviderException);
        return new(certificate, feed, boundary.CurrentRestorable);
    }

    private void CloseDiagnosticRegistration()
    {
        lock (executionTraceGate)
        {
            if (diagnosticRegistrationClosed) return;
            diagnosticRegistrationClosed = true;
            closedDiagnosticWrites = executionTraceWrites.ToArray();
            diagnosticPreparationIncomplete = Volatile.Read(ref preparationInFlight) != 0;
        }
    }
    private void RecordDiagnosticTrace(PreparationEvent evidence)
    {
        // Diagnostic persistence cannot delay any ready authoritative rejection/failure.
        // Cleanup's bounded Diagnostics stage joins the finite writes before final output.
        lock (executionTraceGate)
        {
            if (diagnosticRegistrationClosed) { executionTraceOverflow = true; return; }
            if (executionTraceWrites.Count >= 1000) { executionTraceOverflow = true; return; }
            var write = TraceAsync(evidence, retainForCleanup: true).AsTask();
            executionTraceWrites.Add(write);
            _ = write.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private async ValueTask<bool> ReadProcessStatusAsync(IOwnedProcess process, RunPhase phase, CancellationToken token)
    {
        if (phase == RunPhase.Preparation)
            return await ReadPureAsync(PreparationStage.Launch, preparationDeadline, _ => process.HasExited, token).ConfigureAwait(false);
        try
        {
            return await FiniteOperation.RunAsync<bool>(releaseClock, policy.OperationTimeout,
                cancellation => new(Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return process.HasExited; }, cancellation)), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, exception, phase); }
    }

    private async ValueTask<T> WatchPreparationAsync<T>(Task<T> work, CancellationToken token)
    {
        var process = preparationProcess;
        if (process is null) return await work.ConfigureAwait(false);
        using var exitCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? exited = null;
        var exitFailureRetained = false;
        try
        {
            try { exited = process.WaitForExitAsync(exitCancellation.Token).AsTask(); }
            catch (Exception exception) { exited = Task.FromException(exception); }
            await Task.WhenAny(work, exited).ConfigureAwait(false);
            // Ready clock/provider faults are evidence before any new status probe.
            // No getter is needed: the exact-handle successful watch certifies exit.
            var ready = new List<Exception>();
            if (exited.IsCompletedSuccessfully) ready.Add(new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited));
            AddFault(work, launch: false); AddFault(exited, launch: true);
            if (ready.Count != 0)
            {
                RunEvent Cause(Exception exception) => exception switch
                {
                    ClockProviderException => new(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Clock),
                    RunFailureException typed => typed.Cause,
                    OperationCanceledException when token.IsCancellationRequested => new(RunReason.Cancelled, RunPhase.Preparation, RunOrigin.User),
                    _ => new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)
                };
                var chosen = ready.OrderBy(exception => RunSession.Priority(Cause(exception).Reason))
                    .ThenBy(exception => Cause(exception).Reason).ThenBy(exception => Cause(exception).Phase)
                    .ThenBy(exception => Cause(exception).Origin).First();
                foreach (var exception in ready) if (!ReferenceEquals(exception, chosen)) recordException?.Invoke(exception);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(chosen).Throw();
            }
            return await work.ConfigureAwait(false);
            void AddFault(Task task, bool launch)
            {
                if (!task.IsFaulted && !task.IsCanceled) return;
                try { task.GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    if (launch) exitFailureRetained = true;
                    if (launch && exception is OperationCanceledException cancelled && token.IsCancellationRequested &&
                        cancelled.CancellationToken == exitCancellation.Token)
                        exception = new OperationCanceledException("PreparationExitWatchCancelled", cancelled, token);
                    if (launch && exception is not RunFailureException &&
                        !(exception is OperationCanceledException && token.IsCancellationRequested))
                        exception = new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, exception);
                    ready.Add(exception);
                }
            }
        }
        finally
        {
            FiniteOperation.CancelSafely(exitCancellation, exception => recordException?.Invoke(exception));
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (exited is not null && !exitFailureRetained)
            {
                var obsoleteToken = exitCancellation.Token;
                _ = exited.ContinueWith(task =>
                {
                    try { task.GetAwaiter().GetResult(); }
                    catch (Exception original)
                    {
                        if (original is OperationCanceledException cancelled && obsoleteToken.IsCancellationRequested &&
                            cancelled.CancellationToken == obsoleteToken) return;
                        var evidence = original as RunFailureException ?? new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, original);
                        RecordDiagnosticTrace(new(PreparationStage.Launch, PreparationCode.LaunchFailed));
                        (recordProviderFailure ?? recordException)?.Invoke(evidence);
                    }
                }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async ValueTask<bool> RetryDelayAsync(CancellationToken token)
    {
        // The same Step lifecycle watch covers backoff, connection, metadata and Setup.
        var target = preparationClock.Elapsed + policy.RetryDelay;
        await preparationClock.DelayAsync(policy.RetryDelay, token).ConfigureAwait(false);
        var remaining = target - preparationClock.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await FiniteOperation.DelayIndependentAsync(remaining, token).ConfigureAwait(false);
            if (preparationClock.Elapsed < target)
                throw new ClockProviderException(new InvalidOperationException("RetryClockDidNotAdvance"));
        }
        return true;
    }

    private sealed record SetupAuthority(string[] Operations, TimeSpan Timeout);
    private ValueTask<SetupAuthority> ReadSetupAsync(IApprovedSetup setup, TimeSpan deadline, CancellationToken cancellationToken)
        => ReadPureAsync(PreparationStage.Setup, deadline, token =>
        {
            // Pure adapter metadata is isolated from the owner continuation. Even a
            // blocking getter cannot prevent its original timer/cancellation from running.
            // A late worker may finish reading, but has no Run, cleanup or dispatch authority.
            if (!setup.IsAuthorized(policy.HostMode)) throw new PreparationException(PreparationStage.Setup, PreparationCode.SetupForbidden);
            var source = setup.OperationIds; var allowed = setup.AllowedOperationIds;
            var maximum = setup.MaximumOperations; var timeout = setup.Timeout;
            if (source is null || allowed is null || maximum is < 1 or > 1000 ||
                timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
                throw new PreparationException(PreparationStage.Setup, PreparationCode.SetupForbidden);
            var count = source.Count;
            if (count < 0 || count > maximum) throw new PreparationException(PreparationStage.Setup, PreparationCode.SetupForbidden);
            var operations = new string[count];
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var id = source[index];
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !allowed.Contains(id))
                    throw new PreparationException(PreparationStage.Setup, PreparationCode.SetupForbidden);
                operations[index] = id;
            }
            return new SetupAuthority(operations, timeout);
        }, cancellationToken);

    private ValueTask<bool> ValidateIdentityAsync(HostIdentity identity, TimeSpan deadline, CancellationToken cancellationToken)
        => ReadPureAsync(PreparationStage.Identity, deadline, token =>
    {
        if (identity is null || identity.Protocol != policy.RequiredProtocol || identity.Profile != policy.Profile || identity.Clock != policy.Clock ||
            string.IsNullOrWhiteSpace(identity.SourceId) || string.IsNullOrWhiteSpace(identity.Epoch) ||
            (policy.RequireBuildAttestation && identity.AttestedGameBuildId is null) ||
            (identity.AttestedGameBuildId is not null && identity.AttestedGameBuildId != policy.ExpectedBuildId))
            throw new PreparationException(PreparationStage.Identity, PreparationCode.IdentityMismatch);
        if (identity.Capabilities is null) throw new PreparationException(PreparationStage.Identity, PreparationCode.CapabilityUnavailable);
        foreach (var capability in policy.Capabilities)
        {
            token.ThrowIfCancellationRequested();
            if (!identity.Capabilities.Contains(capability)) throw new PreparationException(PreparationStage.Identity, PreparationCode.CapabilityUnavailable);
        }
        // Do not clear/reset pending input to conceal an invalid Strict start.
        if (policy.StrictStart && identity.HasOutstandingRequests) throw new PreparationException(PreparationStage.Identity, PreparationCode.OutstandingRequests);
        return true;
    }, cancellationToken);

    private ValueTask<T> ReadPureAsync<T>(PreparationStage stage, TimeSpan deadline,
        Func<CancellationToken, T> read, CancellationToken cancellationToken)
        => Step<T>(stage, deadline, token => new(Task.Run(() => read(token), token)), cancellationToken);

    private void BeginOwnedWork(bool acquisition, bool tracked, CancellationToken token)
    {
        // No external callbacks inside the lease gate. A terminal confirmation and
        // a provider start are mutually exclusive even if cancellation arrives in between.
        lock (LeaseLock)
        {
            token.ThrowIfCancellationRequested();
            if (ownershipClosed) throw new InvalidOperationException("PreparationOwnershipClosed");
            if (tracked)
            {
                if (acquisition) Interlocked.Increment(ref pendingAcquisitions);
                else Interlocked.Increment(ref pendingPreparationWork);
            }
        }
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
            cancellationToken.ThrowIfCancellationRequested();
            var acquisition = stage is PreparationStage.Launch or PreparationStage.Connect;
            // Only certified no-effect backoff may outlive the lease. All provider work,
            // including Setup effects and reads, retains exclusion until it actually ends.
            var tracked = stage != PreparationStage.RetryDelay;
            var operationDeadline = stage == PreparationStage.RetryDelay ? deadline : Min(deadline, preparationClock.Elapsed + policy.OperationTimeout);
            var result = await FiniteOperation.RunUntilAsync(preparationClock, operationDeadline, async token =>
            {
                if (preparationClock.Elapsed >= deadline) throw FiniteOperation.DeadlineReached("PreparationDeadlineReached");
                BeginOwnedWork(acquisition, tracked, token);
                async Task<T> ExecuteOwnedAsync()
                {
                    try { return await action(token).ConfigureAwait(false); }
                    finally
                    {
                        if (tracked)
                        {
                            if (acquisition) Interlocked.Decrement(ref pendingAcquisitions);
                            else Interlocked.Decrement(ref pendingPreparationWork);
                        }
                    }
                }
                return await WatchPreparationAsync(ExecuteOwnedAsync(), token).ConfigureAwait(false);
            }, cancellationToken, recordException).ConfigureAwait(false);
            await TraceAsync(new(stage, PreparationCode.Completed), required: true).ConfigureAwait(false); return result;
        }
        catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception)) { RecordDiagnosticTrace(new(stage, PreparationCode.Timeout)); throw; }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
        { RecordDiagnosticTrace(new(stage, PreparationCode.Cancelled)); throw; }
        catch (ConnectionNotReadyException) when (stage == PreparationStage.Connect) { throw; }
        catch (PreparationException exception) { RecordDiagnosticTrace(new(exception.Stage, exception.Code)); throw; }
        catch (RunFailureException) { throw; }
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
            RecordDiagnosticTrace(new(stage, code)); throw new PreparationException(stage, code,
                exception is ProviderCancellationException ? exception.InnerException : exception);
        }
    }
    private ValueTask CheckDeadlineAsync(TimeSpan deadline)
    { if (preparationClock.Elapsed >= deadline) { RecordDiagnosticTrace(new(PreparationStage.Started, PreparationCode.Timeout)); throw FiniteOperation.DeadlineReached("PreparationDeadlineReached"); } return ValueTask.CompletedTask; }
    private ValueTask FailAsync(PreparationStage stage, PreparationCode code)
    { RecordDiagnosticTrace(new(stage, code)); return ValueTask.FromException(new PreparationException(stage, code)); }

    private async ValueTask TraceAsync(PreparationEvent evidence, bool required = false, bool retainForCleanup = false)
    {
        // One worker per sink: a blocked write cannot spawn replacement writes. Error
        // diagnostics have an independent explicit ceiling and cannot replace their cause.
        try
        {
            var clock = required ? preparationClock : releaseClock;
            var deadline = required ? Min(preparationDeadline, clock.Elapsed + policy.OperationTimeout)
                : clock.Elapsed + policy.OperationTimeout;
            await FiniteOperation.RunUntilAsync<bool>(clock, deadline, token =>
            {
                var write = Task.Run(async () =>
                {
                    await traceGate.WaitAsync(token).ConfigureAwait(false);
                    try { token.ThrowIfCancellationRequested(); trace.Record(evidence); return true; }
                    finally { traceGate.Release(); }
                }, token);
                return new(required ? WatchPreparationAsync(write, token).AsTask() : write);
            }, required ? preparationCancellation : CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (retainForCleanup)
            {
                // This trace deadline is provider evidence for cleanup, not proof that
                // the cleanup owner deadline elapsed. Do not export its provenance.
                if (FiniteOperation.IsDeadline(exception)) throw new TimeoutException("TraceWriteTimeout", exception);
                throw;
            }
            if (!required) { recordException?.Invoke(exception); return; }
            if (exception is RunFailureException or ClockProviderException || FiniteOperation.IsDeadline(exception) ||
                exception is OperationCanceledException && preparationCancellation.IsCancellationRequested) throw;
            throw new PreparationException(evidence.Stage, PreparationCode.TraceUnavailable, exception);
        }
    }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
