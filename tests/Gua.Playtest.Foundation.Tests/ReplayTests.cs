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

// Gua worker reads block until the owner pump runs. Fixed physical deadlines must
// not compete with unrelated collections deliberately blocking thread-pool workers.
// The races inside each Replay test remain concurrent; no timeout is enlarged.
[CollectionDefinition("Replay owner timing", DisableParallelization = true)]
public sealed class ReplayOwnerTimingCollection { }

[Collection("Replay owner timing")]
public sealed class ReplayTests
{
    private const string Assertion = """{"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"id":{"value":"buy"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""";
    private static ConditionObservationUnit Unit(bool value) => new([KeyValuePair.Create("$", new ConditionLeafObservation("source:epoch:scope", true,
        [new ConditionTargetObservation("source:epoch:buy", "{\"type\":\"bool\",\"value\":" + (value ? "true" : "false") + "}")], true))]);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "playtest-replay-" + Guid.NewGuid().ToString("N"));
        public string PlanPath => Path.Combine(Root, "plan.json");
        public Fixture(int steps = 2, string completion = "afterPlan", int[]? checkpoints = null, bool initial = false, bool failure = false, bool gameInput = false, int maxActions = 1000)
        {
            Directory.CreateDirectory(Root);
            var scenario = new JsonObject
            {
                ["kind"] = "scenario", ["schemaVersion"] = 1, ["scenarioId"] = "purchase", ["definitionVersion"] = "v1", ["name"] = "purchase",
                ["goal"] = new JsonObject { ["objective"] = "purchase", ["success"] = JsonNode.Parse(Assertion) },
                ["constraints"] = new JsonObject { ["maxDurationMilliseconds"] = 10000, ["maxActions"] = maxActions }
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
        public Func<ReplayObservation, ReplayObservation>? EnvelopeOverride { get; set; }
        public Func<PreparedCondition, CancellationToken, ValueTask>? BeforeCheckpointCapture { get; set; }
        public int CheckpointCaptures;
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        { OnCapture?.Invoke(); return ValueTask.FromResult(new RunObservation(clock.Elapsed, Unit(Goal), Unit(Failure))); }
        public async ValueTask<ReplayObservation> CaptureAsync(PreparedCondition point, CancellationToken token)
        { CheckpointCaptures++; if (BeforeCheckpointCapture is { } wait) await wait(point, token); var unit = new ReplayObservation(await CaptureAsync(token), PointOverride ?? Unit(Checkpoint)); return EnvelopeOverride?.Invoke(unit) ?? unit; }
        public ValueTask WaitForChangeAsync(CancellationToken token)
        { if (OnWait is { } wake) { wake(); return ValueTask.CompletedTask; } return new(Task.Delay(Timeout.InfiniteTimeSpan, token)); }
    }
    private sealed class Playback : IReplayPlayback
    {
        public int Sends, Plays, Checks;
        public int? RejectAfter;
        public ReplayReceiptStatus Status = ReplayReceiptStatus.Succeeded;
        public bool Neutral = true, SkipSend, Duplicate, GuardTwice, OmitGuard;
        public Action? AfterPlayback;
        public Func<ReplayCheck>? PreflightOverride;
        public Action? OnSend { get; set; }
        public Func<CancellationToken, ValueTask>? BeforeResult { get; set; }
        public Func<CancellationToken, ValueTask>? BetweenSends { get; set; }
        public List<int> Order { get; } = [];
        public ReplayCheck Check(ReplayBatch batch, bool starting = true) { Checks++; return PreflightOverride is { } preflight ? preflight() : RejectAfter is { } at && Sends >= at ? ReplayCheck.PermissionDenied : ReplayCheck.Approved; }
        public async ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken token)
        {
            Plays++;
            var prefix = 0;
            try
            {
            for (var i = 0; !SkipSend && i < batch.Count; i++)
            {
                var index = Duplicate ? 0 : i;
                await calls.SendAsync(index, beforeSend =>
                {
                    if (!OmitGuard) beforeSend(); if (GuardTwice) beforeSend();
                    Sends++; Order.Add(batch.BeforeStep + i); OnSend?.Invoke(); return new ReplaySend<bool>(true, true);
                }, token);
                prefix++;
                if (i == 0 && BetweenSends is { } between) await between(token);
            }
            }
            catch (ReplayDispatchClosedException) { return new(ReplayReceiptStatus.Succeeded, prefix, Neutral); }
            if (BeforeResult is { } wait) await wait(token);
            AfterPlayback?.Invoke();
            return new(Status, batch.Count, Neutral);
        }
    }
    private static RunLimits Limits(long actions = 10) => new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), actions, 2, 1, 1024);
    private static async Task<ReplayOutcome> Run(ResolvedReplay replay, Feed feed, IReplayPlayback playback, IClock clock,
        OwnedCleanup? cleanup = null, long actions = 10, bool initial = true, CancellationToken token = default, IClock? conditionClock = null, Action<RunSession>? inspect = null)
        => await new ReplayDriver().ExecuteAsync(replay, Limits(actions), clock, conditionClock ?? clock, cleanup ?? new(), playback,
            async (run, _, cancellation) =>
            {
                inspect?.Invoke(run);
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
        Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status); Assert.False(result.Progress.PlanCompleted);
        Assert.Equal(0, result.Progress.CompletedSteps); Assert.Equal(0, result.Progress.DispatchedSteps);
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
        Assert.True(result.Progress.PlanCompleted); Assert.Null(result.Progress.OmittedFromStep);
    }
    [Theory]
    [InlineData(0, "afterPlan", true)]
    [InlineData(2, "onGoal", true)]
    [InlineData(2, "afterPlan", false)]
    public async Task ZeroScenarioActionCeilingNeverDispatches(int steps, string completion, bool passes)
    {
        using var files = new Fixture(steps, completion, maxActions: 0); var clock = new MonotonicClock();
        var playback = new Playback();
        var result = await Run(await files.Load(), new(clock), playback, clock, actions: 1);
        Assert.Equal(passes ? ResultStatus.Passed : ResultStatus.Failed, result.Run.Primary.Status);
        Assert.Equal(0, playback.Sends); Assert.Equal(0, result.Progress.DispatchedSteps);
        if (!passes) Assert.Equal(RunReason.ActionsExhausted, result.Run.Primary.Cause.Reason);
    }
    [Fact]
    public async Task AlreadyFaultedLosingNotificationCannotPassAfterQueueWake()
    {
        var clock = new MonotonicClock(); var run = new RunSession(Limits(), clock, clock);
        run.BeginPreparation(); run.BeginRunning();
        var losing = new IOException("authoritative observation notification failed");
        Func<CancellationToken, ValueTask>[] waits = [ _ => ValueTask.CompletedTask, _ => new(Task.FromException(losing)) ];
        var method = typeof(ReplayDriver).GetMethod("WaitAnyAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await ((ValueTask)method.Invoke(null, [run, CancellationToken.None, waits])!);
        run.Evaluate();
        Assert.Equal(ResultStatus.Failed, run.Primary?.Status);
        Assert.Equal(RunReason.ExecutionError, run.Primary?.Cause.Reason);
        Assert.Contains(run.Exceptions, e => e.Type == typeof(IOException).FullName);
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
    [Theory]
    [InlineData("future")]
    [InlineData("beforeArrival")]
    [InlineData("nullCheckpoint")]
    [InlineData("nullRun")]
    public async Task InvalidCheckpointEnvelopeIsContractFailure(string fault)
    {
        using var files = new Fixture(checkpoints: [0]); var clock = new MonotonicClock();
        var feed = new Feed(clock)
        {
            EnvelopeOverride = unit => fault switch
            {
                "future" => unit with { Run = unit.Run with { CapturedAt = TimeSpan.FromDays(1) } },
                "beforeArrival" => unit with { Run = unit.Run with { CapturedAt = TimeSpan.Zero } },
                "nullCheckpoint" => unit with { Checkpoint = null! },
                _ => unit with { Run = null! }
            }
        };
        var playback = new Playback(); var result = await Run(await files.Load(), feed, playback, clock);
        Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status);
        Assert.Equal(RunReason.ObservationContractViolation, result.Run.Primary.Cause.Reason);
        Assert.Equal(0, playback.Sends);
    }
    private sealed class EarlyFrozenClock : IClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public int Delays;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Delays++; return ValueTask.CompletedTask; }
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LateTrueCheckpointCaptureCannotExtendPrivateDeadline(int boundary)
    {
        using var files = new Fixture(checkpoints: [boundary]);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!;
        plan["checkpoints"]![0]!["timeoutMilliseconds"] = 20;
        File.WriteAllText(files.PlanPath, plan.ToJsonString());
        var clock = new MonotonicClock(); var playback = new Playback();
        var feed = new Feed(clock) { BeforeCheckpointCapture = (_, token) => new(Task.Delay(100, token)) };
        var result = await Run(await files.Load(), feed, playback, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status);
        Assert.Equal(RunReason.WaitExpired, result.Run.Primary.Cause.Reason);
        Assert.Equal(boundary, playback.Sends); Assert.Equal(0, result.Progress.CompletedCheckpoints);
        Assert.False(result.Progress.PlanCompleted);
    }
    [Fact]
    public async Task AcknowledgedSendWithoutGuardConsumesUncertainAttempt()
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); var playback = new Playback { OmitGuard = true };
        RunSession? owner = null;
        var result = await Run(await files.Load(), new(clock), playback, clock, inspect: run => owner = run);
        Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status);
        Assert.Equal(1, owner!.Budget.Snapshot.Actions); Assert.Equal(0, owner.Budget.Snapshot.ReservedActions);
        Assert.Equal(1, result.Progress.DispatchedSteps); Assert.Equal(0, result.Progress.CompletedSteps);
        Assert.Equal(1, playback.Sends); Assert.False(result.Progress.PlanCompleted);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RepeatedReachedCheckpointsEachHaveFreshFiniteWindow(int boundary)
    {
        using var files = new Fixture(checkpoints: [boundary, boundary]);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!;
        foreach (var point in plan["checkpoints"]!.AsArray()) point!["timeoutMilliseconds"] = 1500;
        File.WriteAllText(files.PlanPath, plan.ToJsonString());
        var delayed = new HashSet<PreparedCondition>();
        var clock = new CheckpointBoundaryClock(); var feed = new Feed(clock)
        { BeforeCheckpointCapture = (point, _) => { if (delayed.Add(point)) clock.MoveTo(clock.Elapsed + TimeSpan.FromMilliseconds(1100)); return ValueTask.CompletedTask; } };
        var result = await Run(await files.Load(), feed, new Playback(), clock, actions: 2);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status);
        Assert.Equal(2, result.Progress.CompletedCheckpoints); Assert.True(result.Progress.PlanCompleted);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnGoalMustSettleAlreadyReachedCheckpoint(bool satisfies)
    {
        using var files = new Fixture(completion: "onGoal", checkpoints: [0]);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!;
        plan["checkpoints"]![0]!["timeoutMilliseconds"] = 1000; File.WriteAllText(files.PlanPath, plan.ToJsonString());
        var clock = new CheckpointBoundaryClock(); var feed = new Feed(clock) { Goal = false, Checkpoint = false };
        feed.OnCapture = () => { if (feed.CheckpointCaptures > 0) feed.Goal = true; };
        feed.OnWait = () => { clock.MoveTo(satisfies ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(1000)); feed.Checkpoint = satisfies; };
        var playback = new Playback(); var result = await Run(await files.Load(), feed, playback, clock);
        Assert.Equal(satisfies ? ResultStatus.Passed : ResultStatus.Failed, result.Run.Primary.Status);
        if (!satisfies) Assert.Equal(RunReason.WaitExpired, result.Run.Primary.Cause.Reason);
        else Assert.Equal(0, result.Progress.OmittedFromStep);
        Assert.Equal(satisfies ? 1 : 0, result.Progress.CompletedCheckpoints); Assert.Equal(0, playback.Sends);
    }
    private sealed class CheckpointBoundaryClock : IClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void MoveTo(TimeSpan boundary) => Elapsed = boundary;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
            => new(Task.Delay(Timeout.InfiniteTimeSpan, token));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GoalAtReachedBoundaryCannotSkipFalseCheckpoint(int boundary)
    {
        using var files = new Fixture(1, "onGoal", [boundary]); var clock = new CheckpointBoundaryClock();
        var feed = new Feed(clock) { Goal = boundary == 0, Checkpoint = false };
        feed.OnWait = () => { if (feed.CheckpointCaptures > 0) clock.MoveTo(TimeSpan.FromMilliseconds(1000)); };
        var playback = new Playback { OnSend = () => feed.Goal = true };
        var result = await Run(await files.Load(), feed, playback, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status);
        Assert.Equal(RunReason.WaitExpired, result.Run.Primary.Cause.Reason);
        Assert.Equal(boundary, playback.Sends); Assert.Equal(0, result.Progress.CompletedCheckpoints);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCallRetainsSuppliedCancellationToken(bool send)
    {
        var clock = new MonotonicClock(); var run = new RunSession(Limits(), clock, clock);
        run.BeginPreparation(); run.BeginRunning(); using var cancellation = new CancellationTokenSource();
        using var calls = new ReplayCalls(run, run.ApproveOperation(1, TimeSpan.FromSeconds(2))!, () => ReplayCheck.Approved, () => { });
        var called = false;
        var waiting = send ? calls.SendAsync(0, _ => { called = true; return new ReplaySend<bool>(true, true); }, cancellation.Token).AsTask()
            : calls.ReadAsync(() => { called = true; return true; }, cancellation.Token).AsTask();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(cancellation.Token, exception.CancellationToken); Assert.False(called);
        Assert.IsType<OperationCanceledException>(FiniteOperation.NormalizeCancellation(exception, cancellation.Token, cancellation.Token));
    }
    [Fact]
    public async Task FullQueuedWriterRetainsSuppliedCancellationToken()
    {
        var clock = new MonotonicClock(); var run = new RunSession(Limits(), clock, clock);
        run.BeginPreparation(); run.BeginRunning(); using var cancellation = new CancellationTokenSource();
        using var calls = new ReplayCalls(run, run.ApproveOperation(1, TimeSpan.FromSeconds(2))!, () => ReplayCheck.Approved, () => { });
        var first = calls.ReadAsync(() => true, default).AsTask(); var called = false;
        var second = calls.ReadAsync(() => { called = true; return true; }, cancellation.Token).AsTask();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(cancellation.Token, exception.CancellationToken); Assert.False(called);
        calls.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
    [Theory]
    [InlineData("recorded", false)]
    [InlineData("recorded", true)]
    [InlineData("conditionSynchronized", true)]
    public async Task RecordedAutomaticChunkCannotRebaseOriginalTiming(string timing, bool gameInput)
    {
        using var files = new Fixture(1001, gameInput: gameInput, maxActions: 2000);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!; plan["timing"] = timing;
        File.WriteAllText(files.PlanPath, plan.ToJsonString());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => files.Load().AsTask());
        Assert.Equal("ReplayAutomaticTimingUnsupported", error.Message);
    }
    [Theory]
    [InlineData(1000, "recorded", false)]
    [InlineData(1001, "conditionSynchronized", true)]
    public async Task SegmentsWithinSupportedPlanBoundariesRemainSupported(int steps, string timing, bool checkpoint)
    {
        using var files = new Fixture(steps, checkpoints: checkpoint ? [1000] : [], maxActions: 2000);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!; plan["timing"] = timing;
        File.WriteAllText(files.PlanPath, plan.ToJsonString());
        Assert.Equal(steps, (await files.Load()).StepCount);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GoalCannotSkipSecondCheckpointAtSameBoundary(int boundary)
    {
        using var files = new Fixture(1, "onGoal", [boundary, boundary]); var clock = new CheckpointBoundaryClock();
        var feed = new Feed(clock) { Goal = boundary == 0 };
        var first = default(PreparedCondition);
        feed.BeforeCheckpointCapture = (point, _) => { first ??= point; feed.Checkpoint = ReferenceEquals(first, point); return ValueTask.CompletedTask; };
        feed.OnWait = () => { if (feed.CheckpointCaptures > 0 && !feed.Checkpoint) clock.MoveTo(TimeSpan.FromMilliseconds(1000)); };
        var result = await Run(await files.Load(), feed, new Playback { OnSend = () => feed.Goal = true }, clock);
        Assert.Equal(ResultStatus.Failed, result.Run.Primary.Status); Assert.Equal(RunReason.WaitExpired, result.Run.Primary.Cause.Reason);
        Assert.Equal(1, result.Progress.CompletedCheckpoints);
    }
    [Theory]
    [InlineData("failure", RunReason.FailureCondition)]
    [InlineData("cancel", RunReason.Cancelled)]
    [InlineData("deadline", RunReason.MaxDuration)]
    [InlineData("deadlineError", RunReason.ExecutionError)]
    [InlineData("lifecycle", RunReason.ExecutionError)]
    [InlineData("clock", RunReason.InvalidContract)]
    public async Task InitialRegistrationFaultRetainsCertifiedEvidenceAndPriority(string mode, RunReason expected)
    {
        var clock = new CheckpointBoundaryClock(); using var cancellation = new CancellationTokenSource();
        var condition = PreparedCondition.Create(JsonNode.Parse(Assertion)!.AsObject(), new(10, 1000));
        var run = new RunSession(Limits(), clock, clock, condition, condition); var executes = 0; var hooks = 0; var exited = false;
        var original = new IOException("registration failed");
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, clock.Elapsed, new(clock.Elapsed, Unit(true), Unit(mode is "failure" or "clock")), "synchronized-hook-test", true);
            boundary.InitialLifecycleFailure = () => exited ? new(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)) : null;
            return ValueTask.FromResult(boundary);
        }, (_, _) => { executes++; return ValueTask.FromResult(true); }, cancellation.Token, confirmPrimary: null, registerInitialObservation: _ =>
        {
            hooks++;
            if (mode == "cancel") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            if (mode is "deadline" or "deadlineError") clock.MoveTo(TimeSpan.FromSeconds(10));
            if (mode == "deadline") throw FiniteOperation.DeadlineReached("hook deadline");
            if (mode == "clock") throw new ClockProviderException(original);
            exited = mode == "lifecycle"; throw original;
        });
        Assert.Equal(expected, result.Primary.Cause.Reason); Assert.Equal(1, hooks); Assert.Equal(0, executes);
        if (mode is "failure" or "clock") Assert.Contains(result.Events, e => e.Reason == RunReason.FailureCondition);
        if (mode is "deadline" or "deadlineError") Assert.Contains(result.Events, e => e.Reason == RunReason.MaxDuration);
        if (mode == "lifecycle") Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin);
        if (mode is not ("cancel" or "deadline")) Assert.Contains(result.Exceptions, e => e.Type == typeof(IOException).FullName);
    }
    [Fact]
    public async Task OnGoalStopsReservedSuffixWhileFirstReceiptSettles()
    {
        using var files = new Fixture(completion: "onGoal"); var clock = new MonotonicClock(); var feed = new Feed(clock) { Goal = false };
        var captured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.OnCapture = () => { if (feed.Goal) captured.TrySetResult(true); };
        var playback = new Playback { OnSend = () => feed.Goal = true,
            BetweenSends = async token => { await captured.Task.WaitAsync(token); await Task.Delay(30, token); } };
        RunSession? owner = null;
        var result = await Run(await files.Load(), feed, playback, clock, inspect: run => owner = run);
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.Equal(1, playback.Sends);
        Assert.Equal(1, result.Progress.CompletedSteps); Assert.Equal(1, result.Progress.OmittedFromStep);
        Assert.Equal(1, owner!.Budget.Snapshot.Actions); Assert.Equal(0, owner.Budget.Snapshot.ReservedActions);
    }
    [Fact]
    public async Task PrivateTemporalCheckpointRejectsEarlyFrozenClockWake()
    {
        using var files = new Fixture(checkpoints: [0]);
        var plan = JsonNode.Parse(File.ReadAllText(files.PlanPath))!;
        plan["checkpoints"]![0]!["condition"] = new JsonObject
        { ["kind"] = "time", ["forMilliseconds"] = 20, ["condition"] = JsonNode.Parse(Assertion) };
        File.WriteAllText(files.PlanPath, plan.ToJsonString());
        var real = new MonotonicClock(); var condition = new EarlyFrozenClock();
        var childUnit = new ConditionObservationUnit([KeyValuePair.Create("$/condition", Unit(true).Leaves["$"])]);
        var feed = new Feed(condition) { PointOverride = childUnit }; var playback = new Playback();
        var result = await Run(await files.Load(), feed, playback, real, conditionClock: condition);
        Assert.Equal(ResultStatus.Invalid, result.Run.Primary.Status);
        Assert.Equal(RunOrigin.Clock, result.Run.Primary.Cause.Origin);
        Assert.Equal(RunReason.InvalidContract, result.Run.Primary.Cause.Reason);
        Assert.InRange(condition.Delays, 1, 2); Assert.InRange(feed.CheckpointCaptures, 1, 3);
        Assert.Equal(0, playback.Sends);
    }
    [Fact]
    public async Task SimulationGuardUsesSerializedOwnerWithoutNestedQueueDeadlock()
    {
        using var files = new Fixture(gameInput: true); var replay = await files.Load();
        var clock = new MonotonicClock(); var host = new SimulationHost();
        var playback = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved,
            new(1000, 100, 100, GuaSegmentClock.Simulation, false, false));
        // The unchanged outer ceiling and Passed assertion detect a nested owner-queue
        // deadlock. An unrelated 500ms caller timer must not cancel cold-start work.
        var result = await Task.Run(async () => await Run(replay, new(clock), playback, clock)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ResultStatus.Passed, result.Run.Primary.Status); Assert.DoesNotContain(result.Run.Events, e => e.Reason == RunReason.Cancelled);
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
        Assert.True(host.Sends > 0, $"Missing-receipt prerequisite was not reached: sends={host.Sends}, primary={result.Run.Primary}, events={JsonSerializer.Serialize(result.Run.Events)}");
        Assert.Equal(RunReason.ActionUnconfirmed, result.Run.Primary.Cause.Reason);
        if (cancel) Assert.Contains(result.Run.Events, e => e.Reason == RunReason.Cancelled);
        await host.Ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(host.Releases > 0); Assert.Equal(1, host.Ends);
    }
    [Fact]
    public async Task UnsentTimedStartupExpiryDoesNotManufactureActionUnconfirmed()
    {
        using var files = new Fixture(gameInput: true); var clock = new CheckpointBoundaryClock();
        var host = new SimulationHost { OnPlaybackCapability = () => clock.MoveTo(TimeSpan.FromSeconds(1)) };
        var playback = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved,
            new(1000, 100, 100, GuaSegmentClock.Simulation, false, false));
        var result = await Run(await files.Load(), new(clock), playback, clock);
        Assert.Equal(RunReason.WaitExpired, result.Run.Primary.Cause.Reason); Assert.Equal(0, host.Sends);
        Assert.Equal(0, result.Progress.DispatchedSteps);
        Assert.DoesNotContain(result.Run.Events, e => e.Reason == RunReason.ActionUnconfirmed);
        Assert.Equal(0, host.Begins); Assert.Equal(0, host.Sends);
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
        Assert.Equal(RunOrigin.Host, result.Run.Primary.Cause.Origin); Assert.Equal(1, result.Run.ExitCode);
        Assert.Contains(result.Run.Exceptions, e => e.Type == typeof(ArgumentException).FullName && e.StackTrace is not null);
    }
    [Theory]
    [InlineData("caller", RunReason.Cancelled, RunOrigin.User, 4)]
    [InlineData("alien", RunReason.ExecutionError, RunOrigin.Host, 1)]
    [InlineData("typed", RunReason.ActionFailed, RunOrigin.Condition, 1)]
    [InlineData("clock", RunReason.InvalidContract, RunOrigin.Clock, 2)]
    public async Task PreflightRetainsClassifiedCauseAndCallerIdentity(string mode, RunReason reason, RunOrigin origin, int exit)
    {
        using var files = new Fixture(); var clock = new MonotonicClock(); using var cancellation = new CancellationTokenSource();
        var original = new IOException("preflight boundary");
        var playback = new Playback { PreflightOverride = () =>
        {
            if (mode == "caller") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            if (mode == "alien") throw new OperationCanceledException(new CancellationToken(true));
            if (mode == "clock") throw new ClockProviderException(original);
            throw new RunFailureException(new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Condition), original);
        } };
        var result = await Run(await files.Load(), new(clock), playback, clock, token: cancellation.Token);
        Assert.Equal(reason, result.Run.Primary.Cause.Reason); Assert.Equal(origin, result.Run.Primary.Cause.Origin);
        Assert.Equal(exit, result.Run.ExitCode); Assert.Equal(0, playback.Plays); Assert.Equal(0, playback.Sends);
        if (mode is "typed" or "clock") Assert.Contains(result.Run.Exceptions, e => e.Type == typeof(IOException).FullName);
    }
    private sealed class SimulationHost : IGuaTimedSegmentHost
    {
        public Exception? CapabilityFault { get; set; }
        public Action? OnPlaybackCapability { get; set; }
        private int capabilityReads;
        public Action? OnSend { get; set; }
        public bool MissingReceipts { get; set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OrderedApplication
        {
            get
            {
                if (CapabilityFault is { } failure) throw failure;
                if (++capabilityReads == 2) OnPlaybackCapability?.Invoke();
                return true;
            }
        }
        public bool ApplicationTimes => false;
        public bool SameTickApplication => false;
        public string? SimulationScope => "controlled-fixture";
        public double SimulationMilliseconds => 0;
        public string? ExecutionFailureCode => null;
        public bool IsNeutral => true;
        public int Begins, Sends, Ends, Releases;
        public void Begin(GuaTimedSegment segment) { Begins++; }
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
