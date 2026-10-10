using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;
using Gua.Playtest.Runner.Replay;
using Gua.Playtest.GuaIntegration;
using Gua.Testing.Recording;
using System.Text.Json;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class ReplayTests
{
    private const string Assertion = """{"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"id":{"value":"buy"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""";
    private static ConditionObservationUnit Unit(bool value) => new([KeyValuePair.Create("$", new ConditionLeafObservation("source:epoch:scope", true,
        [new ConditionTargetObservation("source:epoch:buy", "{\"type\":\"bool\",\"value\":" + (value ? "true" : "false") + "}")], true))]);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "playtest-replay-" + Guid.NewGuid().ToString("N"));
        public string PlanPath => Path.Combine(Root, "plan.json");
        public Fixture(int steps = 2, string completion = "afterPlan", int[]? checkpoints = null, bool initial = false, bool failure = false, bool gameInput = false)
        {
            Directory.CreateDirectory(Root);
            var scenario = new JsonObject
            {
                ["kind"] = "scenario", ["schemaVersion"] = 1, ["scenarioId"] = "purchase", ["definitionVersion"] = "v1", ["name"] = "purchase",
                ["goal"] = new JsonObject { ["objective"] = "purchase", ["success"] = JsonNode.Parse(Assertion) },
                ["constraints"] = new JsonObject { ["maxDurationMilliseconds"] = 10000, ["maxActions"] = 1000 }
            };
            if (failure) scenario["goal"]!["failure"] = JsonNode.Parse(Assertion);
            var recording = new JsonObject { ["schemaVersion"] = 1, ["steps"] = new JsonArray(Enumerable.Range(0, steps).Select(i => (JsonNode)new JsonObject
            {
                ["action"] = "click", ["target"] = new JsonObject { ["id"] = "buy" }, ["relativeMilliseconds"] = i,
                ["preRevision"] = i, ["postRevision"] = i + 1, ["sensitive"] = false
            }).ToArray()) };
            if (gameInput)
            {
                recording["schemaVersion"] = 2;
                for (var i = 0; i < steps; i++)
                {
                    var step = recording["steps"]![i]!.AsObject(); step.Remove("target");
                    step["action"] = "game_input"; step["operation"] = i == 0 ? "key_down" : "key_up";
                    step["relativeMilliseconds"] = 0; step["arguments"] = new JsonObject { ["code"] = "KeyA", ["leaseMs"] = 5000 };
                }
            }
            File.WriteAllText(Path.Combine(Root, "scenario.json"), scenario.ToJsonString());
            File.WriteAllText(Path.Combine(Root, "recording.json"), recording.ToJsonString());
            var plan = new JsonObject
            {
                ["kind"] = "replayPlan", ["schemaVersion"] = 1, ["planId"] = "purchase-replay",
                ["scenario"] = Ref("scenario.json"), ["recording"] = Ref("recording.json"),
                ["timing"] = "conditionSynchronized", ["completion"] = completion,
                ["checkpoints"] = new JsonArray((checkpoints ?? []).Select(step => (JsonNode)new JsonObject
                { ["beforeStep"] = step, ["condition"] = JsonNode.Parse(Assertion), ["timeoutMilliseconds"] = 1000 }).ToArray())
            };
            if (initial) plan["initial"] = JsonNode.Parse(Assertion);
            File.WriteAllText(PlanPath, plan.ToJsonString());
        }
        private JsonObject Ref(string name) => new() { ["path"] = name, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, name)))) };
        public ValueTask<ResolvedReplay> Load() => ResolvedReplay.LoadAsync(PlanPath, [Root], new(10, 1000));
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Feed(IClock clock) : IReplayObservationFeed
    {
        public bool Goal = true, Failure, Checkpoint = true;
        public Action? OnCapture { get; set; }
        public Action? OnWait { get; set; }
        public ConditionObservationUnit? PointOverride { get; set; }
        public int CheckpointCaptures;
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        { OnCapture?.Invoke(); return ValueTask.FromResult(new RunObservation(clock.Elapsed, Unit(Goal), Unit(Failure))); }
        public async ValueTask<ReplayObservation> CaptureAsync(PreparedCondition point, CancellationToken token)
        { CheckpointCaptures++; return new(await CaptureAsync(token), PointOverride ?? Unit(Checkpoint)); }
        public ValueTask WaitForChangeAsync(CancellationToken token)
        { if (OnWait is { } wake) { wake(); return ValueTask.CompletedTask; } return new(Task.Delay(Timeout.InfiniteTimeSpan, token)); }
    }
    private sealed class Playback : IReplayPlayback
    {
        public int Sends, Plays, Checks;
        public int? RejectAfter;
        public ReplayReceiptStatus Status = ReplayReceiptStatus.Succeeded;
        public bool Neutral = true, SkipSend, Duplicate, GuardTwice;
        public Action? AfterPlayback;
        public Action? OnSend { get; set; }
        public Func<CancellationToken, ValueTask>? BeforeResult { get; set; }
        public List<int> Order { get; } = [];
        public ReplayCheck Check(ReplayBatch batch, bool starting = true) { Checks++; return RejectAfter is { } at && Sends >= at ? ReplayCheck.PermissionDenied : ReplayCheck.Approved; }
        public async ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken token)
        {
            Plays++;
            for (var i = 0; !SkipSend && i < batch.Count; i++)
            {
                var index = Duplicate ? 0 : i;
                await calls.SendAsync(index, beforeSend =>
                {
                    beforeSend(); if (GuardTwice) beforeSend();
                    Sends++; Order.Add(batch.BeforeStep + i); OnSend?.Invoke(); return new ReplaySend<bool>(true, true);
                }, token);
            }
            if (BeforeResult is { } wait) await wait(token);
            AfterPlayback?.Invoke();
            return new(Status, batch.Count, Neutral);
        }
    }
    private static RunLimits Limits(long actions = 10) => new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), actions, 2, 1, 1024);
    private static async Task<ReplayOutcome> Run(ResolvedReplay replay, Feed feed, IReplayPlayback playback, IClock clock,
        OwnedCleanup? cleanup = null, long actions = 10, bool initial = true, CancellationToken token = default)
        => await new ReplayDriver().ExecuteAsync(replay, Limits(actions), clock, clock, cleanup ?? new(), playback,
            async (run, _, cancellation) =>
            {
                var capture = run.ArmRunningBoundary(); var observation = await feed.CaptureAsync(cancellation);
                var boundary = capture.Certify(capture.RequestId, clock.Elapsed, observation, "fixture-synchronized", true);
                return new(new PreparedHost(boundary, feed, true), Unit(initial));
            }, token);

    [Fact]
    public async Task RetainedBytesDoNotRereadMutatedRecordingAndHashesDetectNewLoads()
    {
        using var files = new Fixture(); var replay = await files.Load(); var before = replay.CopyRecording().ToJsonString();
        File.AppendAllText(Path.Combine(files.Root, "recording.json"), " ");
        Assert.Equal(before, replay.CopyRecording().ToJsonString());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => files.Load().AsTask()); Assert.Equal("HashMismatch", error.Message);
        var copy = replay.CopyScenario(); copy.Goal.Success!.Clear(); Assert.NotEmpty(replay.CopyScenario().Goal.Success!);
    }
    [Fact]
    public async Task AfterPlanRetainsInitialGoalAndRunsWholeRecordingAndRepeatedFinalCheckpoints()
    {
        using var files = new Fixture(checkpoints: [0, 1, 2, 2]); var replay = await files.Load();
        var clock = new MonotonicClock(); var feed = new Feed(clock); var playback = new Playback();
        var result = await Run(replay, feed, playback, clock, actions: 2);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.Equal(new[] { 0, 1 }, playback.Order);
        Assert.True(result.Progress.PlanCompleted); Assert.Equal(4, result.Progress.CompletedCheckpoints);
        Assert.Equal(2, result.Progress.CompletedSteps); Assert.Null(result.Progress.OmittedFromStep);
    }
    [Fact]
    public async Task OnGoalInitialSuccessExplicitlyOmitsEntireSuffix()
    {
        using var files = new Fixture(completion: "onGoal"); var clock = new MonotonicClock(); var playback = new Playback();
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.Equal(0, playback.Plays);
        Assert.False(result.Progress.PlanCompleted); Assert.Equal(0, result.Progress.OmittedFromStep);
    }
    [Fact]
    public async Task FailedPlanReleaseCannotBeHiddenByEarlierGoal()
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var playback = new Playback { Neutral = false };
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(RunReason.ActionFailed, result.Run.Primary.Cause.Reason); Assert.True(result.Progress.GoalVerified);
        Assert.False(result.Progress.PlanCompleted);
    }
    [Fact]
    public async Task InitialMismatchPreventsEveryPlayRequest()
    {
        using var files = new Fixture(initial: true); var clock = new MonotonicClock(); var playback = new Playback();
        var result = await Run(await files.Load(), new(clock), playback, clock, initial: false);
        Assert.Equal(RunReason.GoalImpossible, result.Run.Primary.Cause.Reason); Assert.Equal(RunPhase.Preparation, result.Run.Primary.Cause.Phase);
        Assert.Equal(0, playback.Sends);
    }
    [Fact]
    public async Task RevokedPermissionStopsPartialBatchWithoutRetryOrRouteRepair()
    {
        using var files = new Fixture(3); var clock = new MonotonicClock(); var playback = new Playback { RejectAfter = 1 };
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.Equal(1, playback.Sends); Assert.Equal(1, playback.Plays);
        Assert.Equal(1, result.Progress.DispatchedSteps); Assert.False(result.Progress.PlanCompleted);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DuplicateDispatchAndDoubleBoundaryFailClosed(bool duplicate, bool twice)
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var playback = new Playback { Duplicate = duplicate, GuardTwice = twice };
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status); Assert.True(playback.Sends <= 1);
    }
    [Fact]
    public async Task FakeSuccessWithoutDispatchCannotCompletePlan()
    {
        using var files = new Fixture(); var clock = new MonotonicClock();
        var result = await Run(await files.Load(), new(clock), new Playback { SkipSend = true }, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.False(result.Progress.PlanCompleted);
    }
    [Fact]
    public async Task WholeBatchBudgetRejectionNeverPartiallyStarts()
    {
        using var files = new Fixture(3); var clock = new MonotonicClock(); var playback = new Playback();
        var result = await Run(await files.Load(), new(clock), playback, clock, actions: 2);
        Assert.Equal(0, playback.Sends); Assert.False(result.Progress.PlanCompleted); Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status);
    }
    [Fact]
    public async Task MandatoryFailureMonitoringContinuesAfterInitialGoal()
    {
        using var files = new Fixture(failure: true); var clock = new MonotonicClock(); var feed = new Feed(clock);
        var playback = new Playback { AfterPlayback = () => feed.Failure = true };
        var result = await Run(await files.Load(), feed, playback, clock);
        Assert.Equal(RunReason.FailureCondition, result.Run.Primary.Cause.Reason); Assert.True(result.Progress.GoalVerified);
    }
    [Fact]
    public async Task OnGoalRetainsSuccessUntilQueuedActionReceiptSettles()
    {
        using var files = new Fixture(1, completion: "onGoal"); var clock = new MonotonicClock();
        var feed = new Feed(clock) { Goal = false };
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new Playback { OnSend = () => feed.Goal = true,
            BeforeResult = async token => { await captured.Task.WaitAsync(token); await Task.Delay(50, token); } };
        feed.OnCapture = () => { if (feed.Goal) captured.TrySetResult(); };
        var result = await Run(await files.Load(), feed, playback, clock, actions: 1);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status);
        Assert.True(result.Progress.GoalVerified); Assert.Equal(1, playback.Sends);
    }
    [Fact]
    public async Task CompletedRecordingWithoutGoalCannotPass()
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var playback = new Playback();
        var result = await Run(await files.Load(), new(clock) { Goal = false }, playback, clock, actions: 2);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.True(result.Progress.PlanCompleted);
        Assert.Equal(2, result.Progress.CompletedSteps); Assert.False(result.Progress.GoalVerified);
    }
    [Fact]
    public async Task GoalCanArriveAfterFinalActionBudgetIsExhausted()
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var feed = new Feed(clock) { Goal = false };
        var finished = false; var playback = new Playback { AfterPlayback = () => finished = true };
        feed.OnWait = () => { if (finished) feed.Goal = true; };
        var result = await Run(await files.Load(), feed, playback, clock, actions: 2);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.True(result.Progress.PlanCompleted);
        Assert.True(result.Progress.GoalVerified); Assert.Equal(2, playback.Sends);
    }
    [Fact]
    public async Task FailedIntermediateCheckpointDoesNotPlayLaterSuffix()
    {
        using var files = new Fixture(checkpoints: [1]); var clock = new MonotonicClock(); var playback = new Playback();
        var result = await Run(await files.Load(), new(clock) { Checkpoint = false }, playback, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.Equal(1, playback.Sends);
        Assert.Equal(0, result.Progress.CompletedCheckpoints); Assert.False(result.Progress.PlanCompleted);
    }
    [Fact]
    public async Task SameCaptureMalformedCheckpointCannotBeHiddenByOnGoalSuccess()
    {
        using var files = new Fixture(completion: "onGoal", checkpoints: [0]); var replay = await files.Load();
        var malformed = new ConditionObservationUnit([KeyValuePair.Create("$", new ConditionLeafObservation("source:epoch:scope", true,
            [new ConditionTargetObservation("source:epoch:buy", "{\"type\":\"bool\",\"value\":\"bad\"}")], true))]);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var clock = new MonotonicClock(); var feed = new Feed(clock) { Goal = false, PointOverride = malformed };
            feed.OnCapture = () => { if (feed.CheckpointCaptures > 0) feed.Goal = true; };
            var playback = new Playback(); var result = await Run(replay, feed, playback, clock);
            Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status); Assert.Equal(0, playback.Sends);
        }
    }
    [Fact]
    public async Task SimulationGuardUsesSerializedOwnerWithoutNestedQueueDeadlock()
    {
        using var files = new Fixture(gameInput: true); var replay = await files.Load();
        var clock = new MonotonicClock(); var host = new SimulationHost();
        var playback = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved,
            new(1000, 100, 100, GuaSegmentClock.Simulation, false, false));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await Run(replay, new(clock), playback, clock, token: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(2, host.Sends); Assert.Equal(1, host.Ends); Assert.True(host.Releases > 0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimulationUsesRealDeadlineAndFreshCleanupWhenClockIsStopped(bool cancel)
    {
        using var files = new Fixture(gameInput: true); var replay = await files.Load(); var clock = new MonotonicClock();
        using var cancellation = new CancellationTokenSource();
        var host = new SimulationHost { MissingReceipts = !cancel, OnSend = cancel ? cancellation.Cancel : null };
        var playback = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved,
            new(1000, 100, 100, GuaSegmentClock.Simulation, false, false));
        var result = await Run(replay, new(clock), playback, clock, token: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3));
        // Cancellation after dispatch cannot erase an unconfirmed ordinary action, which
        // has the existing higher failure priority. Both causes remain in the event ledger.
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status);
        Assert.Equal(RunReason.ActionUnconfirmed, result.Run.Primary.Cause.Reason);
        if (cancel) Assert.Contains(result.Run.Events, e => e.Reason == RunReason.Cancelled);
        await host.Ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(host.Releases > 0); Assert.Equal(1, host.Ends);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrustedTimedCapabilityAndAdmissionFaultRetainOriginalEvidence(bool capability)
    {
        using var files = new Fixture(gameInput: true); var clock = new MonotonicClock();
        var original = new ArgumentException("trusted admission fault");
        var host = new SimulationHost { CapabilityFault = capability ? original : null };
        var playback = new GuaTimedReplay(host, (_, _) => throw original,
            new(1000, 100, 100, GuaSegmentClock.Simulation, false, false));
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.Equal(0, host.Sends);
        Assert.Contains(result.Run.Exceptions, e => e.Type == typeof(ArgumentException).FullName && e.StackTrace is not null);
    }
    private sealed class SimulationHost : IGuaTimedSegmentHost
    {
        public Exception? CapabilityFault { get; set; }
        public Action? OnSend { get; set; }
        public bool MissingReceipts { get; set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OrderedApplication => CapabilityFault is { } failure ? throw failure : true;
        public bool ApplicationTimes => false;
        public bool SameTickApplication => false;
        public string? SimulationScope => "controlled-fixture";
        public double SimulationMilliseconds => 0;
        public string? ExecutionFailureCode => null;
        public bool IsNeutral => true;
        public int Sends, Ends, Releases;
        public void Begin(GuaTimedSegment segment) { }
        public ulong Send(GuaTimedInput input, JsonElement? value, Action boundary) { boundary(); Sends++; OnSend?.Invoke(); return (ulong)Sends; }
        public GuaTimedCompletion? Poll(ulong id) => MissingReceipts ? null : new(true);
        public ulong ReleaseAll() { Releases++; return 100; }
        public void End() { Ends++; Ended.TrySetResult(); }
    }
    [Fact]
    public async Task SynchronousWaitStartupFailureCancelsStartedWaitAndRetainsLosingFault()
    {
        var clock = new MonotonicClock(); var run = new RunSession(Limits(), clock, clock);
        run.BeginPreparation(); run.BeginRunning();
        var cancelled = false; var started = new TaskCompletionSource();
        var losing = new IOException("notification fault while shutting down");
        var startup = new InvalidOperationException("timer startup fault");
        Func<CancellationToken, ValueTask>[] waits =
        [
            token => { token.Register(() => { cancelled = true; started.TrySetException(losing); }); return new(started.Task); },
            _ => throw startup
        ];
        var method = typeof(ReplayDriver).GetMethod("WaitAnyAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var waiting = (ValueTask)method.Invoke(null, [run, CancellationToken.None, waits])!;
        Assert.Same(startup, await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.AsTask()));
        Assert.True(cancelled); run.Evaluate();
        Assert.Contains(run.Exceptions, e => e.Type == typeof(IOException).FullName);
    }
    [Fact]
    public async Task PrimarySuccessAndUnconfirmedCleanupRemainSeparate()
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.InputRelease, _ => ValueTask.FromResult(false));
        var result = await Run(await files.Load(), new(clock), new Playback(), clock, cleanup);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.Equal(11, result.Run.ExitCode);
        Assert.Contains(result.Run.PostProcessing, p => p.Reason == PostProcessingReason.InputReleaseUnconfirmed);
    }
}
