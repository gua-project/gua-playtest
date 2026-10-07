using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Core.Assertions;
using System.Text.Json.Nodes;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class PreparationTests
{
    private sealed class Clock : IClock
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        private TimeSpan elapsed;
        public Action? OnRead { get; set; }
        public TimeSpan Elapsed { get { OnRead?.Invoke(); return elapsed; } private set => elapsed = value; }
        public Action<TimeSpan>? OnDelay { get; set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration == TimeSpan.FromMilliseconds(1)) { token.ThrowIfCancellationRequested(); Advance(1); return ValueTask.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            lock (timers) timers.Add((Elapsed + duration, completion));
            OnDelay?.Invoke(duration);
            return new(completion.Task);
        }
        public void Advance(int milliseconds)
        {
            Elapsed += TimeSpan.FromMilliseconds(milliseconds);
            lock (timers)
                foreach (var timer in timers.Where(timer => timer.Due <= Elapsed).ToArray()) timer.Completion.TrySetResult();
        }
    }
    private sealed class Trace : IPreparationTrace
    {
        private readonly object gate = new();
        private readonly List<PreparationEvent> events = [];
        public IReadOnlyList<PreparationEvent> Events { get { lock (gate) return events.ToArray(); } }
        public Action<PreparationEvent>? OnRecord { get; set; }
        public void Record(PreparationEvent evidence) { lock (gate) events.Add(evidence); OnRecord?.Invoke(evidence); }
    }
    private sealed class Process : IOwnedProcess
    {
        private bool hasExited;
        public Action? OnStatus { get; set; }
        public bool HasExited { get { OnStatus?.Invoke(); return hasExited; } set => hasExited = value; }
        public int Shutdowns { get; private set; }
        public Action<CancellationToken>? OnExitWait { get; set; }
        public Exception? ExitWaitFailure { get; set; }
        public Func<CancellationToken, Task>? ExitWatchTask { get; set; }
        public bool DirectExitWatch { get; set; }
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Exit() { HasExited = true; exited.TrySetResult(); }
        public void ConfirmExitWithStaleStatus() => exited.TrySetResult();
        public void FailExit(Exception exception) => exited.TrySetException(exception);
        public ValueTask WaitForExitAsync(CancellationToken token)
        { OnExitWait?.Invoke(token); return ExitWaitFailure is null ? new(ExitWatchTask?.Invoke(token) ?? (DirectExitWatch ? exited.Task : exited.Task.WaitAsync(token))) : ValueTask.FromException(ExitWaitFailure); }
        public ValueTask<bool> ShutdownAsync(CancellationToken token)
        { Shutdowns++; Exit(); Released.TrySetResult(); return ValueTask.FromResult(true); }
    }
    private sealed class Launcher : IProcessLauncher
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Exception? FailureException { get; set; }
        public Process Process { get; } = new();
        public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token)
        { Calls++; if (FailureException is not null) throw FailureException; if (Fail) throw new IOException("private-path"); return ValueTask.FromResult<IOwnedProcess>(Process); }
    }
    private sealed class DelayedLauncher : IProcessLauncher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IOwnedProcess> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token) { Started.TrySetResult(); return new(Completion.Task); }
    }
    private sealed class Connection : IPreparationConnection, IRunObservationFeed
    {
        public IClock? Clock { get; set; }
        public HostIdentity Identity { get; set; } = new("game", "1", "Testing", "real", new HashSet<string> { "observe" }, "source", "epoch", false);
        public bool Stale { get; set; }
        public bool Preconditions { get; set; } = true;
        public bool WrongRequest { get; set; }
        public bool MissingCapturedCapabilities { get; set; }
        public IReadOnlySet<string>? CapturedCapabilities { get; set; }
        public bool MissingBoundary { get; set; }
        public bool MissingFeed { get; set; }
        public Exception? CaptureFailure { get; set; }
        public Exception? IdentityFailure { get; set; }
        public TaskCompletionSource<HostIdentity>? PendingIdentity { get; set; }
        public TaskCompletionSource<InitialBoundary>? PendingBoundary { get; set; }
        public TaskCompletionSource IdentityStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SynchronizationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? SynchronizeFailure { get; set; }
        public RunObservation? InitialObservation { get; set; }
        public RunObservation? CurrentObservation { get; set; }
        public TaskCompletionSource<RunObservation>? BlockedCapture { get; set; }
        public int Captures { get; private set; }
        public Action<CancellationToken>? OnCapture { get; set; }
        public Action<CancellationToken>? OnWait { get; set; }
        public TaskCompletionSource? BlockedWait { get; set; }
        public int Releases { get; private set; }
        public int Synchronizations { get; private set; }
        public bool ReleaseConfirmed { get; set; } = true;
        public Action? OnRelease { get; set; }
        public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token)
        { IdentityStarted.TrySetResult(); if (IdentityFailure is not null) throw IdentityFailure; return PendingIdentity is null ? ValueTask.FromResult(Identity) : new(PendingIdentity.Task); }
        public ValueTask<InitialBoundary> SynchronizeAsync(string captureRequestId, CancellationToken token)
        { if (SynchronizeFailure is not null) throw SynchronizeFailure; Synchronizations++; SynchronizationStarted.TrySetResult(); if (PendingBoundary is not null) return new(PendingBoundary.Task); if (MissingBoundary) return ValueTask.FromResult<InitialBoundary>(null!);
            return ValueTask.FromResult(new InitialBoundary(Identity with { Epoch = Stale ? "old" : Identity.Epoch,
            Capabilities = MissingCapturedCapabilities ? null! : CapturedCapabilities ?? Identity.Capabilities }, true, Preconditions,
            WrongRequest ? "previous-request" : captureRequestId, Clock?.Elapsed ?? TimeSpan.Zero, "subscription-cursor-1", InitialObservation ?? Observation(), MissingFeed ? null! : this, false)); }
        private RunObservation Observation() => CurrentObservation ?? new(Clock?.Elapsed ?? TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([]));
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        { Captures++; OnCapture?.Invoke(token); return CaptureFailure is not null ? ValueTask.FromException<RunObservation>(CaptureFailure) : BlockedCapture is null ? ValueTask.FromResult(Observation()) : new(BlockedCapture.Task); }
        public ValueTask WaitForChangeAsync(CancellationToken token)
        { OnWait?.Invoke(token); return new(BlockedWait?.Task ?? Task.Delay(Timeout.Infinite, token)); }
        public ValueTask<bool> ReleaseAsync(CancellationToken token) { Releases++; OnRelease?.Invoke(); return ValueTask.FromResult(ReleaseConfirmed); }
    }
    private sealed class Connector(Connection connection) : IPreparationConnector
    {
        public int Calls { get; private set; }
        public int NotReadyCount { get; set; }
        public bool Unknown { get; set; }
        public bool ProviderCancelled { get; set; }
        public bool ProviderTimeout { get; set; }
        public Action? BeforeReturn { get; set; }
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token)
        {
            Calls++; if (Calls <= NotReadyCount) throw new ConnectionNotReadyException();
            if (Unknown) throw new IOException("unknown-connect-result");
            if (ProviderCancelled) throw new OperationCanceledException("provider-timeout");
            if (ProviderTimeout) throw new TimeoutException("provider-timeout");
            BeforeReturn?.Invoke(); return ValueTask.FromResult<IPreparationConnection>(connection);
        }
    }
    private sealed class Planner : IPreparationPlannerCheck
    {
        public int Checks { get; private set; }
        public TaskCompletionSource<bool>? PendingCheck { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? FailureException { get; set; }
        public ValueTask<bool> CheckAsync(CancellationToken token) { Checks++; Started.TrySetResult(); if (FailureException is not null) throw FailureException; return PendingCheck is null ? ValueTask.FromResult(true) : new(PendingCheck.Task); }
    }
    private sealed class DelayedConnector : IPreparationConnector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IPreparationConnection> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token) { Started.TrySetResult(); return new(Completion.Task); }
    }
    private sealed class Setup : IApprovedSetup
    {
        public Action? BeforeAuthorization { get; set; }
        public Func<IReadOnlySet<string>>? ReadAllowed { get; set; }
        public Func<int>? ReadMaximum { get; set; }
        public Func<TimeSpan>? ReadTimeout { get; set; }
        public bool Authorized { get; set; } = true;
        private IReadOnlyList<string> operationIds = ["scene"];
        public Func<IReadOnlyList<string>>? ReadOperations { get; set; }
        public IReadOnlyList<string> OperationIds { get => ReadOperations is null ? operationIds : ReadOperations(); set => operationIds = value; }
        private IReadOnlySet<string> allowedOperationIds = new HashSet<string> { "scene" };
        public IReadOnlySet<string> AllowedOperationIds { get => ReadAllowed is null ? allowedOperationIds : ReadAllowed(); set => allowedOperationIds = value; }
        public Exception? OperationFailure { get; set; }
        public TaskCompletionSource<SetupReceipt>? PendingReceipt { get; set; }
        public TaskCompletionSource OperationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int maximumOperations = 1;
        public int MaximumOperations { get => ReadMaximum is null ? maximumOperations : ReadMaximum(); set => maximumOperations = value; }
        public TimeSpan Timeout => ReadTimeout is null ? TimeSpan.FromSeconds(1) : ReadTimeout();
        public int Calls { get; private set; }
        public SetupReceipt Receipt { get; set; } = SetupReceipt.Confirmed;
        public bool IsAuthorized(HostMode mode) { BeforeAuthorization?.Invoke(); return Authorized; }
        public ValueTask<SetupReceipt> ExecuteOperationAsync(int index, IPreparationConnection connection, CancellationToken token)
        { Calls++; OperationStarted.TrySetResult(); if (OperationFailure is not null) throw OperationFailure;
            return PendingReceipt is null ? ValueTask.FromResult(Receipt) : new(PendingReceipt.Task); }
    }
    private static PreparationPolicy Policy(HostMode hostMode = HostMode.Attach, PlayMode playMode = PlayMode.Replay)
        => new(hostMode, playMode, new Uri($"ws://localhost:7777/{Guid.NewGuid():N}"), "game", "1", "Testing", "real", ["observe"], true,
            true, hostMode == HostMode.Launch ? new("explicit.exe", "explicit-directory", []) : null,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), 3, TimeSpan.FromSeconds(1));
    private static RunSession Run(IClock clock) => new(new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 100), clock, clock);
    private static async Task<RunOutcome> Execute(HostPreparation preparation, IClock clock, Setup? setup = null, Planner? planner = null)
    {
        var run = Run(clock); var cleanup = new OwnedCleanup();
        return await RunExecutor.ExecuteAsync(run, clock, cleanup, async (session, owned, token) =>
        { return (await preparation.PrepareAsync(session, owned, setup, planner, token)).Boundary; },
            (_, _) => ValueTask.FromResult(true));
    }
    [Theory]
    [InlineData(HostMode.Attach, 0)]
    [InlineData(HostMode.Launch, 1)]
    public async Task ReplayUsesNoPlannerAndOnlyLaunchOwnsAProcess(HostMode mode, int expected)
    {
        var clock = new Clock(); var connection = new Connection(); var connector = new Connector(connection);
        var launcher = new Launcher(); var trace = new Trace(); var planner = new Planner();
        var outcome = await Execute(new(Policy(mode), clock, launcher, connector, trace), clock, planner: planner);
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status); Assert.True(outcome.PostProcessingComplete);
        Assert.Equal(expected, launcher.Calls); Assert.Equal(expected, launcher.Process.Shutdowns);
        Assert.Equal(0, planner.Checks); Assert.Equal(1, connection.Releases); Assert.Equal(1, connector.Calls);
        Assert.Equal(new(PreparationStage.Started, PreparationCode.Started), trace.Events[0]);
    }
    [Fact]
    public async Task LaunchFailureIsTracedBeforeAnyConnectionAndDoesNotOwnAProcess()
    {
        var clock = new Clock(); var connector = new Connector(new()); var launcher = new Launcher { Fail = true }; var trace = new Trace();
        var outcome = await Execute(new(Policy(HostMode.Launch), clock, launcher, connector, trace), clock);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunPhase.Preparation, outcome.Primary.Cause.Phase);
        Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(outcome.Exceptions, evidence => evidence.Type == typeof(IOException).FullName);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.LaunchFailed), trace.Events);
        Assert.Equal(0, connector.Calls); Assert.Equal(0, launcher.Process.Shutdowns);
    }
    [Theory]
    [InlineData("build", PreparationCode.IdentityMismatch)]
    [InlineData("unattested", PreparationCode.IdentityMismatch)]
    [InlineData("pending", PreparationCode.OutstandingRequests)]
    [InlineData("profile", PreparationCode.IdentityMismatch)]
    [InlineData("capability", PreparationCode.CapabilityUnavailable)]
    public async Task IdentityAndStrictRequestsFailClosedWithoutSetup(string kind, PreparationCode expected)
    {
        var clock = new Clock(); var connection = new Connection(); var setup = new Setup(); var trace = new Trace();
        connection.Identity = kind switch
        {
            "build" => connection.Identity with { AttestedGameBuildId = "different" },
            "unattested" => connection.Identity with { AttestedGameBuildId = null },
            "pending" => connection.Identity with { HasOutstandingRequests = true },
            "profile" => connection.Identity with { Profile = "Debug" },
            _ => connection.Identity with { Capabilities = new HashSet<string>() }
        };
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock, setup);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(0, setup.Calls); Assert.Equal(1, connection.Releases);
        Assert.Contains(new(PreparationStage.Identity, expected), trace.Events);
    }
    [Theory]
    [InlineData(false, "scene")]
    [InlineData(true, "forbidden")]
    public async Task RunnerRejectsUnauthorizedSetupBeforeSideEffects(bool authorized, string operation)
    {
        var clock = new Clock(); var setup = new Setup { Authorized = authorized, OperationIds = [operation] }; var trace = new Trace();
        await Execute(new(Policy(), clock, new Launcher(), new Connector(new()), trace), clock, setup);
        Assert.Equal(0, setup.Calls); Assert.Contains(new(PreparationStage.Setup, PreparationCode.SetupForbidden), trace.Events);
    }
    [Fact]
    public async Task UnknownSetupIsNotRetriedAndSuccessfulSetupDoesNotProvePrerequisites()
    {
        foreach (var receipt in new[] { SetupReceipt.Unconfirmed, SetupReceipt.Confirmed })
        {
            var clock = new Clock(); var setup = new Setup { Receipt = receipt }; var connection = new Connection { Preconditions = false }; var trace = new Trace();
            var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock, setup);
            Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(1, setup.Calls);
            Assert.Contains(new(receipt == SetupReceipt.Unconfirmed ? PreparationStage.Setup : PreparationStage.Preconditions,
                receipt == SetupReceipt.Unconfirmed ? PreparationCode.SetupUnconfirmed : PreparationCode.PreconditionsUnsatisfied), trace.Events);
        }
    }
    [Fact]
    public async Task StaleEpochAndExitedLaunchNeverBecomeReady()
    {
        var clock = new Clock(); var trace = new Trace(); var connection = new Connection { Stale = true };
        await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Contains(new(PreparationStage.Synchronize, PreparationCode.StaleObservation), trace.Events);
        var launcher = new Launcher(); launcher.Process.HasExited = true; trace = new(); var connector = new Connector(new());
        await Execute(new(Policy(HostMode.Launch), clock, launcher, connector, trace), clock);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events); Assert.Equal(0, connector.Calls);
    }
    [Fact]
    public async Task OverallDeadlineIsNotResetByConnectionAndPartialProcessIsReleased()
    {
        var clock = new Clock(); var connector = new Connector(new()) { BeforeReturn = () => clock.Advance(2000) };
        var launcher = new Launcher(); var trace = new Trace();
        var outcome = await Execute(new(Policy(HostMode.Launch), clock, launcher, connector, trace), clock);
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason); Assert.Equal(1, launcher.Process.Shutdowns);
        Assert.Contains(new(PreparationStage.Connect, PreparationCode.Timeout), trace.Events);
    }
    [Fact]
    public async Task UnknownConnectionIsNotRetried()
    {
        var clock = new Clock(); var connector = new Connector(new()) { Unknown = true };
        await Execute(new(Policy(), clock, new Launcher(), connector, new Trace()), clock);
        Assert.Equal(1, connector.Calls);
    }
    [Fact]
    public async Task OnlyKnownNotReadyConnectCanRetryWithinOriginalDeadline()
    {
        var clock = new Clock(); var connector = new Connector(new() { Clock = clock }) { NotReadyCount = 2 };
        var trace = new Trace(); var outcome = await Execute(new(Policy(), clock, new Launcher(), connector, trace), clock);
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status); Assert.Equal(3, connector.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(2), clock.Elapsed);
        Assert.Single(trace.Events, evidence => evidence == new PreparationEvent(PreparationStage.Connect, PreparationCode.Completed));
        Assert.Equal(2, trace.Events.Count(evidence => evidence.Stage == PreparationStage.RetryDelay));
    }
    [Fact]
    public async Task CancellationAfterConnectionAcquisitionReleasesItAndOnlyOwnedProcess()
    {
        var clock = new Clock(); var connection = new Connection(); using var cancellation = new CancellationTokenSource();
        var connector = new Connector(connection) { BeforeReturn = cancellation.Cancel }; var launcher = new Launcher();
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, launcher, connector, new Trace()); var run = Run(clock);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, owned, token) =>
        { return (await preparation.PrepareAsync(session, owned, null, null, token)).Boundary; }, (_, _) => ValueTask.FromResult(true), cancellation.Token);
        Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status); Assert.Equal(1, launcher.Process.Shutdowns); Assert.Equal(1, connection.Releases);
    }
    [Fact]
    public async Task UnknownOwnerReleaseBlocksAnotherLocalRun()
    {
        var clock = new Clock(); var policy = Policy(); var connection = new Connection { ReleaseConfirmed = false };
        var outcome = await Execute(new(policy, clock, new Launcher(), new Connector(connection), new Trace()), clock);
        Assert.False(outcome.PostProcessingComplete);
        var trace = new Trace(); var connector = new Connector(new());
        await Execute(new(policy, clock, new Launcher(), connector, trace), clock);
        Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), trace.Events); Assert.Equal(0, connector.Calls);
    }
    [Fact]
    public async Task APreviousSnapshotCannotBeCertifiedAsThisRunsInitialCapture()
    {
        var clock = new Clock(); var connection = new Connection { WrongRequest = true }; var trace = new Trace();
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status);
        Assert.Contains(new(PreparationStage.Synchronize, PreparationCode.StaleObservation), trace.Events);
    }
    [Theory]
    [InlineData(false, ResultStatus.Passed)]
    [InlineData(true, ResultStatus.Failed)]
    public async Task ExactInitialCaptureEvaluatesSuccessAndDeathBeforeAnyDriver(bool died, ResultStatus expected)
    {
        var clock = new Clock(); var connection = new Connection(); var trace = new Trace();
        var assertion = JsonNode.Parse("""{"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"role":{"value":"button"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject();
        var condition = PreparedCondition.Create(assertion, new(10, 1000));
        static ConditionObservationUnit Unit(bool value) => new([KeyValuePair.Create("$", new ConditionLeafObservation("scope", true,
            [new ConditionTargetObservation("target", value ? "{\"type\":\"bool\",\"value\":true}" : "{\"type\":\"bool\",\"value\":false}")]))]);
        connection.InitialObservation = new(TimeSpan.Zero, Unit(true), Unit(died));
        var baseline = Run(clock); var run = new RunSession(baseline.Limits, clock, clock, condition, condition);
        var preparation = new HostPreparation(Policy(), clock, new Launcher(), new Connector(connection), trace); var calls = 0;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, owned, token) =>
            (await preparation.PrepareAsync(session, owned, null, null, token)).Boundary,
            (_, _) => { calls++; return ValueTask.FromResult(true); });
        Assert.Equal(expected, outcome.Primary.Status); Assert.Equal(0, calls); Assert.Equal(1, connection.Synchronizations);
        Assert.Equal(died ? RunReason.FailureCondition : RunReason.GoalSatisfied, outcome.Primary.Cause.Reason);
        Assert.Equal(1, connection.Releases);
    }
    [Fact]
    public async Task StructuredSetupCountPreventsEveryOperationBeforeDispatch()
    {
        var clock = new Clock(); var setup = new Setup { OperationIds = ["scene", "scene"] }; var trace = new Trace();
        await Execute(new(Policy(), clock, new Launcher(), new Connector(new()), trace), clock, setup);
        Assert.Equal(0, setup.Calls); Assert.Contains(new(PreparationStage.Setup, PreparationCode.SetupForbidden), trace.Events);
    }
    [Fact]
    public async Task LaunchedExitIsVisibleInReturnedFeedAndAttachDoesNotOwnIt()
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var launcher = new Launcher(); var trace = new Trace();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), trace)
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary); launcher.Process.HasExited = true;
        await Assert.ThrowsAsync<PreparationException>(() => host.Feed.CaptureAsync(CancellationToken.None).AsTask());
        run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)]);
        await cleanup.CompleteAsync(run, clock);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
        Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Fact]
    public async Task NoncooperativeLaunchCannotExtendPreparationAndLateProcessIsShutDown()
    {
        var clock = new Clock(); var launcher = new DelayedLauncher(); var policy = Policy(HostMode.Launch); var trace = new Trace();
        var pending = Execute(new(policy, clock, launcher, new Connector(new()), trace), clock);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(1000);
        var outcome = await pending;
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
        Assert.False(outcome.PostProcessingComplete); // acquisition is still outstanding, local exclusion stays closed
        var lateProcess = new Process(); launcher.Completion.TrySetResult(lateProcess);
        await lateProcess.Released.Task;
        Assert.Equal(1, lateProcess.Shutdowns);
        var connector = new Connector(new()); var laterTrace = new Trace();
        await Execute(new(policy, clock, new Launcher(), connector, laterTrace), clock);
        Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), laterTrace.Events); Assert.Equal(0, connector.Calls);
    }
    [Fact]
    public async Task ConnectionOwnerReleaseHappensBeforeProcessShutdown()
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection();
        connection.OnRelease = () => Assert.Equal(0, launcher.Process.Shutdowns);
        var outcome = await Execute(new(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace()), clock);
        Assert.True(outcome.PostProcessingComplete); Assert.Equal(1, connection.Releases); Assert.Equal(1, launcher.Process.Shutdowns);
    }
    private sealed class OversizedOperations : IReadOnlyList<string>
    {
        public int Count => int.MaxValue;
        public string this[int index] => throw new InvalidOperationException("MustRejectCountBeforeReading");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("MustRejectCountBeforeEnumerating");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public async Task OversizedSetupIsRejectedBeforeAnySnapshotAllocationOrEnumeration()
    {
        var clock = new Clock(); var trace = new Trace(); var setup = new Setup { OperationIds = new OversizedOperations() };
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(new()), trace), clock, setup);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(0, setup.Calls);
        Assert.Contains(new(PreparationStage.Setup, PreparationCode.SetupForbidden), trace.Events);
        Assert.DoesNotContain(outcome.Exceptions, evidence => evidence.Type == typeof(InvalidOperationException).FullName);
    }
    private sealed class ReadCrossingClock : IClock
    {
        private readonly Clock inner = new();
        public bool CrossAfterNextRead { get; set; }
        public TimeSpan Elapsed
        {
            get { var value = inner.Elapsed; if (CrossAfterNextRead) { CrossAfterNextRead = false; inner.Advance(2000); } return value; }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => inner.DelayAsync(duration, token);
    }
    [Fact]
    public async Task DeadlineCrossingBetweenClockReadsRemainsPreparationTimeout()
    {
        var clock = new ReadCrossingClock(); var trace = new Trace();
        trace.OnRecord = evidence => { if (evidence == new PreparationEvent(PreparationStage.Connect, PreparationCode.Completed)) clock.CrossAfterNextRead = true; };
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(new()), trace), clock);
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
        Assert.DoesNotContain(outcome.Exceptions, evidence => evidence.Type == typeof(ArgumentOutOfRangeException).FullName);
    }
    [Fact]
    public async Task CancelledAcquisitionWithUnconfirmedSelfReleaseRetainsTheLease()
    {
        var clock = new Clock(); var policy = Policy(); var connection = new Connection { ReleaseConfirmed = false };
        using var cancellation = new CancellationTokenSource(); var connector = new Connector(connection) { BeforeReturn = cancellation.Cancel };
        var run = Run(clock); var preparation = new HostPreparation(policy, clock, new Launcher(), connector, new Trace());
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, owned, token) =>
            (await preparation.PrepareAsync(session, owned, null, null, token)).Boundary, (_, _) => ValueTask.FromResult(true), cancellation.Token);
        Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status); Assert.False(outcome.PostProcessingComplete);
        var laterTrace = new Trace(); await Execute(new(policy, clock, new Launcher(), new Connector(new()), laterTrace), clock);
        Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), laterTrace.Events);
    }
    [Fact]
    public async Task ProviderCancellationWithoutCallerCancellationIsAHostFailure()
    {
        var clock = new Clock(); var trace = new Trace();
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(new()) { ProviderCancelled = true }, trace), clock);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(new(PreparationStage.Connect, PreparationCode.ConnectionFailed), trace.Events);
        Assert.Contains(outcome.Exceptions, evidence => evidence.Type == typeof(OperationCanceledException).FullName);
    }
    [Fact]
    public async Task RealSystemLauncherValidationFailureIsTracedWithoutLaunching()
    {
        var clock = new Clock(); var trace = new Trace(); var policy = Policy(HostMode.Launch) with
        { Launch = new(Path.GetFullPath($"missing-{Guid.NewGuid():N}.exe"), Directory.GetCurrentDirectory(), []) };
        var outcome = await Execute(new(policy, clock, new SystemProcessLauncher(), new Connector(new()), trace), clock);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Single(trace.Events, evidence => evidence == new PreparationEvent(PreparationStage.Launch, PreparationCode.LaunchFailed));
    }
    [Fact]
    public async Task ProcessExitInterruptsANoncooperativeCapture()
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var launcher = new Launcher(); var connection = new Connection { BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary);
        var capture = host.Feed.CaptureAsync(CancellationToken.None).AsTask();
        launcher.Process.Exit();
        var exception = await Assert.ThrowsAsync<PreparationException>(() => capture);
        Assert.Equal(PreparationCode.ProcessExited, exception.Code); Assert.Equal(RunPhase.Execution, exception.Cause.Phase);
        run.Evaluate(candidates: [exception.Cause]);
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        connection.BlockedCapture.TrySetResult(new(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([])));
    }
    [Fact]
    public async Task ClosedCleanupRegistrationCannotReclaimAnOutstandingAcquisition()
    {
        var clock = new Clock(); var policy = Policy(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var connector = new DelayedConnector(); var preparation = new HostPreparation(policy, clock, new Launcher(), connector, new Trace());
        var pending = preparation.PrepareAsync(run, cleanup, null, null).AsTask();
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Evaluate(cancelled: true); await cleanup.CompleteAsync(run, clock);
        clock.Advance(1000);
        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        var laterTrace = new Trace(); await Execute(new(policy, clock, new Launcher(), new Connector(new()), laterTrace), clock);
        Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), laterTrace.Events);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateConnection = new Connection { ReleaseConfirmed = false, OnRelease = () => released.TrySetResult() };
        connector.Completion.TrySetResult(lateConnection); await released.Task;
        Assert.Equal(1, lateConnection.Releases);
    }
    [Fact]
    public async Task MissingPortOrLaunchCannotGuessAnExecutable()
    {
        Assert.Throws<ArgumentException>(() => new HostPreparation(Policy() with { Endpoint = new("ws://localhost") }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
        Assert.Throws<ArgumentException>(() => new HostPreparation(Policy() with { HostMode = HostMode.Launch }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
        await Assert.ThrowsAsync<IOException>(() => new SystemProcessLauncher().LaunchAsync(new("missing.exe", ".", []), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ProviderTimeoutIsHostFailureRatherThanOwnerDeadline()
    {
        var clock = new Clock(); var trace = new Trace();
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(new()) { ProviderTimeout = true }, trace), clock);
        Assert.Equal(RunReason.ExecutionError, outcome.Primary.Cause.Reason);
        Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Contains(new(PreparationStage.Connect, PreparationCode.ConnectionFailed), trace.Events);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(TimeoutException).FullName);
    }

    private sealed class UnrelatedClock : IClock
    {
        public TimeSpan Elapsed => throw new InvalidOperationException("NotSessionClock");
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => throw new InvalidOperationException("NotSessionClock");
    }
    [Theory]
    [InlineData(HostMode.Attach)]
    [InlineData(HostMode.Launch)]
    public async Task PreparationUsesSessionClockRatherThanUnrelatedConstructorClock(HostMode mode)
    {
        var clock = new Clock(); var launcher = new Launcher();
        var outcome = await Execute(new(Policy(mode), new UnrelatedClock(), launcher, new Connector(new()), new Trace()), clock);
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status);
        Assert.True(outcome.PostProcessingComplete);
        Assert.Equal(mode == HostMode.Launch ? 1 : 0, launcher.Process.Shutdowns);
    }

    [Fact]
    public async Task CancelledProcessWrapperJoinsActualCaptureBeforeFreshRequest()
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var connection = new Connection { BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, new Launcher(), new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary);
        using var cancellation = new CancellationTokenSource();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnCapture = _ => captureStarted.TrySetResult();
        var old = host.Feed.CaptureAsync(cancellation.Token).AsTask();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.False(old.IsCompleted);
        var fresh = host.Feed.CaptureAsync(CancellationToken.None).AsTask();
        Assert.Equal(1, connection.Captures); Assert.False(fresh.IsCompleted);
        var previous = connection.BlockedCapture; connection.BlockedCapture = null;
        previous.SetResult(new(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([])));
        await old;
        await fresh;
        Assert.Equal(2, connection.Captures);
        run.Evaluate(cancelled: true); await cleanup.CompleteAsync(run, clock);
    }

    [Fact]
    public async Task ProcessExitRetainsCancellationCallbackFaultWithoutReplacingHostCause()
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup(); var launcher = new Launcher();
        var connection = new Connection { BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously),
            OnCapture = token => token.Register(() => throw new IOException("capture-cancel")) };
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnCapture = token => { token.Register(() => throw new IOException("capture-cancel")); captureStarted.TrySetResult(); };
        var capture = host.Feed.CaptureAsync(CancellationToken.None).AsTask();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); launcher.Process.Exit();
        var failure = await Assert.ThrowsAsync<PreparationException>(() => capture);
        Assert.Equal(PreparationCode.ProcessExited, failure.Code);
        var evidence = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.Contains(evidence.Flatten().InnerExceptions, item => item is IOException);
        run.Evaluate(candidates: [failure.Cause]); await cleanup.CompleteAsync(run, clock);
        connection.BlockedCapture.SetResult(new(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([])));
    }

    private sealed class BlockedShutdownProcess : IOwnedProcess
    {
        public bool HasExited => false;
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WaitForExitAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
        public ValueTask<bool> ShutdownAsync(CancellationToken token)
        { token.Register(() => Cancelled.TrySetResult()); return new(pending.Task); }
    }
    [Fact]
    public async Task LateProcessShutdownRemainsBoundedAfterRunClockStops()
    {
        var clock = new Clock(); var launcher = new DelayedLauncher();
        var policy = Policy(HostMode.Launch) with { ShutdownTimeout = TimeSpan.FromMilliseconds(20) };
        var run = Execute(new(policy, clock, launcher, new Connector(new()), new Trace()), clock);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(1000); var outcome = await run;
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
        var late = new BlockedShutdownProcess(); launcher.Completion.SetResult(late);
        // No further Run-clock advance: the real release ceiling must still cancel this provider.
        await late.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.PostProcessingComplete);
    }

    private static PreparedCondition BooleanCondition() => PreparedCondition.Create(JsonNode.Parse("""{"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"role":{"value":"button"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject(), new(10, 1000));
    private static ConditionObservationUnit BooleanUnit(bool value) => new([KeyValuePair.Create("$", new ConditionLeafObservation("scope", true,
        [new ConditionTargetObservation("target", value ? "{\"type\":\"bool\",\"value\":true}" : "{\"type\":\"bool\",\"value\":false}")]))]);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task LaunchedMonitorSupersessionRetainsOldFailureAndGetsFreshCapture(bool ignoresCancellation, bool oldFailure, bool callbackFault)
    {
        var clock = new Clock(); var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition());
        var old = new TaskCompletionSource<RunObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            CurrentObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)), BlockedCapture = old,
            OnCapture = token => { token.Register(() => { if (!ignoresCancellation) old.TrySetCanceled(token); cancelled.TrySetResult();
                if (callbackFault) throw new IOException("capture-supersession"); }); captureStarted.TrySetResult(); } };
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, new Launcher(), new Connector(connection), new Trace());
        PreparedHost? host = null;
        var pending = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await preparation.PrepareAsync(session, cleanup, null, null, token);
            return host.Boundary; },
            async (session, token) => (await RunMonitor.AwaitAsync(session, clock, clock, host!.Feed,
                _ => new ValueTask<int>(work.Task), _ => [], token)).Completed).AsTask();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.Captures);
        connection.BlockedCapture = null;
        work.SetResult(1); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (ignoresCancellation)
        {
            Assert.Equal(1, connection.Captures); Assert.False(pending.IsCompleted);
            old.SetResult(new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(oldFailure)));
        }
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connection.Captures);
        Assert.Equal(oldFailure ? RunReason.FailureCondition : RunReason.GoalSatisfied, outcome.Primary.Cause.Reason);
        Assert.Equal(oldFailure ? ResultStatus.Failed : ResultStatus.Passed, outcome.Primary.Status);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.GoalSatisfied);
        Assert.DoesNotContain(outcome.Events, item => item.Reason == RunReason.ExecutionError);
        Assert.True(outcome.PostProcessingComplete);
        if (callbackFault) Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullHostCapabilitiesFailWithTracedHostEvidence(bool synchronized)
    {
        var clock = new Clock(); var connection = new Connection { MissingCapturedCapabilities = synchronized }; var trace = new Trace();
        if (!synchronized) connection.Identity = connection.Identity with { Capabilities = null! };
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(new(PreparationStage.Identity, PreparationCode.CapabilityUnavailable), trace.Events);
        Assert.DoesNotContain(outcome.Exceptions, item => item.Type == typeof(NullReferenceException).FullName);
    }

    [Fact]
    public async Task RetryDelayCanExceedOperationTimeoutWithinOriginalPreparationDeadline()
    {
        var clock = new Clock(); var connector = new Connector(new() { Clock = clock }) { NotReadyCount = 1 }; var trace = new Trace();
        var policy = Policy() with { OperationTimeout = TimeSpan.FromMilliseconds(50), RetryDelay = TimeSpan.FromMilliseconds(200) };
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnDelay = duration => { if (duration == policy.RetryDelay) retryStarted.TrySetResult(); };
        var pending = Execute(new(policy, clock, new Launcher(), connector, trace), clock);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted); clock.Advance(200);
        var outcome = await pending;
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status); Assert.Equal(2, connector.Calls);
        Assert.Contains(new(PreparationStage.RetryDelay, PreparationCode.Completed), trace.Events);
        Assert.DoesNotContain(trace.Events, item => item.Code == PreparationCode.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedSourceFailureSurvivesLosingWaitCancellationFault(bool exitWaitFails)
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup(); var launcher = new Launcher();
        var connection = new Connection(); var expected = new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Contract);
        var typed = new RunFailureException(expected);
        if (exitWaitFails)
        {

            connection.BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnCapture = token =>
            { token.Register(() => throw new IOException("losing-capture-cancel")); launcher.Process.FailExit(typed); };
        }
        else
        {
            connection.CaptureFailure = typed;
            launcher.Process.OnExitWait = token => token.Register(() => throw new IOException("losing-exit-cancel"));
        }
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary);
        var failure = await Assert.ThrowsAsync<RunFailureException>(() => host.Feed.CaptureAsync(CancellationToken.None).AsTask());
        Assert.Equal(expected, failure.Cause);
        var evidence = Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions;
        Assert.Contains(typed, evidence); Assert.Contains(evidence, item => item is IOException);
        run.RecordException(failure); run.Evaluate(candidates: [failure.Cause]); var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(ResultStatus.Invalid, outcome.Primary.Status); Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
        Assert.Contains(outcome.Exceptions, item => item.Type == typed.GetType().FullName && item.StackTrace == typed.StackTrace);
        connection.BlockedCapture?.SetResult(new(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([])));
    }

    [Theory]
    [InlineData("?credential=value")]
    [InlineData("#alias")]
    public void EndpointQueryOrFragmentCannotReachConnector(string suffix)
    {
        var policy = Policy() with { Endpoint = new Uri("ws://localhost:7777/path" + suffix) };
        Assert.Throws<ArgumentException>(() => new HostPreparation(policy, new Clock(), new Launcher(), new Connector(new()), new Trace()));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("boundary")]
    [InlineData("feed")]
    public async Task MissingHostReadinessComponentIsTracedAndFailsClosed(string missing)
    {
        var clock = new Clock(); var connection = new Connection { MissingBoundary = missing == "boundary", MissingFeed = missing == "feed" }; var trace = new Trace();
        if (missing == "identity") connection.Identity = null!;
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(missing == "identity" ? new(PreparationStage.Identity, PreparationCode.IdentityMismatch)
            : new(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed), trace.Events);
        Assert.True(outcome.PostProcessingComplete);
    }

    private sealed class EarlyWakeClock : IClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void Advance(int milliseconds) => Elapsed += TimeSpan.FromMilliseconds(milliseconds);
        public Action<TimeSpan>? OnDelay { get; set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OnDelay?.Invoke(duration); return ValueTask.CompletedTask; }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EarlyWakeCannotStartRetryBeforeItsAbsoluteClockTarget(bool advance)
    {
        var clock = new EarlyWakeClock(); var connector = new Connector(new() { Clock = clock }) { NotReadyCount = 1 };
        var policy = Policy() with { RetryDelay = TimeSpan.FromMilliseconds(60) };
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnDelay = duration => { if (duration == policy.RetryDelay) retryStarted.TrySetResult(); };
        var pending = Execute(new(policy, clock, new Launcher(), connector, new Trace()), clock);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connector.Calls); Assert.False(pending.IsCompleted);
        if (advance) clock.Advance(60);
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(advance ? 2 : 1, connector.Calls);
        Assert.Equal(advance ? ResultStatus.Unverified : ResultStatus.Invalid, outcome.Primary.Status);
        if (!advance) Assert.Equal(RunOrigin.Clock, outcome.Primary.Cause.Origin);
    }

    [Fact]
    public async Task ReadyCaptureCannotHideConcurrentTypedProcessWatchFailure()
    {
        var clock = new Clock(); var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition());
        var launcher = new Launcher();
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            CurrentObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)) };
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace()); PreparedHost? host = null;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await preparation.PrepareAsync(session, cleanup, null, null, token);
            launcher.Process.ExitWaitFailure = new RunFailureException(new(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Contract));
            return host.Boundary; },
            async (session, token) => (await RunMonitor.AwaitAsync(session, clock, clock, host!.Feed,
                _ => ValueTask.FromResult(1), _ => [], token)).Completed);
        Assert.Equal(ResultStatus.Invalid, outcome.Primary.Status); Assert.Equal(RunOrigin.Contract, outcome.Primary.Cause.Origin);
        Assert.DoesNotContain(outcome.Events, item => item.Reason == RunReason.GoalSatisfied);
    }

    [Theory]
    [InlineData("operations")]
    [InlineData("permissions")]
    [InlineData("changed")]
    public async Task MissingSetupListRejectsBeforeAnyOperation(string missing)
    {
        var clock = new Clock(); var setup = new Setup(); var trace = new Trace();
        if (missing == "operations") setup.OperationIds = null!;
        if (missing == "permissions") setup.AllowedOperationIds = null!;
        if (missing == "changed") { var reads = 0; setup.ReadOperations = () => ++reads == 1 ? ["scene"] : null!; }
        var outcome = await Execute(new(Policy(), clock, new Launcher(), new Connector(new()), trace), clock, setup);
        Assert.Equal(0, setup.Calls); Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Contains(new(PreparationStage.Setup, PreparationCode.SetupForbidden), trace.Events);
    }

    [Theory]
    [InlineData(PreparationStage.Launch, PreparationCode.LaunchFailed)]
    [InlineData(PreparationStage.Identity, PreparationCode.IdentityUnavailable)]
    [InlineData(PreparationStage.Setup, PreparationCode.SetupFailed)]
    [InlineData(PreparationStage.Planner, PreparationCode.PlannerUnavailable)]
    [InlineData(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed)]
    public async Task ConnectionRetrySentinelFromOtherStageIsTracedHostFailure(PreparationStage stage, PreparationCode expected)
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection(); var planner = new Planner(); var setup = new Setup(); var trace = new Trace();
        var exception = new ConnectionNotReadyException();
        switch (stage)
        {
            case PreparationStage.Launch: launcher.FailureException = exception; break;
            case PreparationStage.Identity: connection.IdentityFailure = exception; break;
            case PreparationStage.Setup: setup.OperationFailure = exception; break;
            case PreparationStage.Planner: planner.FailureException = exception; break;
            case PreparationStage.Synchronize: connection.SynchronizeFailure = exception; break;
        }
        var connector = new Connector(connection);
        var outcome = await Execute(new(Policy(stage == PreparationStage.Launch ? HostMode.Launch : HostMode.Attach,
            stage == PreparationStage.Planner ? PlayMode.Explore : PlayMode.Replay), clock, launcher, connector, trace), clock,
            stage == PreparationStage.Setup ? setup : null, stage == PreparationStage.Planner ? planner : null);
        Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Contains(new(stage, expected), trace.Events); Assert.True(connector.Calls <= 1);
        Assert.DoesNotContain(trace.Events, item => item.Stage == PreparationStage.RetryDelay);
    }

    [Fact]
    public async Task NestedExceptionWalkRemainsInsideExplicitEvidenceLimit()
    {
        var clock = new Clock(); var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 32);
        var run = new RunSession(limits, clock, clock); run.BeginPreparation();
        var cause = new RunEvent(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host);
        run.RecordException(new RunFailureException(cause, new AggregateException(Enumerable.Range(0, 1000).Select(_ => new IOException()))));
        run.Evaluate(candidates: [cause]); var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(32, outcome.Exceptions.Count); Assert.Equal(cause, outcome.Primary.Cause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DuplicateAggregateReferencesCannotHideLaterDistinctDiagnostics(int nesting)
    {
        var clock = new Clock(); var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 32);
        var run = new RunSession(limits, clock, clock); run.BeginPreparation();
        var duplicate = new IOException(); var distinct = new FormatException();
        var aggregate = new AggregateException(Enumerable.Repeat<Exception>(duplicate, 1000).Append(distinct));
        var cause = new RunEvent(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host);
        run.RecordException(nesting switch
        {
            1 => new RunFailureException(cause, aggregate),
            2 => new AggregateException(aggregate, duplicate),
            _ => aggregate
        });
        run.Evaluate(candidates: [cause]); var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Single(outcome.Exceptions, item => item.Type == typeof(IOException).FullName);
        Assert.Single(outcome.Exceptions, item => item.Type == typeof(FormatException).FullName);
        Assert.Equal(nesting == 0 ? 3 : 4, outcome.Exceptions.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SimultaneouslyFaultedCaptureAndProcessWatchKeepBothOriginalsAndNormativeCause(int sourceKind)
    {
        var clock = new Clock(); var run = Run(clock); var launcher = new Launcher();
        launcher.Process.DirectExitWatch = true;
        var contract = new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Contract);
        var hostFailure = new RunEvent(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host);
        var source = sourceKind == 0 ? (Exception)new IOException() :
            new RunFailureException(sourceKind == 2 ? contract : hostFailure, new IOException());
        var exit = new RunFailureException(sourceKind == 2 ? hostFailure : contract, new FormatException());
        var connection = new Connection { CaptureFailure = source };
        // The source invocation starts before the exact watch faults. Explicitly
        // await any later source evidence before the owner arbitrates both faults.
        connection.OnCapture = _ => launcher.Process.FailExit(exit);

        run.BeginPreparation(); var cleanup = new OwnedCleanup(); var trace = new Trace();
        var prepared = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), trace)
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(prepared.Boundary);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ProcessObservationFeed(connection, launcher.Process,
            token => new(Task.Run(() => launcher.Process.HasExited, token)), trace.Record, run.PostException,
            exception => { run.PostProviderException(exception); if (ReferenceEquals(exception, source) || ReferenceEquals(exception.InnerException, source)) posted.TrySetResult(); });
        var failure = await Assert.ThrowsAsync<RunFailureException>(() => feed.CaptureAsync(CancellationToken.None).AsTask());
        run.RecordException(failure);
        if (!run.Exceptions.Any(item => item.Type == typeof(IOException).FullName)) await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Evaluate(candidates: [failure.Cause]); var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(contract, outcome.Primary.Cause); Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(FormatException).FullName);
        Assert.Equal(1, launcher.Process.Shutdowns); Assert.Equal(1, connection.Releases);
    }

    [Theory]
    [InlineData(CleanupStage.InputRelease, false)] [InlineData(CleanupStage.InputRelease, true)]
    [InlineData(CleanupStage.ResourceRelease, false)] [InlineData(CleanupStage.ResourceRelease, true)]
    public async Task EndpointLeaseOutlivesExecutionResourcesAndOnlyConfirmedReleaseAllowsReentry(CleanupStage stage, bool confirmed)
    {
        var clock = new Clock(); var policy = Policy(); var cleanup = new OwnedCleanup(); var during = new Trace();
        var duringConnector = new Connector(new()); var lateCalls = 0;
        var outcome = await RunExecutor.ExecuteAsync(Run(clock), clock, cleanup, async (session, owned, token) =>
        { return (await new HostPreparation(policy, clock, new Launcher(), new Connector(new()), new Trace())
            .PrepareAsync(session, owned, null, null, token)).Boundary; }, (_, _) =>
        {
            cleanup.Register(stage, async _ =>
            {
                lateCalls++;
                await Execute(new(policy, clock, new Launcher(), duringConnector, during), clock);
                return confirmed;
            });
            return ValueTask.FromResult(true);
        });
        Assert.Equal(1, lateCalls); Assert.Equal(0, duringConnector.Calls);
        Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), during.Events);
        Assert.Equal(confirmed, outcome.PostProcessingComplete);
        var after = new Trace(); var afterConnector = new Connector(new());
        await Execute(new(policy, clock, new Launcher(), afterConnector, after), clock);
        Assert.Equal(confirmed ? 1 : 0, afterConnector.Calls);
        Assert.Equal(!confirmed, after.Events.Contains(new(PreparationStage.Ownership, PreparationCode.Busy)));
    }

    [Fact]
    public async Task FullCleanupEvidenceCannotHideReleaseCallbackFaultAndOpenEndpointLease()
    {
        var clock = new Clock(); var policy = Policy(); var cleanup = new OwnedCleanup();
        var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 32);
        var outcome = await RunExecutor.ExecuteAsync(new(limits, clock, clock), clock, cleanup, async (session, owned, token) =>
        { return (await new HostPreparation(policy, clock, new Launcher(), new Connector(new()), new Trace())
            .PrepareAsync(session, owned, null, null, token)).Boundary; }, (_, _) =>
        {
            for (var index = 0; index < 32; index++) cleanup.Register(CleanupStage.Diagnostics, _ => ValueTask.FromResult(false));
            cleanup.Register(CleanupStage.ResourceRelease, token =>
            { token.Register(() => throw new IOException()); return ValueTask.FromResult(true); });
            return ValueTask.FromResult(true);
        });
        Assert.Equal(32, outcome.PostProcessing.Count);
        Assert.Contains(outcome.PostProcessing, item => item.Reason == PostProcessingReason.EvidenceLimitExceeded);
        var connector = new Connector(new()); var trace = new Trace();
        await Execute(new(policy, clock, new Launcher(), connector, trace), clock);
        Assert.Equal(0, connector.Calls); Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), trace.Events);
    }

    private sealed class MetadataCountList(Action read) : IReadOnlyList<string>
    {
        public int Count { get { read(); return 1; } }
        public string this[int index] => "scene";
        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)["scene"]).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(3, false)] [InlineData(4, false)] [InlineData(5, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    [InlineData(3, true)] [InlineData(4, true)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public async Task BlockingSetupMetadataCannotSuppressOwnerDeadlineOrCancellation(int member, bool cancel)
    {
        using var releaseRead = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Block() { entered.TrySetResult(); try { releaseRead.Wait(); } finally { returned.TrySetResult(); } }
        var clock = new Clock(); var setup = new Setup(); var connection = new Connection(); var policy = Policy();
        switch (member)
        {
            case 0: setup.BeforeAuthorization = Block; break;
            case 1: setup.ReadOperations = () => { Block(); return ["scene"]; }; break;
            case 2: setup.ReadAllowed = () => { Block(); return new HashSet<string> { "scene" }; }; break;
            case 3: setup.OperationIds = new MetadataCountList(Block); break;
            case 4: setup.ReadMaximum = () => { Block(); return 1; }; break;
            case 5: setup.ReadTimeout = () => { Block(); return TimeSpan.FromSeconds(1); }; break;
            case 6: var reads = 0; setup.BeforeAuthorization = () => { if (++reads == 2) Block(); }; break;
        }
        try
        {
            var pending = RunExecutor.ExecuteAsync(Run(clock), clock, new OwnedCleanup(), async (session, cleanup, token) =>
            { return (await new HostPreparation(policy, clock, new Launcher(), new Connector(connection), new Trace())
                .PrepareAsync(session, cleanup, setup, null, token)).Boundary; }, (_, _) => ValueTask.FromResult(true), cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel(); else clock.Advance(1000);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? ResultStatus.Aborted : ResultStatus.Failed, outcome.Primary.Status);
            Assert.Equal(cancel ? RunReason.Cancelled : RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
            Assert.Equal(0, setup.Calls); Assert.Equal(1, connection.Releases); Assert.False(outcome.PostProcessingComplete);
            Assert.Contains(outcome.PostProcessing, item => item.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
            // No unlimited replacement workers while an earlier provider read remains unended.
            var connector = new Connector(new()); var later = new Trace();
            await Execute(new(policy, clock, new Launcher(), connector, later), clock);
            Assert.Equal(0, connector.Calls); Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), later.Events);
        }
        finally { releaseRead.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, setup.Calls);
    }

    [Fact]
    public async Task WholePreparationDeadlineCannotOmitOwnershipReleaseDuringRetryDelay()
    {
        var clock = new Clock(); var policy = Policy() with { RetryDelay = TimeSpan.FromSeconds(3) };
        var first = new Connector(new()) { NotReadyCount = 1 }; var trace = new Trace();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnDelay = duration => { if (duration == policy.RetryDelay) retryStarted.TrySetResult(); };
        var pending = Execute(new(policy, clock, new Launcher(), first, trace), clock);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Calls); clock.Advance(2000);
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
        // The outer owner may enter cleanup before preparation finishes unwinding.
        // Diagnostics then remains unconfirmed, but certified no-effect backoff
        // must not leave the endpoint's resource ownership unreleased.
        Assert.DoesNotContain(outcome.PostProcessing, item => item.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
        if (!outcome.PostProcessingComplete)
            Assert.Contains(outcome.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed);
        var second = new Connector(new()); var secondTrace = new Trace();
        await Execute(new(policy, clock, new Launcher(), second, secondTrace), clock);
        Assert.Equal(1, second.Calls); Assert.DoesNotContain(new(PreparationStage.Ownership, PreparationCode.Busy), secondTrace.Events);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SuccessfulProcessExitWatchCannotBeLostToFaultedOrCancelledCapture(bool cancel)
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection(); var trace = new Trace();
        // The case promises the exact watch is already successful, not merely
        // that an asynchronously forwarded fake exit signal will become ready.
        launcher.Process.DirectExitWatch = true;
        using var cancellation = new CancellationTokenSource();
        connection.OnCapture = token =>
        { launcher.Process.Exit(); if (cancel) cancellation.Cancel(); connection.CaptureFailure = cancel ? new OperationCanceledException(token) : new IOException(); };
        // Arbitrate the same ready wrapper failure and cancellation in one owner cycle;
        // async status access means global cancellation can otherwise precede fault readiness.
        var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var prepared = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), trace)
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(prepared.Boundary);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observe the same wrapper's owner-evidence port so actual losing-source
        // completion, rather than a future throw, is ready before primary freezes.
        var feed = new ProcessObservationFeed(connection, launcher.Process,
            token => new(Task.Run(() => launcher.Process.HasExited, token)), trace.Record,
            exception => { run.PostException(exception); posted.TrySetResult(); });
        var failure = await Assert.ThrowsAsync<PreparationException>(() => feed.CaptureAsync(cancellation.Token).AsTask());
        Assert.Equal(PreparationCode.ProcessExited, failure.Code);
        run.RecordException(failure);
        var expectedType = (cancel ? typeof(OperationCanceledException) : typeof(IOException)).FullName;
        if (!run.Exceptions.Any(item => item.Type == expectedType)) await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Evaluate(candidates: [failure.Cause], cancelled: cancellation.IsCancellationRequested);
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Equal(RunPhase.Execution, outcome.Primary.Cause.Phase);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
        Assert.Contains(outcome.Exceptions, item => item.Type == expectedType && item.StackTrace is not null);
        Assert.Equal(1, launcher.Process.Shutdowns);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task LateLosingSourceFailurePostsOriginalBeforeFreezeButCannotChangeConfirmedSnapshot(bool waiting, bool cancel, bool frozen)
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var connection = new Connection { BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sourceToken = default;
        void Started(CancellationToken token) { sourceToken = token; started.TrySetResult(); }
        connection.OnCapture = Started; connection.OnWait = Started;
        var feed = new ProcessObservationFeed(connection, launcher.Process, _ => ValueTask.FromResult(false), _ => { },
            exception => { run.PostException(exception); posted.TrySetResult(exception); });
        var pending = waiting ? feed.WaitForChangeAsync(cancellation.Token).AsTask() : (Task)feed.CaptureAsync(cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        launcher.Process.ConfirmExitWithStaleStatus(); if (cancel) cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<PreparationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PreparationCode.ProcessExited, failure.Code); run.RecordException(failure);
        RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(candidates: [failure.Cause], cancelled: cancel); snapshot = run.CapturePrimary(); }
        Exception original;
        try { if (cancel) throw new OperationCanceledException(sourceToken); throw new IOException(); }
        catch (Exception exception) { original = exception; }
        if (waiting) connection.BlockedWait.SetException(original); else connection.BlockedCapture.SetException(original);
        var ready = await Task.WhenAny(posted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(posted.Task, ready); Assert.Same(original, await posted.Task);
        if (!frozen) run.Evaluate(candidates: [failure.Cause], cancelled: cancel);
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        if (frozen) Assert.Equal(snapshot!.Exceptions, outcome.Exceptions);
        else Assert.Contains(outcome.Exceptions, item => item.Type == original.GetType().FullName && item.StackTrace is not null);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateLosingExitWatchFailurePostsOriginalWithoutChangingAnAlreadyConfirmedPrimary(bool frozen)
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var connection = new Connection(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        var posted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ProcessObservationFeed(connection, launcher.Process, _ => ValueTask.FromResult(false), _ => { },
            exception => { run.PostException(exception); posted.TrySetResult(exception); });
        var observation = await feed.CaptureAsync(CancellationToken.None);
        RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(cancelled: true); snapshot = run.CapturePrimary(); }
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        launcher.Process.FailExit(original);
        var ready = await Task.WhenAny(posted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(posted.Task, ready); Assert.Same(original, await posted.Task);
        if (!frozen) run.Evaluate(observation.Success, observation.CapturedAt, failureUnit: observation.Failure);
        var outcome = await cleanup.CompleteAsync(run, clock);
        if (frozen) { Assert.Equal(snapshot!.Primary, outcome.Primary); Assert.Equal(snapshot.Exceptions, outcome.Exceptions); }
        else
        {
            Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task ActualProviderCauseParticipatesBeforeFreezeWhileCallbackFaultsRemainSecondary(bool provider, bool frozen)
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); run.BeginRunning();
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!; operation.BeginDispatch(0);
        var contract = new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Contract);
        var host = new RunEvent(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host);
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        var fault = new RunFailureException(contract, original); RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(candidates: [host]); snapshot = run.CapturePrimary(); }
        if (provider) run.PostProviderException(fault); else run.PostException(fault);
        if (!frozen) run.Evaluate(candidates: [host]);
        var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(provider && !frozen ? contract : new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host), outcome.Primary.Cause);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.ActionUnconfirmed);
        Assert.False(operation.IsOpen); Assert.False(operation.ConfirmResult());
        Assert.Equal(DeliveryState.Uncertain, operation.Actions!.Deliveries[0]); Assert.Equal(1, run.Budget.Snapshot.Actions);
        if (frozen) Assert.Equal(snapshot!.Exceptions, outcome.Exceptions);
        else Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task LateUntypedProviderFaultCompetesWithGoalOnlyBeforeFreeze(int kind, bool frozen)
    {
        var clock = new Clock(); var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition());
        run.BeginPreparation(); run.BeginRunning();
        var process = new Process { DirectExitWatch = true };
        var connection = new Connection { BlockedCapture = kind == 0 ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null,
            BlockedWait = kind == 1 ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnCapture = _ => entered.TrySetResult(); connection.OnWait = _ => entered.TrySetResult();
        var posted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ProcessObservationFeed(connection, process, _ => ValueTask.FromResult(false), _ => { }, run.PostException,
            exception => { run.PostProviderException(exception); posted.TrySetResult(exception); });
        var pending = kind == 1 ? feed.WaitForChangeAsync(CancellationToken.None).AsTask() : (Task)feed.CaptureAsync(CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind != 2)
        {
            process.ConfirmExitWithStaleStatus();
            await Assert.ThrowsAsync<PreparationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else await pending.WaitAsync(TimeSpan.FromSeconds(5));
        RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false)); snapshot = run.CapturePrimary(); }
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        if (kind == 0) connection.BlockedCapture!.SetException(original);
        else if (kind == 1) connection.BlockedWait!.SetException(original);
        else process.FailExit(original);
        var evidence = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expectedCause = new RunEvent(RunReason.ExecutionError, RunPhase.Execution, kind == 2 ? RunOrigin.Host : RunOrigin.Runner);
        if (!frozen) run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false));
        var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        if (frozen) { Assert.Equal(snapshot!.Primary, outcome.Primary); Assert.Equal(snapshot.Exceptions, outcome.Exceptions); }
        else
        {
            Assert.Equal(kind == 2 ? 1 : 10, outcome.ExitCode); Assert.Equal(expectedCause, outcome.Primary.Cause);
            Assert.Contains(outcome.Events, item => item.Reason == RunReason.GoalSatisfied);
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
        }
        Assert.Same(original, evidence.InnerException);
        Assert.Equal(expectedCause, Assert.IsAssignableFrom<RunFailureException>(evidence).Cause);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task PostedEvidenceOverflowCannotSilentlyPassOrRewriteFrozenPrimary(bool provider, bool frozen)
    {
        var clock = new Clock(); var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition());
        run.BeginPreparation(); run.BeginRunning(); RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false)); snapshot = run.CapturePrimary(); }
        for (var i = 0; i < run.Limits.MaxEvidenceItems; i++) run.PostException(new IOException("secondary"));
        var cause = new RunEvent(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host);
        if (provider) run.PostProviderException(new RunFailureException(cause)); else run.PostException(new IOException("overflow"));
        if (!frozen) run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false));
        var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.True(outcome.Exceptions.Count <= run.Limits.MaxEvidenceItems); Assert.True(outcome.Events.Count <= run.Limits.MaxEvidenceItems);
        if (frozen) { Assert.Equal(snapshot!.Primary, outcome.Primary); Assert.Equal(snapshot.Exceptions, outcome.Exceptions); }
        else { Assert.Equal(10, outcome.ExitCode); Assert.Equal(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), outcome.Primary.Cause); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalLifecycleSampleSharesPrimaryClosureAfterEarlierPollWasNotReady(bool dispatch)
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); run.BeginPreparation();
        var cleanup = new OwnedCleanup(); var trace = new Trace();
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)) };
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), trace)
            .PrepareAsync(run, cleanup, null, null);
        var exactProbe = host.Boundary.InitialLifecycleFailure!; var polls = 0;
        host.Boundary.InitialLifecycleFailure = () =>
        {
            var ready = exactProbe();
            if (++polls == 1) { Assert.Null(ready); launcher.Process.ConfirmExitWithStaleStatus(); }
            return ready;
        };
        run.BeginRunning(host.Boundary);
        var operation = dispatch ? run.ApproveOperation(1, TimeSpan.FromSeconds(1)) : null;
        operation?.BeginDispatch(0);
        run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false));
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.ExecutionError && item.Origin == RunOrigin.Host);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.GoalSatisfied);
        Assert.Contains(trace.Events, item => item.Code == PreparationCode.ProcessExited);
        Assert.Equal(2, polls);
        if (dispatch) { Assert.Contains(outcome.Events, item => item.Reason == RunReason.ActionUnconfirmed); Assert.False(operation!.IsOpen); }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task AttachLateActualSourceFaultCannotBeLostToObservationOnlyCleanup(bool waiting, bool frozen)
    {
        var clock = new Clock(); var launcher = new Launcher();
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); run.BeginPreparation();
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously), BlockedWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cleanup = new OwnedCleanup(); var host = await new HostPreparation(Policy(HostMode.Attach), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        using var obsolete = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnCapture = _ => entered.TrySetResult(); connection.OnWait = _ => entered.TrySetResult();
        var pending = waiting ? host.Feed.WaitForChangeAsync(obsolete.Token).AsTask() : (Task)host.Feed.CaptureAsync(obsolete.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); obsolete.Cancel(); Assert.False(pending.IsCompleted);
        RunSnapshot? snapshot = null;
        if (frozen) { run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false)); snapshot = run.CapturePrimary(); }
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        if (waiting) connection.BlockedWait.SetException(original); else connection.BlockedCapture.SetException(original);
        var fault = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Same(original, fault);
        if (!frozen) run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false));
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(0, launcher.Calls); Assert.Equal(0, launcher.Process.Shutdowns); Assert.Equal(1, connection.Releases);
        if (frozen) { Assert.Equal(snapshot!.Primary, outcome.Primary); Assert.Equal(snapshot.Exceptions, outcome.Exceptions); }
        else { Assert.Equal(10, outcome.ExitCode); Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null); }
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ReadyAndLateLifecycleProviderFaultsEmitLaunchFailedTrace(int kind)
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); run.BeginRunning();
        var process = new Process { DirectExitWatch = true }; var trace = new Trace();
        var traced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        trace.OnRecord = item => { if (item.Code == PreparationCode.LaunchFailed) traced.TrySetResult(); };
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        var feed = new ProcessObservationFeed(new Connection(), process,
            _ => kind == 0 ? ValueTask.FromException<bool>(new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, original, RunPhase.Execution)) : ValueTask.FromResult(false),
            trace.Record, run.PostException, run.PostProviderException);
        if (kind == 1) process.FailExit(original);
        if (kind < 2) await Assert.ThrowsAsync<PreparationException>(() => feed.CaptureAsync(CancellationToken.None).AsTask());
        else { await feed.CaptureAsync(CancellationToken.None); process.FailExit(original); }
        await traced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(trace.Events, item => item == new PreparationEvent(PreparationStage.Launch, PreparationCode.LaunchFailed));
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task PostedCausesAndOverflowCloseAuthorityBeforeNextEvaluation(int kind)
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); run.BeginRunning();
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!; var before = run.Budget.Snapshot;
        var plannerRun = Run(clock); plannerRun.BeginPreparation(); plannerRun.BeginRunning();
        var approvalRun = Run(clock); approvalRun.BeginPreparation(); approvalRun.BeginRunning();
        await Task.Run(() =>
        {
            foreach (var session in new[] { run, plannerRun, approvalRun })
            {
                if (kind == 0) session.PostException(new IOException());
                else if (kind == 1) session.PostProviderException(new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)));
                else for (var i = 0; i <= session.Limits.MaxEvidenceItems; i++) session.PostException(new IOException());
            }
        });
        Assert.Null(run.Primary); Assert.Equal(kind != 0, run.ActionsClosing);
        if (kind == 0)
        { Assert.NotNull(plannerRun.RequestPlanner()); Assert.NotNull(approvalRun.ApproveOperation(1, TimeSpan.FromSeconds(1))); operation.BeginDispatch(0); }
        else
        {
            Assert.Null(plannerRun.RequestPlanner()); Assert.Null(approvalRun.ApproveOperation(1, TimeSpan.FromSeconds(1)));
            Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
            Assert.Equal(before, run.Budget.Snapshot); Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]);
            run.Evaluate(); Assert.Equal(RunReason.ExecutionError, run.Primary!.Cause.Reason);
        }
    }
    [Fact]
    public async Task LinkedPreparationExitCancellationPreservesOperationTokenAndOriginalEvidence()
    {
        var clock = new Clock(); var launcher = new Launcher();
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), new Trace());
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(HostPreparation).GetField("preparationProcess", flags)!.SetValue(preparation, launcher.Process);
        using var caller = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var method = typeof(HostPreparation).GetMethod("WatchPreparationAsync", flags)!.MakeGenericMethod(typeof(bool));
        var watched = ((ValueTask<bool>)method.Invoke(preparation, [pending.Task, caller.Token])!).AsTask();
        caller.Cancel();
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => watched.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(caller.Token, failure.CancellationToken);
        var original = Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.NotEqual(caller.Token, original.CancellationToken); Assert.NotNull(original.StackTrace);
        pending.TrySetResult(true);
    }
    [Fact]
    public void DispatchRechecksPostedCauseAfterItsClockRead()
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); run.BeginRunning();
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!; var before = run.Budget.Snapshot;
        clock.OnRead = () => { clock.OnRead = null; run.PostProviderException(new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner))); };
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
        Assert.Null(run.Primary); Assert.True(run.ActionsClosing); Assert.Equal(before, run.Budget.Snapshot);
        Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangeAndCaptureShareActualSourceOwnershipUntilObsoleteCallEnds(bool firstWait)
    {
        var connection = new Connection { BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously), BlockedWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = 0; void Started(CancellationToken _) { if (Interlocked.Increment(ref sources) == 1) firstStarted.TrySetResult(); else secondStarted.TrySetResult(); }
        connection.OnCapture = Started; connection.OnWait = Started;
        var secondEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        var feed = new ProcessObservationFeed(connection, null, _ => { if (++reads == 2) secondEntry.TrySetResult(); return ValueTask.FromResult(false); }, _ => { }, _ => { });
        using var obsolete = new CancellationTokenSource();
        var first = firstWait ? feed.WaitForChangeAsync(obsolete.Token).AsTask() : (Task)feed.CaptureAsync(obsolete.Token).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); obsolete.Cancel();
        var second = firstWait ? (Task)feed.CaptureAsync(CancellationToken.None).AsTask() : feed.WaitForChangeAsync(CancellationToken.None).AsTask();
        await secondEntry.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(secondStarted.Task.IsCompleted); Assert.Equal(1, sources);
        var observation = new RunObservation(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([]));
        if (firstWait) connection.BlockedWait.SetResult(); else connection.BlockedCapture.SetResult(observation);
        await first.WaitAsync(TimeSpan.FromSeconds(5)); await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (firstWait) connection.BlockedCapture.SetResult(observation); else connection.BlockedWait.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, sources);
    }
    [Fact]
    public async Task SelectedLateLifecycleFailureOwesAlreadyRegisteredTraceThroughCleanup()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup(); var trace = new Trace();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), trace).PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary); await host.Feed.CaptureAsync(CancellationToken.None);
        trace.OnRecord = item => { if (item.Code == PreparationCode.LaunchFailed) { entered.TrySetResult(); try { release.Wait(); } finally { returned.TrySetResult(); } } };
        try
        {
            launcher.Process.FailExit(new IOException()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.Evaluate(); var outcome = await cleanup.CompleteAsync(run, clock).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, outcome.ExitCode); Assert.False(outcome.PostProcessingComplete);
            Assert.Contains(outcome.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task LateLifecycleCausePublicationCannotPrecedeTraceRegistration()
    {
        var process = new Process { DirectExitWatch = true }; var registered = 0;
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ProcessObservationFeed(new Connection(), process, _ => ValueTask.FromResult(false),
            item => { Assert.Equal(PreparationCode.LaunchFailed, item.Code); Volatile.Write(ref registered, 1); }, _ => { },
            _ => published.TrySetResult(Volatile.Read(ref registered) == 1));
        await feed.CaptureAsync(CancellationToken.None); process.FailExit(new IOException());
        Assert.True(await published.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DispatchAndCausalPostHaveOneReservationLinearizationBoundary()
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); run.BeginRunning();
            var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
            using var ready = new ManualResetEventSlim();
            var posted = Task.Run(() =>
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
                run.PostProviderException(new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)));
                return operation.Actions!.Deliveries[0];
            });
            clock.OnRead = () => { clock.OnRead = null; ready.Set(); };
            var dispatched = true;
            try { operation.BeginDispatch(0); } catch (InvalidOperationException) { dispatched = false; }
            var stateAtPost = await posted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(run.ActionsClosing);
            if (stateAtPost == DeliveryState.Reserved)
            { Assert.False(dispatched); Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]); Assert.Equal(0, run.Budget.Snapshot.Actions); }
            else
            { Assert.True(dispatched); Assert.Equal(DeliveryState.Uncertain, stateAtPost); Assert.Equal(1, run.Budget.Snapshot.Actions); }
        }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task LatePreparationExitFaultPostsHostCauseAndTraceUnlessFrozenOrExpectedCancellation(bool frozen, bool cancelled)
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); run.BeginPreparation();
        var cleanup = new OwnedCleanup(); var trace = new Trace();
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)) };
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), trace);
        var host = await preparation.PrepareAsync(run, cleanup, null, null);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken watchToken = default;
        launcher.Process.ExitWatchTask = token => { watchToken = token; return late.Task; };
        var posted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(HostPreparation).GetField("recordProviderFailure", flags)!.SetValue(preparation,
            (Action<Exception>)(exception => { run.PostProviderException(exception); posted.TrySetResult(exception); }));
        var method = typeof(HostPreparation).GetMethod("WatchPreparationAsync", flags)!.MakeGenericMethod(typeof(bool));
        Assert.True(await (ValueTask<bool>)method.Invoke(preparation, [Task.FromResult(true), CancellationToken.None])!);
        Assert.True(watchToken.IsCancellationRequested); launcher.Process.ExitWatchTask = null;
        RunSnapshot? snapshot = null;
        if (frozen) { run.BeginRunning(host.Boundary); run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false)); snapshot = run.CapturePrimary(); }
        Exception original; try { throw new IOException(); } catch (Exception exception) { original = exception; }
        if (cancelled) late.SetCanceled(watchToken);
        else
        {
            late.SetException(original); var evidence = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(original, evidence.InnerException); Assert.Equal(PreparationCode.LaunchFailed, Assert.IsType<PreparationException>(evidence).Code);
        }
        if (!frozen) { run.BeginRunning(host.Boundary); run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false)); }
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(cancelled || frozen ? 0 : 1, outcome.ExitCode); Assert.Equal(1, launcher.Process.Shutdowns);
        if (frozen) { Assert.Equal(snapshot!.Primary, outcome.Primary); Assert.Equal(snapshot.Exceptions, outcome.Exceptions); }
        else if (!cancelled)
        { Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin); Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null); }
        if (cancelled) Assert.DoesNotContain(trace.Events, item => item.Code == PreparationCode.LaunchFailed);
        else Assert.Contains(trace.Events, item => item.Code == PreparationCode.LaunchFailed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CertificationClockInvalidOperationRetainsOriginalAndIsNotStaleObservation(bool condition)
    {
        var real = new Clock(); var simulation = new Clock(); var provider = condition ? simulation : real;
        var run = new RunSession(Run(real).Limits, real, simulation); var trace = new Trace();
        provider.OnRead = () =>
        {
            if (new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "ValidateStartTimes"))
            { provider.OnRead = null; throw new InvalidOperationException("provider-clock-certification"); }
        };
        var outcome = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), async (session, cleanup, token) =>
            (await new HostPreparation(Policy(), real, new Launcher(), new Connector(new()), trace).PrepareAsync(session, cleanup, null, null, token)).Boundary,
            (_, _) => ValueTask.FromResult(true));
        Assert.Equal(new(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Clock), outcome.Primary.Cause);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(InvalidOperationException).FullName &&
            item.StackTrace?.Contains(nameof(CertificationClockInvalidOperationRetainsOriginalAndIsNotStaleObservation)) == true);
        Assert.DoesNotContain(trace.Events, item => item.Stage == PreparationStage.Synchronize && item.Code == PreparationCode.StaleObservation);
    }

    private sealed class CapabilitySet(Action contains) : IReadOnlySet<string>
    {
        private readonly HashSet<string> values = ["observe"];
        public bool Contains(string value) { contains(); return values.Contains(value); }
        public int Count => values.Count;
        public IEnumerator<string> GetEnumerator() => values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<string> other) => values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => values.Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => values.SetEquals(other);
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CapabilityReadsAreBoundedAtInitialAndCapturedIdentity(bool captured, bool cancel)
    {
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capabilities = new CapabilitySet(() => { entered.TrySetResult(); try { release.Wait(); } finally { returned.TrySetResult(); } });
        var clock = new Clock(); var connection = new Connection(); var run = Run(clock); var trace = new Trace();
        if (captured) connection.CapturedCapabilities = capabilities;
        else connection.Identity = connection.Identity with { Capabilities = capabilities };
        try
        {
            var pending = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
            { return (await new HostPreparation(Policy(), clock, new Launcher(), new Connector(connection), trace)
                .PrepareAsync(session, cleanup, null, null, token)).Boundary; }, (_, _) => ValueTask.FromResult(true), cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel(); else clock.Advance(1000);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? RunReason.Cancelled : RunReason.PreparationTimeout, result.Primary.Cause.Reason);
            Assert.Null(run.RunningOrigin); Assert.Equal(captured ? 1 : 0, connection.Synchronizations);
            // If cleanup closes before the abandoned catch registers its write,
            // registration is rejected and diagnostics must stay unconfirmed.
            Assert.True(trace.Events.Contains(new(PreparationStage.Identity, cancel ? PreparationCode.Cancelled : PreparationCode.Timeout)) ||
                result.PostProcessing.Any(item => item.Reason == PostProcessingReason.DiagnosticsFailed));
            Assert.False(result.PostProcessingComplete); Assert.Equal(1, connection.Releases);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ThrowingCapabilityReadPreservesIdentityStageAndProviderEvidence(bool captured)
    {
        var clock = new Clock(); var connection = new Connection(); var trace = new Trace();
        var capabilities = new CapabilitySet(() => throw new IOException());
        if (captured) connection.CapturedCapabilities = capabilities;
        else connection.Identity = connection.Identity with { Capabilities = capabilities };
        var result = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(new(PreparationStage.Identity, PreparationCode.IdentityUnavailable), trace.Events);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
        Assert.True(result.PostProcessingComplete);
    }

    private sealed class GateRaceClock : IClock
    {
        public Action? OnRead { get; set; }
        public TimeSpan Elapsed { get { OnRead?.Invoke(); return TimeSpan.Zero; } }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
    [Theory] [InlineData(HostMode.Attach)] [InlineData(HostMode.Launch)]
    public async Task TerminalOwnershipConfirmationPreventsProviderStartPastFinalClockCheck(HostMode mode)
    {
        var clock = new GateRaceClock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var policy = Policy(mode); var launcher = new Launcher(); var connector = new Connector(new()); RunOutcome? closed = null;
        var lateWrites = 0;
        var trace = new Trace { OnRecord = item =>
        {
            if (item.Code is PreparationCode.Busy or PreparationCode.Cancelled) { lateWrites++; throw new IOException("late-trace"); }
            if (item.Stage != PreparationStage.Started) return;
            var reads = 0;
            clock.OnRead = () =>
            {
                // Force owner completion at the final clock guard inside the provider step,
                // after the finite operation's cancellation check but before provider start.
                if (++reads != 3) return;
                clock.OnRead = null;
                run.Evaluate(cancelled: true); closed = cleanup.CompleteAsync(run, clock).AsTask().GetAwaiter().GetResult();
            };
        } };
        await Assert.ThrowsAsync<PreparationException>(() => new HostPreparation(policy, clock, launcher, connector, trace)
            .PrepareAsync(run, cleanup, null, null).AsTask());
        Assert.NotNull(closed); Assert.False(closed.PostProcessingComplete);
        Assert.Contains(closed.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed);
        Assert.Equal(0, lateWrites);
        Assert.Equal(0, launcher.Calls); Assert.Equal(0, connector.Calls);
        var nextClock = new Clock(); var nextConnector = new Connector(new());
        await Execute(new(policy, nextClock, new Launcher(), nextConnector, new Trace()), nextClock);
        Assert.Equal(1, nextConnector.Calls);
    }

    [Fact]
    public async Task FullCleanupRegistryRollsBackLeaseBeforeAnyHostResourceStarts()
    {
        var clock = new Clock(); var policy = Policy(); var cleanup = new OwnedCleanup(); var first = new Connector(new());
        for (var index = 0; index < 1000; index++) cleanup.Register(CleanupStage.Diagnostics, _ => ValueTask.FromResult(true));
        await RunExecutor.ExecuteAsync(Run(clock), clock, cleanup, async (session, owned, token) =>
        { return (await new HostPreparation(policy, clock, new Launcher(), first, new Trace())
            .PrepareAsync(session, owned, null, null, token)).Boundary; }, (_, _) => ValueTask.FromResult(true));
        Assert.Equal(0, first.Calls);
        var second = new Connector(new()); var trace = new Trace();
        await Execute(new(policy, clock, new Launcher(), second, trace), clock);
        Assert.Equal(1, second.Calls); Assert.DoesNotContain(new(PreparationStage.Ownership, PreparationCode.Busy), trace.Events);
    }
    [Fact]
    public async Task BusyTraceCanWaitForAnUnrelatedEndpointWithoutHoldingGlobalLeaseLock()
    {
        var clock = new Clock(); var policy = Policy();
        await Execute(new(policy, clock, new Launcher(), new Connector(new() { ReleaseConfirmed = false }), new Trace()), clock);
        var unrelated = new Connector(new());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new Trace { OnRecord = item =>
        {
            if (item.Code != PreparationCode.Busy) return;
            Task.Run(async () => await Execute(new(Policy(), clock, new Launcher(), unrelated, new Trace()), clock))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            completed.TrySetResult();
        } };
        await Execute(new(policy, clock, new Launcher(), new Connector(new()), trace), clock);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, unrelated.Calls);
    }

    [Fact]
    public async Task UnendedSetupEffectCannotOpenEndpointAfterItsConnectionRelease()
    {
        var clock = new Clock(); var policy = Policy(); var connection = new Connection();
        var setup = new Setup { PendingReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var pending = Execute(new(policy, clock, new Launcher(), new Connector(connection), new Trace()), clock, setup);
            await setup.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(1000);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason); Assert.Equal(1, connection.Releases);
            Assert.False(outcome.PostProcessingComplete);
            var next = new Connector(new()); var trace = new Trace();
            await Execute(new(policy, clock, new Launcher(), next, trace), clock);
            Assert.Equal(0, next.Calls); Assert.Contains(new(PreparationStage.Ownership, PreparationCode.Busy), trace.Events);
        }
        finally { setup.PendingReceipt.SetResult(SetupReceipt.Confirmed); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitialTraceCannotBlockDeadlineOrCancellation(bool cancel)
    {
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new Trace { OnRecord = _ => { entered.TrySetResult(); try { release.Wait(); } finally { returned.TrySetResult(); } } };
        var clock = new Clock(); var connector = new Connector(new()); var launcher = new Launcher();
        try
        {
            var pending = RunExecutor.ExecuteAsync(Run(clock), clock, new OwnedCleanup(), async (session, owned, token) =>
                (await new HostPreparation(Policy(HostMode.Launch), clock, launcher, connector, trace).PrepareAsync(session, owned, null, null, token)).Boundary,
                (_, _) => ValueTask.FromResult(true), cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel(); else clock.Advance(1000);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? RunReason.Cancelled : RunReason.PreparationTimeout, result.Primary.Cause.Reason);
            Assert.Equal(0, connector.Calls); Assert.Equal(0, launcher.Calls);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RejectingTracePreservesTypedIdentityAndBusyFailure(bool busy)
    {
        var clock = new Clock(); var policy = Policy(); var connection = new Connection();
        if (busy) await Execute(new(policy, clock, new Launcher(), new Connector(new() { ReleaseConfirmed = false }), new Trace()), clock);
        else connection.Identity = connection.Identity with { Protocol = "wrong" };
        var trace = new Trace { OnRecord = item => { if (item.Code is PreparationCode.Busy or PreparationCode.IdentityMismatch) throw new IOException(); } };
        var result = await Execute(new(policy, clock, new Launcher(), new Connector(connection), trace), clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(result.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed &&
            item.Exception is { } exception && exception.Type == typeof(IOException).FullName && exception.StackTrace is not null);
        Assert.Contains(trace.Events, item => item.Code == (busy ? PreparationCode.Busy : PreparationCode.IdentityMismatch));
    }
    [Fact]
    public async Task ProcessExitInterruptsRetryThatWouldExhaustPreparation()
    {
        var clock = new Clock(); var launcher = new Launcher(); var connector = new Connector(new()) { NotReadyCount = 1 };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new Trace(); var policy = Policy(HostMode.Launch) with { RetryDelay = TimeSpan.FromSeconds(3) };
        clock.OnDelay = duration => { if (duration == policy.RetryDelay) started.TrySetResult(); };
        var pending = Execute(new(policy, clock, launcher, connector, trace), clock);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); launcher.Process.Exit();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
        Assert.Equal(1, connector.Calls); Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Fact]
    public async Task DeepUniqueAggregateWrappersCannotHideOriginalLeafEvidence()
    {
        var clock = new Clock(); var run = new RunSession(new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 32), clock, clock);
        run.BeginPreparation(); Exception leaf;
        try { throw new IOException(); } catch (IOException exception) { leaf = exception; }
        Exception graph = leaf; for (var index = 0; index < 256; index++) graph = new AggregateException(graph);
        run.RecordException(graph); run.Evaluate(cancelled: true); var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(32, outcome.Exceptions.Count);
        Assert.Contains(outcome.Exceptions, item => item.Type == leaf.GetType().FullName && item.StackTrace == leaf.StackTrace);
    }

    [Fact]
    public void NullEndpointFollowsPolicyValidation()
    {
        Assert.Throws<ArgumentException>(() => new HostPreparation(Policy() with { Endpoint = null! }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SourceFailureOriginDoesNotDependOnLoserCancellationFault(bool callbackFault)
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection { CaptureFailure = new IOException() };
        if (callbackFault) launcher.Process.OnExitWait = token => token.Register(() => throw new FormatException());
        PreparedHost? host = null;
        var result = await RunExecutor.ExecuteAsync(Run(clock), clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(session, cleanup, null, null, token); return host.Boundary; },
            async (_, token) => { await host!.Feed.CaptureAsync(token); return true; });
        Assert.Equal(10, result.ExitCode); Assert.Equal(RunOrigin.Runner, result.Primary.Cause.Origin);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName);
        Assert.Equal(callbackFault, result.Exceptions.Any(item => item.Type == typeof(FormatException).FullName));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ThrowingProcessStatusKeepsHostOriginAtPreparationAndExecution(bool execution)
    {
        var clock = new Clock(); var launcher = new Launcher(); PreparedHost? host = null;
        if (!execution) launcher.Process.OnStatus = () => throw new IOException();
        var result = await RunExecutor.ExecuteAsync(Run(clock), clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), new Trace())
            .PrepareAsync(session, cleanup, null, null, token); return host.Boundary; },
            async (_, token) => { launcher.Process.OnStatus = () => throw new IOException(); await host!.Feed.CaptureAsync(token); return true; });
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Equal(execution ? RunPhase.Execution : RunPhase.Preparation, result.Primary.Cause.Phase);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BlockingProcessStatusIsBoundedInBothPhases(bool execution)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock(); var launcher = new Launcher(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var policy = Policy(HostMode.Launch) with { OperationTimeout = TimeSpan.FromMilliseconds(200) };
        void Block() { entered.TrySetResult(); try { release.Wait(); } finally { returned.TrySetResult(); } }
        try
        {
            if (!execution) launcher.Process.OnStatus = Block;
            var preparation = new HostPreparation(policy, clock, launcher, new Connector(new()), new Trace());
            var pending = preparation.PrepareAsync(run, cleanup, null, null).AsTask();
            if (execution)
            {
                var host = await pending; run.BeginRunning(host.Boundary); launcher.Process.OnStatus = Block;
                var capture = host.Feed.CaptureAsync(CancellationToken.None).AsTask();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var exception = await Assert.ThrowsAsync<PreparationException>(() => capture.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(PreparationStage.Launch, exception.Stage); Assert.Equal(RunOrigin.Host, exception.Cause.Origin);
            }
            else
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(200);
                await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(0, launcher.Process.Shutdowns);
            run.Evaluate(cancelled: true); await cleanup.CompleteAsync(run, clock);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task UnknownConnectionReleaseStillClosesAllPreparationProviderStarts()
    {
        var clock = new GateRaceClock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var connection = new Connection { ReleaseConfirmed = false }; var setup = new Setup(); RunOutcome? closed = null;
        var identities = 0;
        var trace = new Trace { OnRecord = item =>
        {
            if (item.Stage != PreparationStage.Identity || item.Code != PreparationCode.Completed || ++identities != 2) return;
            var reads = 0; clock.OnRead = () =>
            {
                if (++reads != 3) return; clock.OnRead = null;
                run.Evaluate(cancelled: true); closed = cleanup.CompleteAsync(run, clock).AsTask().GetAwaiter().GetResult();
            };
        } };
        await Assert.ThrowsAsync<PreparationException>(() => new HostPreparation(Policy(), clock, new Launcher(), new Connector(connection), trace)
            .PrepareAsync(run, cleanup, setup, null).AsTask());
        Assert.NotNull(closed); Assert.False(closed.PostProcessingComplete); Assert.Equal(1, connection.Releases); Assert.Equal(0, setup.Calls);
    }

    [Fact]
    public void NullLaunchArgumentsFollowPolicyValidation()
    {
        var policy = Policy(HostMode.Launch); var error = Assert.Throws<ArgumentException>(() => new HostPreparation(
            policy with { Launch = policy.Launch! with { Arguments = null! } }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
        Assert.Equal("policy", error.ParamName);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExecutionTraceRejectionIsAnUnconfirmedDiagnostic(bool block)
    {
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock(); var launcher = new Launcher(); PreparedHost? host = null;
        var trace = new Trace { OnRecord = item =>
        {
            if (item.Code != PreparationCode.ProcessExited) return;
            if (!block) throw new IOException();
            try { release.Wait(); } finally { returned.TrySetResult(); }
        } };
        try
        {
            var result = await RunExecutor.ExecuteAsync(Run(clock), clock, new OwnedCleanup(), async (session, cleanup, token) =>
            { host = await new HostPreparation(Policy(HostMode.Launch) with { OperationTimeout = TimeSpan.FromMilliseconds(100) }, clock,
                launcher, new Connector(new()), trace).PrepareAsync(session, cleanup, null, null, token); return host.Boundary; },
                async (_, token) => { launcher.Process.Exit(); await host!.Feed.CaptureAsync(token); return true; });
            Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
            Assert.False(result.PostProcessingComplete);
            Assert.Contains(result.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed);
            if (!block) Assert.Contains(result.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed && item.Exception?.Type == typeof(IOException).FullName);
            else
            {
                // Cleanup's fair stage budget may expire before the independent
                // provider deadline is delivered on a loaded runner. Either path
                // must stay unconfirmed; only an observed provider fault has type.
                Assert.True(result.PostProcessing.Any(item => item.Reason == PostProcessingReason.DiagnosticsFailed && item.Exception?.Type == typeof(TimeoutException).FullName) ||
                    result.PostProcessing.Any(item => item.Reason == PostProcessingReason.CleanupTimeout));
            }
        }
        finally { release.Set(); if (block) await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task RetryWatchFaultIsLaunchFailureWithoutSetup()
    {
        var clock = new Clock(); var launcher = new Launcher(); var trace = new Trace();
        var policy = Policy(HostMode.Launch) with { RetryDelay = TimeSpan.FromMilliseconds(250) };
        clock.OnDelay = duration => { if (duration == policy.RetryDelay) launcher.Process.ExitWaitFailure = new IOException(); };
        var connector = new Connector(new()) { NotReadyCount = 1 };
        var result = await Execute(new(policy, clock, launcher, connector, trace), clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.LaunchFailed), trace.Events);
        Assert.DoesNotContain(trace.Events, item => item.Code == PreparationCode.SetupFailed);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName);
        Assert.Equal(1, connector.Calls); Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RetryCancellationCallbackFaultRemainsSecondaryEvidence(bool exit)
    {
        var clock = new Clock(); var launcher = new Launcher(); var trace = new Trace();
        var policy = Policy(HostMode.Launch) with { RetryDelay = TimeSpan.FromMilliseconds(250) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnDelay = duration =>
        {
            if (duration != policy.RetryDelay) return;
            launcher.Process.OnExitWait = token =>
            { token.Register(() => throw new IOException()); launcher.Process.OnExitWait = null; started.TrySetResult(); };
        };
        var connector = new Connector(new() { Clock = clock }) { NotReadyCount = 1 };
        var pending = Execute(new(policy, clock, launcher, connector, trace), clock);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (exit) launcher.Process.Exit(); else clock.Advance(250);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(exit ? 1 : 5, result.ExitCode);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
        Assert.Equal(exit ? 1 : 2, connector.Calls);
        if (exit) Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
    }
    private sealed class RetryFaultClock(TimeSpan retry, Action fault) : IClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration == retry) { fault(); return ValueTask.FromException(new FormatException()); }
            return new(Task.Delay(Timeout.Infinite, token));
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReadyRetryClockFaultPrecedesStatusProbeAndRetainsConcurrentExit(bool exit)
    {
        var launcher = new Launcher(); var reads = 0; var policy = Policy(HostMode.Launch) with { RetryDelay = TimeSpan.FromMilliseconds(250) };
        var clock = new RetryFaultClock(policy.RetryDelay, () =>
        { launcher.Process.OnStatus = () => { reads++; throw new IOException(); }; if (exit) launcher.Process.Exit(); });
        var connector = new Connector(new()) { NotReadyCount = 1 };
        var result = await Execute(new(policy, clock, launcher, connector, new Trace()), clock).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, result.ExitCode); Assert.Equal(RunOrigin.Clock, result.Primary.Cause.Origin); Assert.Equal(0, reads);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(FormatException).FullName);
        Assert.Equal(exit, result.Exceptions.Any(item => item.Type == typeof(PreparationException).FullName));
        Assert.Equal(1, connector.Calls); Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ProcessExitInterruptsEveryBlockedPreparationProvider(int stage)
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection(); var trace = new Trace();
        var delayed = new DelayedConnector(); var setup = new Setup(); var planner = new Planner();
        IPreparationConnector connector = new Connector(connection); Task started;
        switch (stage)
        {
            case 0: connector = delayed; started = delayed.Started.Task; break;
            case 1: connection.PendingIdentity = new(TaskCreationOptions.RunContinuationsAsynchronously); started = connection.IdentityStarted.Task; break;
            case 2: setup.PendingReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously); started = setup.OperationStarted.Task; break;
            case 3: planner.PendingCheck = new(TaskCreationOptions.RunContinuationsAsynchronously); started = planner.Started.Task; break;
            default: connection.PendingBoundary = new(TaskCreationOptions.RunContinuationsAsynchronously); started = connection.SynchronizationStarted.Task; break;
        }
        try
        {
            var pending = Execute(new(Policy(HostMode.Launch, stage == 3 ? PlayMode.Explore : PlayMode.Replay), clock, launcher, connector, trace),
                clock, stage == 2 ? setup : null, planner);
            await started.WaitAsync(TimeSpan.FromSeconds(5)); launcher.Process.Exit();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
            Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
            Assert.False(result.PostProcessingComplete); Assert.Equal(1, launcher.Process.Shutdowns);
            Assert.Equal(stage == 0 ? 0 : 1, connection.Releases);
        }
        finally
        {
            delayed.Completion.TrySetResult(connection); connection.PendingIdentity?.TrySetResult(connection.Identity);
            setup.PendingReceipt?.TrySetResult(SetupReceipt.Confirmed); planner.PendingCheck?.TrySetResult(true);
            connection.PendingBoundary?.TrySetResult(null!);
        }
    }

    [Fact]
    public async Task RejectionTraceCannotHideReadyHostFailureAtPreparationDeadline()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock(); var connection = new Connection();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Identity = connection.Identity with { Capabilities = new CapabilitySet(() =>
        { clock.Advance(1900); throw new PreparationException(PreparationStage.Identity, PreparationCode.IdentityMismatch); }) };
        var trace = new Trace { OnRecord = item =>
        {
            if (item.Code != PreparationCode.IdentityMismatch) return;
            entered.TrySetResult(); try { release.Wait(); } finally { returned.TrySetResult(); }
        } };
        try
        {
            var preparation = new HostPreparation(Policy() with { OperationTimeout = TimeSpan.FromSeconds(2) }, clock, new Launcher(), new Connector(connection), trace);
            var run = Run(clock); var cleanup = new OwnedCleanup();
            cleanup.Register(CleanupStage.PrimarySnapshot, _ => { cleanupStarted.TrySetResult(); return ValueTask.FromResult(true); });
            var pending = RunExecutor.ExecuteAsync(run, clock, cleanup,
                async (session, owned, token) => (await preparation.PrepareAsync(session, owned, null, null, token)).Boundary,
                (_, _) => ValueTask.FromResult(true)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(100);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, result.ExitCode); Assert.Equal(RunReason.ExecutionError, result.Primary.Cause.Reason);
            Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin); Assert.False(result.PostProcessingComplete);
            Assert.Contains(result.PostProcessing, item => item.Reason == PostProcessingReason.DiagnosticsFailed);
        }
        finally { release.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SuccessfulExitWatchOverridesStaleStatusWithReadyOrPendingSource(bool pending)
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection(); var run = Run(clock);
        launcher.Process.DirectExitWatch = true;
        run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        if (pending) connection.BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnCapture = _ => launcher.Process.ConfirmExitWithStaleStatus();
        var failure = await Assert.ThrowsAsync<PreparationException>(() => host.Feed.CaptureAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PreparationCode.ProcessExited, failure.Code); Assert.False(launcher.Process.HasExited);
        Assert.Equal(RunOrigin.Host, failure.Cause.Origin);
        run.RecordException(failure); run.Evaluate(candidates: [failure.Cause]); var result = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(1, launcher.Process.Shutdowns);
        connection.BlockedCapture?.TrySetResult(new(TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([])));
    }
    [Fact]
    public async Task ExitWatchIsArmedBeforeSynchronousSourceThrow()
    {
        var clock = new Clock(); var launcher = new Launcher(); var connection = new Connection(); var run = Run(clock);
        // Configure the raw watch before preparation acquires its continuous watch.
        launcher.Process.DirectExitWatch = true;
        run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        var armed = false; launcher.Process.OnExitWait = _ => armed = true;
        connection.OnCapture = _ => { Assert.True(armed); throw new IOException(); };
        // Establish the thrown source evidence before signalling exit. Signalling
        // first would allow authoritative exit to win before the throw is ready.
        var failure = await Assert.ThrowsAsync<IOException>(() => host.Feed.CaptureAsync(CancellationToken.None).AsTask());
        Assert.True(armed); launcher.Process.ConfirmExitWithStaleStatus();
        run.RecordException(failure); run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)]);
        var result = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitialGoalCannotHideExitAfterPreparationOrDuringInitialClockRead(bool duringEvaluation)
    {
        var clock = new GateRaceClock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)) };
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); var calls = 0;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        {
            var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
                .PrepareAsync(session, cleanup, null, null, token);
            if (!duringEvaluation) launcher.Process.ConfirmExitWithStaleStatus();
            else clock.OnRead = () => { if (run.State == ExecutionState.Running) { clock.OnRead = null; launcher.Process.ConfirmExitWithStaleStatus(); } };
            return host.Boundary;
        }, (_, _) => { calls++; return ValueTask.FromResult(true); });
        Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Equal(RunReason.ExecutionError, outcome.Primary.Cause.Reason);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.GoalSatisfied);
        Assert.Equal(0, calls); Assert.Equal(0, connection.Captures); Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationCannotReceiveSourceCallbackFault(bool waiting)
    {
        var clock = new Clock(); var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var connection = new Connection(); var launcher = new Launcher();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Configure(CancellationToken token)
        {
            token.Register(() => { connection.BlockedCapture.TrySetCanceled(token); throw new IOException("source-cancel"); });
            started.TrySetResult();
        }
        connection.OnCapture = Configure; connection.OnWait = Configure;
        var pending = waiting ? host.Feed.WaitForChangeAsync(cancellation.Token).AsTask() : (Task)host.Feed.CaptureAsync(cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Record.Exception(() => cancellation.Cancel()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        run.Evaluate(cancelled: true); var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(RunReason.Cancelled, outcome.Primary.Cause.Reason);
        Assert.Contains(outcome.Exceptions, item => item.Type == typeof(IOException).FullName && item.StackTrace is not null);
    }
    [Fact]
    public async Task PostedProviderEvidenceFreezesBeforePrimarySnapshotAndLatePostsCannotMutateIt()
    {
        var clock = new GateRaceClock(); var run = Run(clock); run.BeginPreparation();
        Exception Fault() { try { throw new IOException(); } catch (Exception exception) { return exception; } }
        run.PostException(Fault());
        clock.OnRead = () => { clock.OnRead = null; Task.Run(() => run.PostException(Fault())).GetAwaiter().GetResult(); };
        run.Evaluate(cancelled: true); var primary = run.CapturePrimary();
        var before = run.Exceptions.Count;
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => run.PostException(Fault()))));
        var outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(2, before); Assert.Equal(primary.Exceptions, outcome.Exceptions);
        Assert.Equal(before, run.Exceptions.Count); Assert.All(outcome.Exceptions, item => Assert.NotNull(item.StackTrace));
    }

    [Fact]
    public async Task InterruptedCompletedBoundaryRetainsKnownProcessExitWithoutStartingRunning()
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)) };
        var trace = new Trace(); var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); var calls = 0;
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        {
            var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), trace)
                .PrepareAsync(session, cleanup, null, null, token);
            clock.OnRead = () => { clock.OnRead = null; launcher.Process.ConfirmExitWithStaleStatus(); clock.Advance(2000); };
            return host.Boundary;
        }, (_, _) => { calls++; return ValueTask.FromResult(true); });
        Assert.Null(run.RunningOrigin); Assert.Equal(0, calls); Assert.Equal(0, connection.Captures);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunReason.PreparationTimeout, result.Primary.Cause.Reason);
        Assert.Equal(RunPhase.Preparation, result.Primary.Cause.Phase);
        Assert.Contains(result.Events, item => item.Reason == RunReason.ExecutionError && item.Origin == RunOrigin.Host && item.Phase == RunPhase.Preparation);
        Assert.DoesNotContain(result.Events, item => item.Reason == RunReason.GoalSatisfied);
        Assert.Contains(result.Exceptions, item => item.Type == typeof(PreparationException).FullName);
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
        Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Fact]
    public async Task EmptySetupStillEnforcesItsWholeMetadataDeadline()
    {
        var clock = new Clock(); var connection = new Connection(); var trace = new Trace();
        var setup = new Setup { OperationIds = [], ReadTimeout = () => { clock.Advance(50); return TimeSpan.FromMilliseconds(40); } };
        var result = await Execute(new(Policy(), clock, new Launcher(), new Connector(connection), trace), clock, setup);
        Assert.Equal(RunReason.PreparationTimeout, result.Primary.Cause.Reason);
        Assert.Equal(0, setup.Calls); Assert.Equal(0, connection.Synchronizations);
        Assert.Contains(new(PreparationStage.Setup, PreparationCode.Timeout), trace.Events);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public async Task EntryStatusCannotHideExactExitWithStaleBlockingOrThrowingGetter(bool waiting, int mode)
    {
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup(); var connection = new Connection();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary); run.Evaluate();
        var reads = 0; var sourceStarts = 0;
        connection.OnCapture = _ => Interlocked.Increment(ref sourceStarts);
        connection.OnWait = _ => Interlocked.Increment(ref sourceStarts);
        launcher.Process.OnStatus = () =>
        {
            Interlocked.Increment(ref reads); entered.TrySetResult();
            if (mode == 0) throw new IOException("must-not-read-after-known-exit");
            if (mode == 2) { try { release.Wait(); } finally { returned.TrySetResult(); } }
            else launcher.Process.ConfirmExitWithStaleStatus();
            if (mode == 3) throw new IOException("status-failed-during-exit");
        };
        if (mode == 0) launcher.Process.ConfirmExitWithStaleStatus();
        try
        {
            var pending = waiting ? host.Feed.WaitForChangeAsync(cancellation.Token).AsTask() : (Task)host.Feed.CaptureAsync(cancellation.Token).AsTask();
            if (mode == 2)
            { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); launcher.Process.ConfirmExitWithStaleStatus(); cancellation.Cancel(); }
            var failure = await Assert.ThrowsAsync<PreparationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(PreparationCode.ProcessExited, failure.Code); Assert.Equal(0, connection.Captures);
            Assert.Equal(0, sourceStarts);
            Assert.Equal(mode == 0 ? 0 : 1, reads);
            launcher.Process.OnStatus = null;
            run.RecordException(failure); run.Evaluate(candidates: [failure.Cause], cancelled: cancellation.IsCancellationRequested);
            var outcome = await cleanup.CompleteAsync(run, clock);
            Assert.Equal(1, outcome.ExitCode); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        }
        finally { release.Set(); if (mode == 2) await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task ReadySourceUsesExactWatchWithoutAnotherPotentiallyBlockedStatusRead()
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); run.BeginPreparation();
        var cleanup = new OwnedCleanup(); var sourceReturned = false; var reads = 0;
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            CurrentObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)) };
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace())
            .PrepareAsync(run, cleanup, null, null); run.BeginRunning(host.Boundary); run.Evaluate();
        launcher.Process.OnStatus = () => { reads++; if (sourceReturned) throw new IOException("late-getter-must-not-strand-ready-unit"); };
        connection.OnCapture = _ => sourceReturned = true;
        var observation = await host.Feed.CaptureAsync(CancellationToken.None);
        Assert.Equal(1, reads); Assert.Equal(1, connection.Captures);
        run.Evaluate(observation.Success, observation.CapturedAt, failureUnit: observation.Failure);
        var result = await cleanup.CompleteAsync(run, clock); Assert.Equal(ResultStatus.Passed, result.Primary.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LifecycleOnlyFailureCollectsOutstandingDeliveryBeforePrimary(bool sent)
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = Run(clock); run.BeginPreparation(); var cleanup = new OwnedCleanup();
        var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary); run.Evaluate(); Assert.Null(run.Primary);
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); if (sent) operation.Actions!.ConfirmSent(0);
        launcher.Process.ConfirmExitWithStaleStatus(); run.Evaluate();
        Assert.Contains(run.Events, item => item.Reason == RunReason.ActionUnconfirmed && item.Origin == RunOrigin.Host);
        Assert.Contains(run.Events, item => item.Reason == RunReason.ExecutionError && item.Origin == RunOrigin.Host);
        Assert.Equal(RunReason.ActionUnconfirmed, run.Primary!.Cause.Reason);
        Assert.False(operation.IsOpen); Assert.False(operation.ConfirmResult());
        Assert.Equal(sent ? DeliveryState.Sent : DeliveryState.Uncertain, operation.Actions!.Deliveries[0]);
        Assert.Equal(1, run.Budget.Snapshot.Actions);
        var result = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(1, launcher.Process.Shutdowns);
        Assert.Contains(result.Events, item => item.Reason == RunReason.ActionUnconfirmed);
    }
    [Fact]
    public async Task ContinuousOwnerLifecycleCheckSurvivesTheFirstNonterminalEvaluation()
    {
        var clock = new Clock(); var launcher = new Launcher(); launcher.Process.DirectExitWatch = true;
        var run = new RunSession(Run(clock).Limits, clock, clock, BooleanCondition(), BooleanCondition()); run.BeginPreparation();
        var cleanup = new OwnedCleanup(); var host = await new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(new()), new Trace())
            .PrepareAsync(run, cleanup, null, null);
        run.BeginRunning(host.Boundary); run.Evaluate(); Assert.Null(run.Primary);
        clock.OnRead = () => { clock.OnRead = null; launcher.Process.ConfirmExitWithStaleStatus(); };
        run.Evaluate(BooleanUnit(true), TimeSpan.Zero, failureUnit: BooleanUnit(false));
        var result = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, result.ExitCode); Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        Assert.Contains(result.Events, item => item.Reason == RunReason.GoalSatisfied);
    }

}
