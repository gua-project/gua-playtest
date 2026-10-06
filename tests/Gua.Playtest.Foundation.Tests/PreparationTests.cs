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
        public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration == TimeSpan.FromMilliseconds(1)) { token.ThrowIfCancellationRequested(); Elapsed += duration; return ValueTask.CompletedTask; }
            return new(Task.Delay(Timeout.Infinite, token));
        }
        public void Advance(int milliseconds) => Elapsed += TimeSpan.FromMilliseconds(milliseconds);
    }
    private sealed class Trace : IPreparationTrace
    {
        public List<PreparationEvent> Events { get; } = [];
        public void Record(PreparationEvent evidence) => Events.Add(evidence);
    }
    private sealed class Process : IOwnedProcess
    {
        public bool HasExited { get; set; }
        public int Shutdowns { get; private set; }
        public ValueTask WaitForExitAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
        public ValueTask<bool> ShutdownAsync(CancellationToken token)
        { Shutdowns++; HasExited = true; return ValueTask.FromResult(true); }
    }
    private sealed class Launcher : IProcessLauncher
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Process Process { get; } = new();
        public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token)
        { Calls++; if (Fail) throw new IOException("private-path"); return ValueTask.FromResult<IOwnedProcess>(Process); }
    }
    private sealed class Connection : IPreparationConnection, IRunObservationFeed
    {
        public IClock? Clock { get; set; }
        public HostIdentity Identity { get; set; } = new("game", "1", "Testing", "real", new HashSet<string> { "observe" }, "source", "epoch", false);
        public bool Stale { get; set; }
        public bool Preconditions { get; set; } = true;
        public bool WrongRequest { get; set; }
        public RunObservation? InitialObservation { get; set; }
        public int Releases { get; private set; }
        public int Synchronizations { get; private set; }
        public bool ReleaseConfirmed { get; set; } = true;
        public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token) => ValueTask.FromResult(Identity);
        public ValueTask<InitialBoundary> SynchronizeAsync(string captureRequestId, CancellationToken token)
        { Synchronizations++; return ValueTask.FromResult(new InitialBoundary(Identity with { Epoch = Stale ? "old" : Identity.Epoch }, true, Preconditions,
            WrongRequest ? "previous-request" : captureRequestId, Clock?.Elapsed ?? TimeSpan.Zero, "subscription-cursor-1", InitialObservation ?? Observation(), this, false)); }
        private RunObservation Observation() => new(Clock?.Elapsed ?? TimeSpan.Zero, new ConditionObservationUnit([]), new ConditionObservationUnit([]));
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => ValueTask.FromResult(Observation());
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
        public ValueTask<bool> ReleaseAsync(CancellationToken token) { Releases++; return ValueTask.FromResult(ReleaseConfirmed); }
    }
    private sealed class Connector(Connection connection) : IPreparationConnector
    {
        public int Calls { get; private set; }
        public int NotReadyCount { get; set; }
        public bool Unknown { get; set; }
        public Action? BeforeReturn { get; set; }
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token)
        {
            Calls++; if (Calls <= NotReadyCount) throw new ConnectionNotReadyException();
            if (Unknown) throw new IOException("unknown-connect-result");
            BeforeReturn?.Invoke(); return ValueTask.FromResult<IPreparationConnection>(connection);
        }
    }
    private sealed class Planner : IPreparationPlannerCheck
    {
        public int Checks { get; private set; }
        public ValueTask<bool> CheckAsync(CancellationToken token) { Checks++; return ValueTask.FromResult(true); }
    }
    private sealed class Setup : IApprovedSetup
    {
        public bool Authorized { get; set; } = true;
        public IReadOnlyList<string> OperationIds { get; set; } = ["scene"];
        public IReadOnlySet<string> AllowedOperationIds { get; } = new HashSet<string> { "scene" };
        public int MaximumOperations { get; set; } = 1;
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public int Calls { get; private set; }
        public SetupReceipt Receipt { get; set; } = SetupReceipt.Confirmed;
        public bool IsAuthorized(HostMode mode) => Authorized;
        public ValueTask<SetupReceipt> ExecuteOperationAsync(int index, IPreparationConnection connection, CancellationToken token)
        { Calls++; return ValueTask.FromResult(Receipt); }
    }
    private static PreparationPolicy Policy(HostMode hostMode = HostMode.Attach, PlayMode playMode = PlayMode.Replay)
        => new(hostMode, playMode, new Uri($"ws://localhost:7777/{Guid.NewGuid():N}"), "game", "1", "Testing", "real", ["observe"], true,
            true, hostMode == HostMode.Launch ? new("explicit.exe", "explicit-directory", []) : null,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), 3, TimeSpan.FromSeconds(1));
    private static RunSession Run(Clock clock) => new(new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, 2, 100), clock, clock);
    private static async Task<RunOutcome> Execute(HostPreparation preparation, Clock clock, Setup? setup = null, Planner? planner = null)
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
        var outcome = await Execute(new(Policy(), clock, new Launcher(), connector, new Trace()), clock);
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status); Assert.Equal(3, connector.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(2), clock.Elapsed);
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
    public async Task MissingPortOrLaunchCannotGuessAnExecutable()
    {
        Assert.Throws<ArgumentException>(() => new HostPreparation(Policy() with { Endpoint = new("ws://localhost") }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
        Assert.Throws<ArgumentException>(() => new HostPreparation(Policy() with { HostMode = HostMode.Launch }, new Clock(), new Launcher(), new Connector(new()), new Trace()));
        await Assert.ThrowsAsync<PreparationException>(() => new SystemProcessLauncher().LaunchAsync(new("missing.exe", ".", []), CancellationToken.None).AsTask());
    }
}
