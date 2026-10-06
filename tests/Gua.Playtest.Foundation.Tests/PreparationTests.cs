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
        public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration == TimeSpan.FromMilliseconds(1)) { token.ThrowIfCancellationRequested(); Advance(1); return ValueTask.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            lock (timers) timers.Add((Elapsed + duration, completion));
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
        public List<PreparationEvent> Events { get; } = [];
        public Action<PreparationEvent>? OnRecord { get; set; }
        public void Record(PreparationEvent evidence) { Events.Add(evidence); OnRecord?.Invoke(evidence); }
    }
    private sealed class Process : IOwnedProcess
    {
        public bool HasExited { get; set; }
        public int Shutdowns { get; private set; }
        public Action<CancellationToken>? OnExitWait { get; set; }
        public Exception? ExitWaitFailure { get; set; }
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Exit() { HasExited = true; exited.TrySetResult(); }
        public ValueTask WaitForExitAsync(CancellationToken token)
        { OnExitWait?.Invoke(token); return ExitWaitFailure is null ? new(exited.Task.WaitAsync(token)) : ValueTask.FromException(ExitWaitFailure); }
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
        public TaskCompletionSource<IOwnedProcess> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token) => new(Completion.Task);
    }
    private sealed class Connection : IPreparationConnection, IRunObservationFeed
    {
        public IClock? Clock { get; set; }
        public HostIdentity Identity { get; set; } = new("game", "1", "Testing", "real", new HashSet<string> { "observe" }, "source", "epoch", false);
        public bool Stale { get; set; }
        public bool Preconditions { get; set; } = true;
        public bool WrongRequest { get; set; }
        public bool MissingCapturedCapabilities { get; set; }
        public bool MissingBoundary { get; set; }
        public bool MissingFeed { get; set; }
        public Exception? CaptureFailure { get; set; }
        public Exception? IdentityFailure { get; set; }
        public Exception? SynchronizeFailure { get; set; }
        public RunObservation? InitialObservation { get; set; }
        public RunObservation? CurrentObservation { get; set; }
        public TaskCompletionSource<RunObservation>? BlockedCapture { get; set; }
        public int Captures { get; private set; }
        public Action<CancellationToken>? OnCapture { get; set; }
        public int Releases { get; private set; }
        public int Synchronizations { get; private set; }
        public bool ReleaseConfirmed { get; set; } = true;
        public Action? OnRelease { get; set; }
        public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token)
        { if (IdentityFailure is not null) throw IdentityFailure; return ValueTask.FromResult(Identity); }
        public ValueTask<InitialBoundary> SynchronizeAsync(string captureRequestId, CancellationToken token)
        { if (SynchronizeFailure is not null) throw SynchronizeFailure; Synchronizations++; if (MissingBoundary) return ValueTask.FromResult<InitialBoundary>(null!);
            return ValueTask.FromResult(new InitialBoundary(Identity with { Epoch = Stale ? "old" : Identity.Epoch,
            Capabilities = MissingCapturedCapabilities ? null! : Identity.Capabilities }, true, Preconditions,
            WrongRequest ? "previous-request" : captureRequestId, Clock?.Elapsed ?? TimeSpan.Zero, "subscription-cursor-1", InitialObservation ?? Observation(), MissingFeed ? null! : this, false)); }
        private RunObservation Observation() => CurrentObservation ?? new(Clock?.Elapsed ?? TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([]));
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        { Captures++; OnCapture?.Invoke(token); return CaptureFailure is not null ? ValueTask.FromException<RunObservation>(CaptureFailure) : BlockedCapture is null ? ValueTask.FromResult(Observation()) : new(BlockedCapture.Task); }
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
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
        public Exception? FailureException { get; set; }
        public ValueTask<bool> CheckAsync(CancellationToken token) { Checks++; if (FailureException is not null) throw FailureException; return ValueTask.FromResult(true); }
    }
    private sealed class DelayedConnector : IPreparationConnector
    {
        public TaskCompletionSource<IPreparationConnection> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token) => new(Completion.Task);
    }
    private sealed class Setup : IApprovedSetup
    {
        public bool Authorized { get; set; } = true;
        private IReadOnlyList<string> operationIds = ["scene"];
        public Func<IReadOnlyList<string>>? ReadOperations { get; set; }
        public IReadOnlyList<string> OperationIds { get => ReadOperations is null ? operationIds : ReadOperations(); set => operationIds = value; }
        public IReadOnlySet<string> AllowedOperationIds { get; set; } = new HashSet<string> { "scene" };
        public Exception? OperationFailure { get; set; }
        public int MaximumOperations { get; set; } = 1;
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public int Calls { get; private set; }
        public SetupReceipt Receipt { get; set; } = SetupReceipt.Confirmed;
        public bool IsAuthorized(HostMode mode) => Authorized;
        public ValueTask<SetupReceipt> ExecuteOperationAsync(int index, IPreparationConnection connection, CancellationToken token)
        { Calls++; if (OperationFailure is not null) throw OperationFailure; return ValueTask.FromResult(Receipt); }
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
        Assert.Contains(new(PreparationStage.Launch, PreparationCode.ProcessExited), trace.Events);
        run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)]);
        await cleanup.CompleteAsync(run, clock);
        Assert.Equal(1, launcher.Process.Shutdowns);
    }
    [Fact]
    public async Task NoncooperativeLaunchCannotExtendPreparationAndLateProcessIsShutDown()
    {
        var clock = new Clock(); var launcher = new DelayedLauncher(); var policy = Policy(HostMode.Launch); var trace = new Trace();
        var pending = Execute(new(policy, clock, launcher, new Connector(new()), trace), clock);
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
        run.Evaluate(cancelled: true); await cleanup.CompleteAsync(run, clock);
        clock.Advance(1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
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
        var old = host.Feed.CaptureAsync(cancellation.Token).AsTask(); cancellation.Cancel();
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
        run.BeginRunning(host.Boundary); var capture = host.Feed.CaptureAsync(CancellationToken.None).AsTask(); launcher.Process.Exit();
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
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            CurrentObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)), BlockedCapture = old,
            OnCapture = token => token.Register(() => { if (!ignoresCancellation) old.TrySetCanceled(token); cancelled.TrySetResult();
                if (callbackFault) throw new IOException("capture-supersession"); }) };
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, new Launcher(), new Connector(connection), new Trace());
        PreparedHost? host = null;
        var pending = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await preparation.PrepareAsync(session, cleanup, null, null, token); return host.Boundary; },
            async (session, token) => (await RunMonitor.AwaitAsync(session, clock, clock, host!.Feed,
                _ => new ValueTask<int>(work.Task), _ => [], token)).Completed).AsTask();
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
        var pending = Execute(new(policy, clock, new Launcher(), connector, trace), clock);
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
            launcher.Process.ExitWaitFailure = typed;
            connection.BlockedCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnCapture = token => token.Register(() => throw new IOException("losing-capture-cancel"));
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
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EarlyWakeCannotStartRetryBeforeItsAbsoluteClockTarget(bool advance)
    {
        var clock = new EarlyWakeClock(); var connector = new Connector(new() { Clock = clock }) { NotReadyCount = 1 };
        var policy = Policy() with { RetryDelay = TimeSpan.FromMilliseconds(60) };
        var pending = Execute(new(policy, clock, new Launcher(), connector, new Trace()), clock);
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
        var launcher = new Launcher { Process = { ExitWaitFailure = new RunFailureException(new(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Contract)) } };
        var connection = new Connection { InitialObservation = new(TimeSpan.Zero, BooleanUnit(false), BooleanUnit(false)),
            CurrentObservation = new(TimeSpan.Zero, BooleanUnit(true), BooleanUnit(false)) };
        var preparation = new HostPreparation(Policy(HostMode.Launch), clock, launcher, new Connector(connection), new Trace()); PreparedHost? host = null;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (session, cleanup, token) =>
        { host = await preparation.PrepareAsync(session, cleanup, null, null, token); return host.Boundary; },
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
}
