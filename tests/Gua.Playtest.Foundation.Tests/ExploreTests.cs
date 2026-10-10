using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Exploration;
using Gua.Playtest.Runner.Planning;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class ExploreTests
{
    private sealed class Clock : IClock
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (duration <= TimeSpan.Zero) return ValueTask.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            timers.Add((Elapsed + duration, completion)); return new(completion.Task);
        }
        public void At(int milliseconds)
        {
            Elapsed = TimeSpan.FromMilliseconds(milliseconds);
            foreach (var timer in timers.Where(x => x.Due <= Elapsed).ToArray()) timer.Completion.TrySetResult();
        }
    }
    private static ResourceLimits Limits(long actions = 30, long decisions = 30, long recovery = 2, long stagnation = 8, long repeats = 3)
        => new(5000, actions, decisions, 1000, 1000, 1000, 2000, 1000, 0, 1000, 100000, 100, 1024,
            stagnation, repeats, recovery, 100000, 1024, 1024, 100000);
    private static JsonObject Read(string type = "number") => JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"public"}}},"region":"standard","field":"visible","valueType":{"type":"number"}}""")!.AsObject()
        .Also(x => { x["valueType"]!["type"] = type; if (type != "bool") { x["region"] = "observe"; x.Remove("field"); x["name"] = "hp"; } });
    private static JsonObject Action(string id = "attack") => new()
    { ["kind"] = "execute", ["mode"] = "single", ["action"] = new JsonObject { ["kind"] = "semantic", ["actionId"] = id, ["operation"] = "press" } };
    private static JsonObject Situation(int position = 0) => new() { ["position"] = position, ["context"] = "arena" };
    private static PreparedCondition Condition() => PreparedCondition.Create(new JsonObject
    {
        ["kind"] = "assertion", ["read"] = Read("bool"), ["quantifier"] = "one", ["operator"] = "equals",
        ["expected"] = new JsonObject { ["type"] = "bool", ["value"] = true }
    }, new(100, 1024));
    private static ConditionObservationUnit Unit(bool value) => new([KeyValuePair.Create("$",
        new ConditionLeafObservation("scope", true, [new("target", "{\"type\":\"bool\",\"value\":" + (value ? "true" : "false") + "}")]))]);
    private static ProgressObservation Progress(Clock clock, double? hp = null, bool? milestone = null, JsonObject? situation = null, string identity = "target")
        => new(clock.Elapsed,
            milestone.HasValue ? new Dictionary<string, ConditionObservationUnit> { ["milestone"] = Unit(milestone.Value) } : new Dictionary<string, ConditionObservationUnit>(),
            hp.HasValue ? new Dictionary<string, ConditionLeafObservation> { ["hp"] = new("scope", true,
                [new(identity, "{\"type\":\"number\",\"value\":" + hp.Value.ToString("R", CultureInfo.InvariantCulture) + "}")]) } : new Dictionary<string, ConditionLeafObservation>(), situation);
    private static ProgressDefinitions Metric(double improvement = 10) => new([], [new("hp", Read(), MetricDirection.Minimize, improvement, new(100, 1024))]);

    [Fact]
    public void BestValueDoesNotResurrectAcrossRecoveryOrRuntimeReplacementAndSmallChangesAccumulate()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(), clock, Progress(clock, 100));
        Assert.True(tracker.ObserveBoundary(Progress(clock, 90), Action()).Improved);
        Assert.False(tracker.ObserveBoundary(Progress(clock, 100), Action(), true).Improved);
        tracker.BeginRecovery(); Assert.Equal(ExplorePhase.Recovering, tracker.Phase);
        Assert.False(tracker.ObserveBoundary(Progress(clock, 90, identity: "replacement"), Action()).Improved);
        Assert.False(tracker.ObserveBoundary(Progress(clock, 87), Action()).Improved);
        Assert.False(tracker.ObserveBoundary(Progress(clock, 84), Action()).Improved);
        Assert.True(tracker.ObserveBoundary(Progress(clock, 80), Action()).Improved);
        Assert.Equal(ExplorePhase.Exploring, tracker.Phase);
    }
    [Fact]
    public void MilestoneCountsOnlyFirstProvenRuntimeReachAndNeverInitialTrue()
    {
        var clock = new Clock(); var definitions = new ProgressDefinitions([new("milestone", Condition())], []);
        var initialTrue = new ProgressTracker(definitions, Limits(), clock, Progress(clock, milestone: true));
        Assert.False(initialTrue.Initial.Improved);
        initialTrue.ObserveBoundary(Progress(clock, milestone: false), Action());
        Assert.False(initialTrue.ObserveBoundary(Progress(clock, milestone: true), Action()).Improved);
        var initialFalse = new ProgressTracker(definitions, Limits(), clock, Progress(clock, milestone: false));
        Assert.True(initialFalse.ObserveBoundary(Progress(clock, milestone: true), Action()).Improved);
        initialFalse.ObserveBoundary(Progress(clock, milestone: false), Action(), true); initialFalse.BeginRecovery();
        Assert.False(initialFalse.ObserveBoundary(Progress(clock, milestone: true), Action()).Improved);
    }
    [Fact]
    public void UndefinedAndMissingProgressAreUnknownNotZeroAndRevisionAnimationIsNotProgress()
    {
        var clock = new Clock(); var undefined = new ProgressTracker(new([], []), Limits(stagnation: 2), clock, Progress(clock));
        for (int i = 0; i < 10; i++)
        {
            var result = undefined.ObserveBoundary(Progress(clock), Action());
            Assert.False(result.Defined); Assert.False(result.Known); Assert.False(result.Improved); Assert.Equal(StallSignal.None, result.Signal);
        }
        var missing = new ProgressTracker(Metric(), Limits(stagnation: 2), clock, Progress(clock, 100));
        for (int i = 0; i < 10; i++) Assert.Equal(StallSignal.None, missing.ObserveBoundary(Progress(clock), Action()).Signal);
    }
    [Fact]
    public void ComparableOscillationIncludesSituationAndWholeOperationSequence()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(new([], []), Limits(), clock, Progress(clock, situation: Situation()));
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var outward = tracker.ObserveBoundary(Progress(clock, situation: Situation(1)), Action("right"));
            Assert.Equal(StallSignal.None, outward.Signal);
            var returning = tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action("left"));
            Assert.Equal(cycle == 2 ? StallSignal.ComparableRepetition : StallSignal.None, returning.Signal);
        }
    }
    [Fact]
    public void RepeatThresholdDoesNotDependOnUndefinedProgressThreshold()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(new([], []), Limits(stagnation: 1, repeats: 2), clock, Progress(clock, situation: Situation()));
        Assert.Equal(StallSignal.None, tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action()).Signal);
        Assert.Equal(StallSignal.ComparableRepetition, tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action()).Signal);
    }
    [Fact]
    public void ThreeOperationRouteRepeatsAfterNineBoundariesWithIndependentThresholds()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(new([], []), Limits(stagnation: 8, repeats: 3), clock, Progress(clock, situation: Situation()));
        for (int i = 0; i < 9; i++)
        {
            var step = i % 3;
            var boundary = tracker.ObserveBoundary(Progress(clock, situation: Situation((step + 1) % 3)), Action("step-" + step));
            Assert.Equal(i == 8 ? StallSignal.ComparableRepetition : StallSignal.None, boundary.Signal);
        }
    }
    [Fact]
    public void ImprovingAttackIsNotRepetitionAndMissingSituationBreaksComparison()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(1), Limits(), clock, Progress(clock, 100, situation: Situation()));
        for (int i = 1; i < 10; i++) Assert.Equal(StallSignal.None,
            tracker.ObserveBoundary(Progress(clock, 100 - i, situation: Situation()), Action()).Signal);
        var absent = new ProgressTracker(new([], []), Limits(), clock, Progress(clock, situation: Situation()));
        absent.ObserveBoundary(Progress(clock, situation: Situation()), Action());
        absent.ObserveBoundary(Progress(clock), Action());
        absent.ObserveBoundary(Progress(clock, situation: Situation()), Action());
        Assert.Equal(StallSignal.None, absent.ObserveBoundary(Progress(clock, situation: Situation()), Action()).Signal);
    }
    [Fact]
    public void UnknownMetricBaselineIsNotAnImprovementAndMalformedIncompleteValueIsNotHidden()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(), clock, Progress(clock));
        Assert.False(tracker.ObserveBoundary(Progress(clock, 90), Action()).Improved);
        var invalid = new ProgressObservation(clock.Elapsed, new Dictionary<string, ConditionObservationUnit>(),
            new Dictionary<string, ConditionLeafObservation> { ["hp"] = new("scope", false, [new("target", "{\"type\":\"number\",\"value\":\"private-invalid\"}")]) });
        Assert.True(tracker.ObserveBoundary(invalid, Action()).ObservationViolation);
    }
    [Fact]
    public void DefinedStagnationRequiresConfirmedActionDecisionsAndWaitSamplesDoNotShortenTheWindow()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(stagnation: 2), clock, Progress(clock, 100));
        for (int i = 0; i < 10; i++) Assert.Equal(StallSignal.None, tracker.ObserveSample(Progress(clock, 100)).Signal);
        Assert.Equal(StallSignal.None, tracker.ObserveBoundary(Progress(clock, 100), Action()).Signal);
        Assert.Equal(StallSignal.ProgressNotUpdated, tracker.ObserveBoundary(Progress(clock, 100), Action()).Signal);
    }

    private sealed class Authority : IPlannerAuthority
    {
        public bool InputsNeutral => true;
        public bool RequiresResynchronization => false;
        public bool CheckPermissions(JsonObject proposal) => true;
        public bool CheckDefinitions(JsonObject proposal) => true;
        public bool CheckContext(JsonObject proposal, ProjectedPlannerState basis) => true;
        public bool CheckTargets(JsonObject proposal, ProjectedPlannerState basis) => true;
        public bool CheckClock(JsonObject proposal) => true;
        public bool CheckInputBoundary(JsonObject proposal) => true;
    }
    private sealed class Feed(Clock clock) : IExploreObservationFeed
    {
        public int Captures;
        public bool Goal;
        public bool Failure;
        public double? PrivateMetric;
        public ConditionObservationUnit? PrivateMilestone;
        public Func<CancellationToken, ValueTask>? WaitOverride;
        public Func<ExploreObservation, ExploreObservation>? TransformCapture;
        public ExploreObservation Frame()
        {
            var observation = new JsonObject
            {
                ["observationId"] = "observation-" + Captures, ["observedAt"] = "2026-10-10T11:00:00Z", ["profile"] = "Player",
                ["sources"] = new JsonArray(new JsonObject { ["source"] = "ui", ["sourceId"] = "source", ["sessionEpoch"] = 1, ["revision"] = Captures, ["frameSequence"] = Captures }),
                ["complete"] = true, ["reads"] = new JsonArray()
            };
            var state = new ProjectedPlannerState("observation-" + Captures, observation,
                JsonNode.Parse("""{"schemaVersion":1,"sessionEpoch":1,"revision":1,"context":"fixture","actions":[]}""")!.AsObject(), [], []);
            var progress = Progress(clock, PrivateMetric, situation: Situation());
            if (PrivateMilestone is not null) progress = progress with { Milestones = new Dictionary<string, ConditionObservationUnit> { ["milestone"] = PrivateMilestone } };
            return new(new(clock.Elapsed, Unit(Goal), Unit(Failure)), state, progress);
        }
        public ValueTask<ExploreObservation> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Captures++; var frame = Frame(); return ValueTask.FromResult(TransformCapture?.Invoke(frame) ?? frame); }
        public async ValueTask WaitForChangeAsync(CancellationToken token) { if (WaitOverride is { } wait) await wait(token); else await Task.Delay(Timeout.Infinite, token); }
    }
    private sealed class Planner(Func<int, JsonObject> proposal) : IPlanner<PlannerInputDocument, PlannerReply>
    {
        public readonly List<PlannerInputDocument> Inputs = [];
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
        {
            Inputs.Add(input);
            return ValueTask.FromResult(new PlannerReply(PlannerReplyStatus.Completed,
                Encoding.UTF8.GetBytes(ContractJson.Serialize(new PlannerDecisionDocument(input.RunId, input.DecisionRequestId, input.BasedOnObservationId, proposal(Inputs.Count))))));
        }
    }
    private sealed class Work(Func<JsonObject, IExploreCalls, CancellationToken, ValueTask<ExploreReceipt>> body) : IExploreWork
    {
        public int Calls;
        public ValueTask<ExploreReceipt> ExecuteAsync(JsonObject decision, IExploreCalls calls, CancellationToken token)
        { Calls++; return body(decision, calls, token); }
    }
    private static JsonObject Finish(string reason = "goalClaimed") => new() { ["kind"] = "finish", ["report"] = reason };
    private sealed class Setup
    {
        public readonly Clock Clock = new();
        public readonly RunSession Run;
        public readonly PlannerGate Gate;
        public readonly Feed Feed;
        public readonly ResourceLimits Limits;
        public readonly ProgressDefinitions Definitions;
        public readonly OwnedCleanup Cleanup = new();
        public Setup(bool success = false, bool failure = false, ResourceLimits? limits = null, ProgressDefinitions? definitions = null)
        {
            Limits = limits ?? ExploreTests.Limits();
            Definitions = definitions ?? new([], []);
            Run = new(new RunLimits(TimeSpan.FromMilliseconds(Limits.MaxDurationMilliseconds), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), Limits.MaxActions, Limits.MaxDecisions, Limits.RecoveryDecisionLimit, 1024),
                Clock, Clock, success ? Condition() : null, failure ? Condition() : null);
            Run.BeginPreparation(); Run.BeginRunning();
            Gate = new(Run, Clock, new Authority(), "run-1", "public objective", Limits); Feed = new(Clock);
        }
        public ValueTask<bool> Execute(Planner planner, Work work, CancellationToken token = default, Action<ProgressSummary>? record = null,
            Func<CancellationToken, ValueTask<bool>>? release = null, ExploreObservation? initial = null)
            => ExploreDriver.ExecuteRunningAsync(Run, Gate, planner, Feed, work, Definitions, Limits, initial ?? Feed.Frame(),
                release ?? (_ => ValueTask.FromResult(true)), Cleanup, token, record);
    }
    [Theory]
    [InlineData(false, ResultStatus.Unverified, RunReason.ExplorationFinished)]
    [InlineData(true, ResultStatus.Failed, RunReason.SuccessUnconfirmed)]
    public async Task PlannerFinishNeverGradesMachineGoal(bool success, ResultStatus status, RunReason reason)
    {
        var s = new Setup(success); var planner = new Planner(_ => Finish());
        var work = new Work((_, _, _) => throw new InvalidOperationException());
        Assert.True(await s.Execute(planner, work)); s.Run.Evaluate(executionComplete: true);
        Assert.Equal(status, s.Run.Primary!.Status); Assert.Equal(reason, s.Run.Primary.Cause.Reason);
        Assert.Equal(0, work.Calls); Assert.True(s.Feed.Captures >= 2);
    }
    [Fact]
    public async Task PlannerReportedStallConsumesBothBudgetsAndPrivateProgressDoesNotEnterFeedback()
    {
        var s = new Setup(); var planner = new Planner(_ => Finish("stuck"));
        var work = new Work((_, _, _) => throw new InvalidOperationException());
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(RunReason.RecoveryExhausted, s.Run.Primary!.Cause.Reason);
        Assert.Equal(RunOrigin.Budget, s.Run.Primary.Cause.Origin);
        Assert.Equal(3, s.Run.Budget.Snapshot.Decisions); Assert.Equal(2, s.Run.Budget.Snapshot.RecoveryDecisions);
        Assert.Equal(3, planner.Inputs.Count);
        Assert.All(planner.Inputs, input => Assert.DoesNotContain("milestone", ContractJson.Serialize(input), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, work.Calls);
    }
    [Fact]
    public async Task SentActionUsesOwnerGateAndNormalConfirmationBeforeNextPlannerDecision()
    {
        var s = new Setup(); int sends = 0;
        var planner = new Planner(i => i == 1 ? Action() : Finish());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); sends++; return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.True(await s.Execute(planner, work));
        Assert.Equal(1, sends); Assert.Equal(1, s.Run.Budget.Snapshot.Actions);
        Assert.Contains(planner.Inputs[1].Feedback, x => x["code"]!.GetValue<string>() == "Confirmed");
    }
    [Fact]
    public async Task UnconfirmedSentActionStopsWithoutResendAndPreservesHostOrigin()
    {
        var s = new Setup(); var planner = new Planner(_ => Action()); int sends = 0;
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); sends++; return new ExploreSend<bool>(true, false); }, token);
            return new(ExploreWorkStatus.Unconfirmed, false);
        });
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(1, sends); Assert.Single(planner.Inputs);
        Assert.Equal(RunReason.ActionUnconfirmed, s.Run.Primary!.Cause.Reason); Assert.Equal(RunOrigin.Host, s.Run.Primary.Cause.Origin);
    }
    [Fact]
    public async Task WholeWaitFinishesBeforePlannerReportedRecoveryCanStart()
    {
        var s = new Setup(); var planner = new Planner(i => i == 1
            ? new JsonObject { ["kind"] = "wait", ["durationMilliseconds"] = 500 } : Finish("stuck"));
        var work = new Work((_, _, _) => throw new InvalidOperationException());
        var task = s.Execute(planner, work).AsTask();
        Assert.Single(planner.Inputs); Assert.False(task.IsCompleted);
        s.Clock.At(499); Assert.Single(planner.Inputs); Assert.False(task.IsCompleted);
        s.Clock.At(500); Assert.False(await task);
        Assert.Equal(RunReason.RecoveryExhausted, s.Run.Primary!.Cause.Reason);
        Assert.Equal(4, planner.Inputs.Count); Assert.Equal(2, s.Run.Budget.Snapshot.RecoveryDecisions);
    }
    [Fact]
    public async Task SuccessfulLastAllowedActionKeepsMachineGoalAndFailureOverridesSuccess()
    {
        foreach (bool failure in new[] { false, true })
        {
            var s = new Setup(success: true, failure: true, limits: Limits(actions: 1));
            var planner = new Planner(_ => Action());
            var work = new Work(async (_, calls, token) =>
            {
                await calls.SendAsync(0, before => { before(); s.Feed.Goal = true; s.Feed.Failure = failure; return new ExploreSend<bool>(true, true); }, token);
                return new(ExploreWorkStatus.Completed, true);
            });
            Assert.False(await s.Execute(planner, work));
            Assert.Equal(failure ? RunReason.FailureCondition : RunReason.GoalSatisfied, s.Run.Primary!.Cause.Reason);
            Assert.Equal(1, s.Run.Budget.Snapshot.Actions); Assert.Single(planner.Inputs);
        }
    }
    [Fact]
    public async Task InvalidPlannerProposalsStayFiniteAndDoNotStartHostWork()
    {
        var s = new Setup(limits: Limits(decisions: 2)); var planner = new Planner(_ => new JsonObject { ["kind"] = "restart" });
        var work = new Work((_, _, _) => throw new InvalidOperationException());
        Assert.False(await s.Execute(planner, work)); Assert.Equal(2, planner.Inputs.Count); Assert.Equal(0, work.Calls);
        Assert.Equal(RunReason.PlannerOutputInvalid, s.Run.Primary!.Cause.Reason);
    }
    [Fact]
    public async Task PrivateMetricValueAndReadNeverReachPlannerEvenDuringRecovery()
    {
        var s = new Setup(limits: Limits(stagnation: 2), definitions: Metric()); s.Feed.PrivateMetric = 739;
        var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(RunReason.RecoveryExhausted, s.Run.Primary!.Cause.Reason);
        Assert.Equal(2, s.Run.Budget.Snapshot.RecoveryDecisions);
        Assert.All(planner.Inputs, input =>
        {
            var json = ContractJson.Serialize(input);
            Assert.DoesNotContain("739", json); Assert.DoesNotContain("\"hp\"", json); Assert.DoesNotContain("minImprovement", json);
        });
    }
    [Fact]
    public async Task CancellationBeforeQueuedSendNeverInvokesTransport()
    {
        var s = new Setup(); using var cancellation = new CancellationTokenSource(); int sends = 0;
        var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            var pending = calls.SendAsync(0, before => { before(); sends++; return new ExploreSend<bool>(true, true); }, token);
            cancellation.Cancel(); await pending; return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work, cancellation.Token));
        Assert.Equal(0, sends); Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
        Assert.Equal(RunReason.Cancelled, s.Run.Primary!.Cause.Reason);
    }
    [Fact]
    public async Task HostCallbackFaultRetainsOriginalEvidenceAndDoesNotBecomePlannerRetry()
    {
        var s = new Setup(); var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync<bool>(0, before => { before(); throw new IOException("private-host-failure"); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work)); Assert.Single(planner.Inputs);
        Assert.Contains(s.Run.Exceptions, x => x.Type == typeof(IOException).FullName);
        Assert.Equal(RunOrigin.Host, s.Run.Primary!.Cause.Origin);
        Assert.All(planner.Inputs, x => Assert.DoesNotContain("private-host-failure", ContractJson.Serialize(x)));
    }
    [Fact]
    public void MissingIntermediateSampleCannotJoinTwoComparableActionSequences()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(new([], []), Limits(), clock, Progress(clock, situation: Situation()));
        tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action());
        tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action());
        tracker.ObserveSample(Progress(clock));
        Assert.Equal(StallSignal.None, tracker.ObserveBoundary(Progress(clock, situation: Situation()), Action()).Signal);
    }
    [Fact]
    public void MidOperationMilestoneReachSurvivesLaterFalseButIsNotCountedAgain()
    {
        var clock = new Clock(); var definitions = new ProgressDefinitions([new("milestone", Condition())], []);
        var tracker = new ProgressTracker(definitions, Limits(), clock, Progress(clock, milestone: false));
        Assert.True(tracker.ObserveSample(Progress(clock, milestone: true)).Improved);
        Assert.True(tracker.ObserveBoundary(Progress(clock, milestone: false), Action()).Improved);
        Assert.False(tracker.ObserveBoundary(Progress(clock, milestone: true), Action()).Improved);
    }
    [Fact]
    public void IntermediateMetricImprovementBelongsToTheWholeActionBoundary()
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(stagnation: 1), clock, Progress(clock, 100));
        Assert.True(tracker.ObserveSample(Progress(clock, 90)).Improved);
        var boundary = tracker.ObserveBoundary(Progress(clock, 90), Action());
        Assert.True(boundary.Improved); Assert.Equal(ExplorePhase.Exploring, boundary.Phase); Assert.Equal(StallSignal.None, boundary.Signal);
        Assert.Equal(StallSignal.ProgressNotUpdated, tracker.ObserveBoundary(Progress(clock, 90), Action()).Signal);
    }
    [Theory] [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)] [InlineData(false, true)]
    public async Task PrivateObservationViolationAndGoalSharePrimaryArbitration(bool duringPlanner, bool milestone)
    {
        var definitions = milestone ? new ProgressDefinitions([new("milestone", Condition())], []) : Metric();
        var s = new Setup(success: true, definitions: definitions); var planner = new Planner(_ => Action());
        var invalid = new ConditionLeafObservation("scope", true, [new("target", "{\"type\":\"number\",\"value\":\"invalid\"}")]);
        s.Feed.TransformCapture = frame =>
        {
            if (!duringPlanner && !s.Feed.Goal) return frame;
            var progress = milestone
                ? frame.Progress with { Milestones = new Dictionary<string, ConditionObservationUnit> { ["milestone"] = new([KeyValuePair.Create("$", invalid)]) } }
                : frame.Progress with { Metrics = new Dictionary<string, ConditionLeafObservation> { ["hp"] = invalid } };
            return frame with { Run = frame.Run with { Success = Unit(true) }, Progress = progress };
        };
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); s.Feed.Goal = true; return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(RunReason.ObservationContractViolation, s.Run.Primary!.Cause.Reason);
        Assert.NotEqual(ResultStatus.Passed, s.Run.Primary.Status);
        Assert.True(s.Run.GoalVerified); Assert.Single(planner.Inputs);
        Assert.Equal(duringPlanner ? 0 : 1, work.Calls);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void MalformedMetricMetadataIsViolationRatherThanUnknown(bool emptyScope)
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(), clock, Progress(clock));
        var invalid = emptyScope ? new ConditionLeafObservation("", true, []) : new ConditionLeafObservation("scope", false, [], unavailable: EvaluationCode.None);
        var frame = Progress(clock) with { Metrics = new Dictionary<string, ConditionLeafObservation> { ["hp"] = invalid } };
        Assert.True(tracker.ObserveSample(frame).ObservationViolation);
    }
    [Theory] [InlineData(EvaluationCode.Missing)] [InlineData(EvaluationCode.GetterError)] [InlineData(EvaluationCode.Gap)] [InlineData(EvaluationCode.None)]
    public void MetricTargetUnavailableCodesFollowConditionValidation(EvaluationCode code)
    {
        var clock = new Clock(); var tracker = new ProgressTracker(Metric(), Limits(), clock, Progress(clock));
        var frame = Progress(clock) with { Metrics = new Dictionary<string, ConditionLeafObservation>
            { ["hp"] = new("scope", true, [new("target", Unavailable: code)]) } };
        var sample = tracker.ObserveSample(frame);
        Assert.False(sample.Known); Assert.Equal(code == EvaluationCode.None, sample.ObservationViolation);
    }
    [Fact]
    public async Task PrivateMilestoneTimerCapturesAttainmentWithoutInterruptingApprovedWork()
    {
        var held = PreparedCondition.Create(new JsonObject { ["kind"] = "time", ["forMilliseconds"] = 100,
            ["condition"] = new JsonObject { ["kind"] = "assertion", ["read"] = Read("bool"), ["quantifier"] = "one", ["operator"] = "equals",
                ["expected"] = new JsonObject { ["type"] = "bool", ["value"] = true } } }, new(100, 1024));
        var s = new Setup(definitions: new([new("milestone", held)], []));
        static ConditionObservationUnit HeldUnit(bool value) => new([KeyValuePair.Create("$/condition",
            new ConditionLeafObservation("scope", true, [new("target", "{\"type\":\"bool\",\"value\":" + (value ? "true" : "false") + "}")], true))]);
        s.Feed.PrivateMilestone = HeldUnit(false);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var improvement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Feed.TransformCapture = frame => { if (frame.Run.CapturedAt == TimeSpan.FromMilliseconds(10)) first.TrySetResult();
            if (frame.Run.CapturedAt == TimeSpan.FromMilliseconds(110)) attained.TrySetResult(); return frame; };
        var summaries = new List<ProgressSummary>(); var planner = new Planner(i => i == 1 ? Action() : Finish());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); s.Clock.At(10); s.Feed.PrivateMilestone = HeldUnit(true); return new ExploreSend<bool>(true, true); }, token);
            await finish.Task.WaitAsync(token); return new(ExploreWorkStatus.Completed, true);
        });
        var pending = s.Execute(planner, work, record: summary => { summaries.Add(summary); if (summary.Improved) improvement.TrySetResult(); }).AsTask();
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); s.Clock.At(110);
            Assert.Same(attained.Task, await Task.WhenAny(attained.Task, Task.Delay(1000)));
            await improvement.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); Assert.Single(planner.Inputs);
            Assert.Contains(summaries, x => x.Improved);
            s.Clock.At(150); s.Feed.PrivateMilestone = HeldUnit(false); finish.TrySetResult();
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, s.Run.Budget.Snapshot.RecoveryDecisions); Assert.Equal(1, work.Calls);
        }
        finally { finish.TrySetResult(); if (!pending.IsCompleted) { s.Clock.At(5000); await pending.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task QueueWakeContainsCancellationCallbackFaultAndRetainsLosingProviderFault(bool providerFault)
    {
        var s = new Setup(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool registered = false;
        s.Feed.WaitOverride = token =>
        {
            if (registered) return new ValueTask(Task.Delay(Timeout.Infinite, token));
            registered = true;
            token.Register(() => { if (providerFault) source.TrySetException(new InvalidDataException("actual-source")); else source.TrySetCanceled(token);
                throw new IOException("callback-diagnostic"); });
            entered.TrySetResult(); return new(source.Task);
        };
        var planner = new Planner(i => i == 1 ? Action() : Finish());
        var work = new Work(async (_, calls, token) =>
        {
            await entered.Task.WaitAsync(token);
            await calls.SendAsync(0, before => { before(); return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        var completed = await s.Execute(planner, work).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(s.Run.Exceptions, x => x.Type == typeof(AggregateException).FullName);
        if (providerFault)
        {
            Assert.False(completed); Assert.Equal(RunOrigin.Host, s.Run.Primary!.Cause.Origin);
            Assert.Contains(s.Run.Exceptions, x => x.Type == typeof(InvalidDataException).FullName);
        }
        else { Assert.True(completed); Assert.Null(s.Run.Primary); Assert.Equal(1, s.Run.Budget.Snapshot.Actions); }
    }
    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public async Task PrivateCaptureTimestampViolationRetainsContractCauseAndOriginalException(bool duringPlanner, bool future)
    {
        var s = new Setup(success: true, definitions: Metric()); s.Clock.At(10);
        s.Feed.TransformCapture = frame =>
        {
            if (!duringPlanner && !s.Feed.Goal) return frame;
            var rejected = TimeSpan.FromMilliseconds(future ? 11 : 9);
            return frame with { Run = frame.Run with { CapturedAt = rejected, Success = Unit(true) }, Progress = frame.Progress with { CapturedAt = rejected } };
        };
        var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); s.Feed.Goal = true; return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(new RunEvent(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract), s.Run.Primary!.Cause);
        Assert.NotEqual(ResultStatus.Passed, s.Run.Primary.Status);
        Assert.Contains(s.Run.Exceptions, x => x.Type == typeof(ArgumentException).FullName && x.StackTrace is not null);
        Assert.Equal(duringPlanner ? 0 : 1, work.Calls);
    }
    [Fact]
    public async Task HealthyCaptureBurstDoesNotConsumeEvidenceCapacity()
    {
        var s = new Setup(); var started = false;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Feed.WaitOverride = token => started && s.Feed.Captures < 1100
            ? ValueTask.CompletedTask : new(Task.Delay(Timeout.Infinite, token));
        s.Feed.TransformCapture = frame => { if (s.Feed.Captures >= 1100) captured.TrySetResult(); return frame; };
        var planner = new Planner(i => i == 1 ? Action() : Finish());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); started = true; return new ExploreSend<bool>(true, true); }, token);
            await captured.Task.WaitAsync(token); return new(ExploreWorkStatus.Completed, true);
        });
        Assert.True(await s.Execute(planner, work).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(s.Feed.Captures >= 1100); Assert.Null(s.Run.Primary); Assert.Equal(1, s.Run.Budget.Snapshot.Actions);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OnGoalCancelledSuffixAllowsOnlyActualPrefixSettlement(bool attemptsSuffix)
    {
        var s = new Setup(success: true); int sends = 0;
        var decision = JsonNode.Parse("""{"kind":"execute","mode":"timed","segment":{"schemaVersion":1,"durationMilliseconds":100,"maxLatenessMilliseconds":0,"executionTimeoutMilliseconds":1000,"cleanupTimeoutMilliseconds":1000,"inputs":[{"offsetMilliseconds":0,"kind":1,"operation":1,"target":"attack"},{"offsetMilliseconds":1,"kind":1,"operation":3,"target":"attack"}]}}""")!.AsObject();
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); sends++; s.Feed.Goal = true; return new ExploreSend<bool>(true, true); }, token);
            while (!await calls.ReadAsync(() => s.Run.GoalVerified, token)) await Task.Yield();
            Assert.True(await calls.ReadAsync(() => s.Run.ActionsClosing, token));
            if (attemptsSuffix)
            {
                var exception = await Record.ExceptionAsync(() => calls.SendAsync(1, before =>
                    { before(); sends++; return new ExploreSend<bool>(true, true); }, token).AsTask());
                Assert.NotNull(exception); Assert.Equal("ExploreDispatchClosedByGoalException", exception.GetType().Name);
            }
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(new Planner(_ => decision), work).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ResultStatus.Passed, s.Run.Primary!.Status); Assert.Equal(1, sends);
        Assert.Equal(1, s.Run.Budget.Snapshot.Actions); Assert.Equal(0, s.Run.Budget.Snapshot.ReservedActions);
        Assert.DoesNotContain(s.Run.Events, x => x.Reason == RunReason.ActionUnconfirmed);
    }
    [Fact]
    public async Task AcknowledgedSendWithoutGuardConsumesUncertainAttempt()
    {
        var s = new Setup(); var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, _ => new ExploreSend<bool>(true, true), token);
            return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(planner, work));
        Assert.Equal(RunReason.ActionUnconfirmed, s.Run.Primary!.Cause.Reason);
        Assert.Equal(1, s.Run.Budget.Snapshot.Actions); Assert.Equal(0, s.Run.Budget.Snapshot.ReservedActions);
        Assert.Single(planner.Inputs); Assert.Equal(1, work.Calls);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task UnconfirmedOwnedInputAlwaysReceivesFallbackCleanup(bool released)
    {
        var s = new Setup(); bool held = false; int releases = 0;
        var planner = new Planner(_ => Action());
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); held = true; return new ExploreSend<bool>(true, true); }, token);
            return new(ExploreWorkStatus.Unconfirmed, false);
        });
        Assert.False(await s.Execute(planner, work, release: _ => { releases++; if (released) held = false; return ValueTask.FromResult(released); }));
        var primary = s.Run.Primary;
        var outcome = await s.Cleanup.CompleteAsync(s.Run, s.Clock);
        Assert.Equal(1, releases); Assert.Equal(!released, held); Assert.Equal(primary, s.Run.Primary);
        Assert.Equal(!released, outcome.PostProcessing.Any(x => x.Reason == PostProcessingReason.InputReleaseUnconfirmed));
    }
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task InitialPrivateTimestampIsTypedContractFailureBeforePlanner(int milliseconds)
    {
        var s = new Setup(); var frame = s.Feed.Frame(); var invalid = TimeSpan.FromMilliseconds(milliseconds);
        frame = frame with { Run = frame.Run with { CapturedAt = invalid }, Progress = frame.Progress with { CapturedAt = invalid } };
        var planner = new Planner(_ => Finish()); var work = new Work((_, _, _) => throw new InvalidOperationException());
        var failure = await Assert.ThrowsAsync<RunFailureException>(() => s.Execute(planner, work, initial: frame).AsTask());
        Assert.Equal(new RunEvent(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract), failure.Cause);
        Assert.IsType<ArgumentException>(failure.InnerException); Assert.NotNull(failure.InnerException!.StackTrace);
        Assert.Empty(planner.Inputs); Assert.Equal(0, work.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InterruptedSendWithoutReceiptStillAttemptsOwnedRelease(bool releaseThrows)
    {
        var s = new Setup(); using var cancel = new CancellationTokenSource(); bool held = false; int releases = 0;
        var work = new Work(async (_, calls, token) =>
        {
            await calls.SendAsync(0, before => { before(); held = true; cancel.Cancel(); return new ExploreSend<bool>(true, true); }, token);
            await Task.Delay(Timeout.Infinite, token); return new(ExploreWorkStatus.Completed, true);
        });
        Assert.False(await s.Execute(new Planner(_ => Action()), work, cancel.Token, release: _ =>
        { releases++; if (releaseThrows) throw new IOException("release-fault"); held = false; return ValueTask.FromResult(true); }));
        var primary = s.Run.Primary; Assert.Equal(1, s.Run.Budget.Snapshot.Actions);
        var outcome = await s.Cleanup.CompleteAsync(s.Run, s.Clock, cancel.Token);
        Assert.Equal(1, releases); Assert.Equal(releaseThrows, held); Assert.Equal(primary, outcome.Primary);
        Assert.Equal(releaseThrows, outcome.PostProcessing.Any(x => x.Reason == PostProcessingReason.InputReleaseUnconfirmed));
    }
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task InitialPrivateTimestampRetainsContractCauseThroughRunExecutor(int milliseconds)
    {
        var s = new Setup(); var run = new RunSession(s.Run.Limits, s.Clock, s.Clock);
        var cleanup = new OwnedCleanup(); var planner = new Planner(_ => Finish());
        var work = new Work((_, _, _) => throw new InvalidOperationException());
        var outcome = await RunExecutor.ExecuteAsync(run, s.Clock, cleanup, (_, _) => ValueTask.FromResult(true),
            (owner, token) =>
            {
                var frame = s.Feed.Frame(); var invalid = TimeSpan.FromMilliseconds(milliseconds);
                frame = frame with { Run = frame.Run with { CapturedAt = invalid }, Progress = frame.Progress with { CapturedAt = invalid } };
                return ExploreDriver.ExecuteRunningAsync(owner, new(owner, s.Clock, new Authority(), "run-1", "objective", s.Limits),
                    planner, s.Feed, work, s.Definitions, s.Limits, frame, _ => ValueTask.FromResult(true), cleanup, token);
            });
        Assert.Equal(ResultStatus.Invalid, outcome.Primary.Status);
        Assert.Equal(RunReason.ObservationContractViolation, outcome.Primary.Cause.Reason);
        Assert.Contains(outcome.Exceptions, x => x.Type == typeof(ArgumentException).FullName && x.StackTrace is not null);
        Assert.Empty(planner.Inputs);
    }
}

internal static class ExploreTestJson
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
