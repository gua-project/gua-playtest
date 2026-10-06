using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    private sealed class Clock : IClock
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            timers.Add((Elapsed + duration, completion));
            return new(completion.Task);
        }
        public void At(int milliseconds)
        {
            Elapsed = TimeSpan.FromMilliseconds(milliseconds);
            foreach (var timer in timers.Where(x => x.Due <= Elapsed).ToArray()) timer.Completion.TrySetResult();
        }
    }
    private static RunLimits Limits(long actions = 3, long decisions = 3, long recovery = 2)
        => new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), actions, decisions, recovery, 1024);
    private static PreparedCondition Condition(bool time = false)
    {
        var node = JsonNode.Parse("""{"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"role":{"value":"button"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject();
        if (time) node = new() { ["kind"] = "time", ["condition"] = node, ["withinMilliseconds"] = 5000 };
        return PreparedCondition.Create(node, new(10, 1000));
    }
    private static ConditionObservationUnit Unit(string value = "true", bool time = false)
        => new([KeyValuePair.Create(time ? "$/condition" : "$", new ConditionLeafObservation("scope", true,
            [new ConditionTargetObservation("target", "{\"type\":\"bool\",\"value\":" + value + "}")]))]);
    private static RunSession Running(Clock clock, bool success = true, bool failure = false,
        RunLimits? limits = null, CompletionPolicy policy = CompletionPolicy.OnGoal, Clock? conditionClock = null)
    {
        var run = new RunSession(limits ?? Limits(), clock, conditionClock ?? clock,
            success ? Condition() : null, failure ? Condition() : null, policy);
        run.BeginPreparation(); run.BeginRunning(); return run;
    }
    private static RunEvent Event(RunReason reason) => new(reason, RunPhase.Execution, RunOrigin.Host);

    // Independent complete 32-row candidate presence table (contract/failure/cancel/deadline/success).
    public static IEnumerable<object[]> Priorities()
    {
        string[] expected = [
            "None", "Invalid", "Failed", "Invalid", "Aborted", "Invalid", "Failed", "Invalid",
            "TimedOut", "Invalid", "Failed", "Invalid", "Aborted", "Invalid", "Failed", "Invalid",
            "Passed", "Invalid", "Failed", "Invalid", "Aborted", "Invalid", "Failed", "Invalid",
            "TimedOut", "Invalid", "Failed", "Invalid", "Aborted", "Invalid", "Failed", "Invalid"];
        for (var mask = 0; mask < 32; mask++) yield return [mask, expected[mask]];
    }
    [Theory, MemberData(nameof(Priorities))]
    public void FullPriorityTableIsIndependentOfCallbackOrder(int mask, string expected)
    {
        foreach (var reverse in new[] { false, true })
        {
            var clock = new Clock(); var run = Running(clock);
            var events = new List<RunEvent>();
            if ((mask & 1) != 0) events.Add(Event(RunReason.InvalidContract));
            if ((mask & 2) != 0) events.Add(Event(RunReason.ActionFailed));
            if (reverse) events.Reverse();
            if ((mask & 8) != 0) clock.At(5000);
            var result = run.Evaluate((mask & 16) != 0 ? Unit() : null, TimeSpan.Zero, events, (mask & 4) != 0);
            Assert.Equal(expected, result?.Status.ToString() ?? "None");
            Assert.Equal(result is null ? ExecutionState.Running : ExecutionState.Completing, run.State);
        }
    }
    [Theory]
    [InlineData(RunReason.InvalidContract, RunReason.ObservationContractViolation, RunReason.InvalidContract)]
    [InlineData(RunReason.FailureCondition, RunReason.ActionFailed, RunReason.FailureCondition)]
    [InlineData(RunReason.GoalImpossible, RunReason.ActionsExhausted, RunReason.GoalImpossible)]
    [InlineData(RunReason.ActionsExhausted, RunReason.DecisionsExhausted, RunReason.ActionsExhausted)]
    public void EqualPriorityTieBreakRetainsLosingEvents(RunReason a, RunReason b, RunReason expected)
    {
        foreach (var events in new[] { new[] { Event(a), Event(b) }, new[] { Event(b), Event(a) } })
        {
            var run = Running(new Clock()); Assert.Equal(expected, run.Evaluate(candidates: events)!.Cause.Reason);
            Assert.Equal(2, run.Events.Count);
        }
    }
    [Fact]
    public void FailureMonitorAlwaysWinsEvenWhenSuccessSameCapture()
    {
        var run = Running(new Clock(), failure: true);
        Assert.Equal(RunReason.FailureCondition, run.Evaluate(Unit(), TimeSpan.Zero, failureUnit: Unit())!.Cause.Reason);
        Assert.True(run.GoalVerified); Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Fact]
    public void InvalidObservationCannotPassOrEscapeToUnverified()
    {
        var run = Running(new Clock());
        Assert.Equal(ResultStatus.Invalid, run.Evaluate(Unit("1"), TimeSpan.Zero, executionComplete: true)!.Status);
        Assert.False(run.GoalVerified);
    }
    [Theory]
    [InlineData(false, ResultStatus.Unverified, 5)]
    [InlineData(true, ResultStatus.Failed, 1)]
    public async Task FinishClaimDoesNotSubstituteForMachineSuccess(bool hasSuccess, ResultStatus expected, int exit)
    {
        var clock = new Clock(); var run = Running(clock, success: hasSuccess);
        Assert.Equal(expected, run.Evaluate(executionComplete: true)!.Status);
        var result = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(exit, result.ExitCode); Assert.Equal(ExecutionState.Finished, run.State);
    }
    [Fact]
    public void TemporalInclusiveStartDoesNotOverrideGlobalDeadline()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, Condition(time: true));
        run.BeginPreparation(); run.BeginRunning(); clock.At(5000);
        Assert.Equal(ResultStatus.TimedOut, run.Evaluate(Unit(time: true), clock.Elapsed)!.Status);
        Assert.True(run.GoalVerified);
    }
    [Fact]
    public void DelayedCaptureAndSharedConditionOriginAreDistinctFromDeliveryDeadline()
    {
        var real = new Clock(); var simulation = new Clock();
        var run = new RunSession(Limits(), real, simulation, Condition(time: true), Condition(time: true));
        run.BeginPreparation(); real.At(100); run.BeginRunning();
        real.At(5100);
        Assert.Equal(ResultStatus.Failed, run.Evaluate(Unit(time: true), TimeSpan.Zero, failureUnit: Unit(time: true))!.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(100), run.RunningOrigin);
        Assert.Contains(run.Events, x => x.Reason == RunReason.MaxDuration);
    }
    [Fact]
    public void WholeSegmentCannotPartiallyReserveAndCountsOneActionDecision()
    {
        var run = Running(new Clock(), limits: Limits(actions: 2));
        Assert.Null(run.ApproveOperation(3, TimeSpan.FromSeconds(1)));
        Assert.Equal(new BudgetSnapshot(0, 0, 0, 0, 0), run.Budget.Snapshot);
        var segment = run.ApproveOperation(2, TimeSpan.FromSeconds(1))!;
        segment.BeginDispatch(0); segment.Actions!.ConfirmSent(0); segment.BeginDispatch(1);
        Assert.Equal(new BudgetSnapshot(2, 0, 0, 1, 0), run.Budget.Snapshot);
        Assert.Equal(new[] { DeliveryState.Sent, DeliveryState.Uncertain }, segment.Actions.Deliveries);
    }
    [Fact]
    public void UncertainDeliveryNeverRefundsAndPrimaryClosesDispatch()
    {
        var run = Running(new Clock()); var operation = run.ApproveOperation(3, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0);
        Assert.Throws<InvalidOperationException>(() => operation.Actions!.ConfirmNotSent(0));
        run.Evaluate(cancelled: true);
        Assert.Equal(new[] { DeliveryState.Uncertain, DeliveryState.NotSent, DeliveryState.NotSent }, operation.Actions!.Deliveries);
        Assert.Equal(1, run.Budget.Snapshot.Actions);
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(1));
    }
    [Fact]
    public void FinalPurchaseRetainsFiniteObservationOpportunityThenPasses()
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
        Assert.True(run.ActionsClosing); Assert.Null(run.RequestPlanner());
        Assert.Null(run.ApproveOperation(0, TimeSpan.FromSeconds(1)));
        Assert.Null(run.Evaluate());
        clock.At(500); Assert.True(operation.ConfirmResult()); clock.At(1500); operation.Complete();
        Assert.Equal(ResultStatus.Passed, run.Evaluate(Unit(), clock.Elapsed)!.Status);
        Assert.Contains(run.Events, x => x.Reason == RunReason.ActionsExhausted);
    }
    [Fact]
    public void FinalUnknownActionTimesOutWithoutInventingNotSent()
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!; operation.BeginDispatch(0);
        clock.At(1000);
        Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate(Unit(), TimeSpan.Zero)!.Cause.Reason);
        Assert.Equal(DeliveryState.Uncertain, operation.Actions!.Deliveries[0]);
    }
    [Fact]
    public void LastPlannerDecisionCanApproveOnceAndRecoveryConsumesBothBudgets()
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(decisions: 1, recovery: 1));
        var permit = run.RequestPlanner(recovering: true)!;
        Assert.Equal(1, run.Budget.Snapshot.Decisions); Assert.Equal(1, run.Budget.Snapshot.RecoveryDecisions);
        Assert.Null(run.RequestPlanner()); Assert.Null(run.Evaluate());
        var operation = permit.Approve(1, TimeSpan.FromSeconds(1)); Assert.NotNull(operation);
        Assert.Null(permit.Approve(1, TimeSpan.FromSeconds(1)));
        operation!.BeginDispatch(0); operation.ConfirmResult(); operation.Complete();
        Assert.Equal(ResultStatus.Passed, run.Evaluate(Unit(), TimeSpan.Zero)!.Status);
    }
    [Fact]
    public void AfterPlanRetainsGoalButContinuesFailureMonitoring()
    {
        var clock = new Clock(); var run = Running(clock, policy: CompletionPolicy.AfterPlan);
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero)); Assert.True(run.GoalVerified);
        Assert.Equal(ResultStatus.Failed, run.Evaluate(Unit("false"), TimeSpan.Zero,
            [Event(RunReason.ActionFailed)], executionComplete: true)!.Status);
    }
    [Theory]
    [InlineData(true, 11)]
    [InlineData(false, 1)]
    public async Task IncompleteCleanupPreservesPrimaryAndExitCode(bool passed, int expectedExit)
    {
        var clock = new Clock(); var run = Running(clock);
        var primary = passed ? run.Evaluate(Unit(), TimeSpan.Zero) : run.Evaluate(candidates: [Event(RunReason.ActionFailed)]);
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.InputRelease, _ => ValueTask.FromResult(false));
        run.Evaluate(cancelled: true); Assert.Same(primary, run.Primary);
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(expectedExit, outcome.ExitCode); Assert.Same(primary, outcome.Primary);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.InputReleaseUnconfirmed);
        Assert.Throws<InvalidOperationException>(() => run.BeginRunning());
    }
    [Fact]
    public async Task AllPathsCleanupOnlyAcquiredResourcesAndPreservesOriginalException()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        var cleanup = new OwnedCleanup(); var releases = 0;
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
            throw new IOException("private details");
        }, (_, _) => throw new Exception("execute must not run"));
        Assert.Equal(ResultStatus.Failed, result.Primary.Status); Assert.Equal(1, releases);
        Assert.Equal("System.IO.IOException", result.Exceptions.Single().Type);
        Assert.Contains(nameof(AllPathsCleanupOnlyAcquiredResourcesAndPreservesOriginalException), result.Exceptions.Single().StackTrace);
    }
    [Fact]
    public async Task CancellationDuringPreparationStillReleasesOwnedResources()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); var cleanup = new OwnedCleanup();
        using var cancel = new CancellationTokenSource(); var released = false;
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, token => { Assert.False(token.IsCancellationRequested); released = true; return ValueTask.FromResult(true); });
            cancel.Cancel(); return ValueTask.FromResult(true);
        }, (_, _) => ValueTask.FromResult(true), cancel.Token);
        Assert.Equal(ResultStatus.Aborted, result.Primary.Status); Assert.True(released);
    }
    [Fact]
    public async Task ArtifactFailureAndCancelDoNotSkipLaterOwnedRelease()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(candidates: [Event(RunReason.ActionFailed)]);
        using var cancel = new CancellationTokenSource(); var order = new List<CleanupStage>(); var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.ResourceRelease, token => { Assert.False(token.IsCancellationRequested); order.Add(CleanupStage.ResourceRelease); return ValueTask.FromResult(true); });
        cleanup.Register(CleanupStage.Diagnostics, _ => { order.Add(CleanupStage.Diagnostics); return ValueTask.FromResult(true); });
        cleanup.Register(CleanupStage.Artifacts, _ => { order.Add(CleanupStage.Artifacts); cancel.Cancel(); throw new IOException("artifact"); });
        var outcome = await cleanup.CompleteAsync(run, clock, cancel.Token);
        Assert.Equal(new[] { CleanupStage.Diagnostics, CleanupStage.Artifacts, CleanupStage.ResourceRelease }, order);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.ArtifactFailed);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
    }
    [Fact]
    public async Task NoncooperativeArtifactIsBoundedWithoutSkippingReleaseOrChangingPrimary()
    {
        var clock = new Clock(); var simulation = new Clock(); var run = Running(clock, conditionClock: simulation);
        run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup(); var released = false;
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanup.Register(CleanupStage.Artifacts, _ => new(never.Task));
        cleanup.Register(CleanupStage.InputRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var finishing = cleanup.CompleteAsync(run, clock).AsTask();
        clock.At(500); var outcome = await finishing;
        Assert.True(released); Assert.Equal(TimeSpan.Zero, simulation.Elapsed); Assert.Equal(11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupTimeout);
        never.SetResult(true); Assert.Equal(11, outcome.ExitCode);
    }
    [Fact]
    public async Task PreparationSafetyDeadlineRunsWhileSimulationPaused()
    {
        var clock = new Clock(); var simulation = new Clock(); var run = new RunSession(Limits(), clock, simulation);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => new(never.Task), (_, _) => ValueTask.FromResult(true)).AsTask();
        clock.At(1000); var outcome = await result;
        Assert.Equal(RunReason.PreparationTimeout, outcome.Primary.Cause.Reason);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(TimeSpan.Zero, simulation.Elapsed);
        never.SetResult(true);
    }
    [Fact]
    public async Task StateTransitionsAndLateEventsAreFrozen()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        Assert.Equal(ExecutionState.Created, run.State); Assert.Throws<InvalidOperationException>(() => run.BeginRunning());
        run.BeginPreparation(); Assert.Equal(ExecutionState.Preparing, run.State);
        run.BeginRunning(); Assert.Equal(ExecutionState.Running, run.State);
        run.Evaluate(cancelled: true); Assert.Equal(ExecutionState.Completing, run.State);
        var result = await new OwnedCleanup().CompleteAsync(run, clock);
        Assert.Equal(ExecutionState.Finished, run.State); var before = run.Events.Count;
        Assert.Same(result.Primary, run.Evaluate(Unit(), TimeSpan.Zero, [Event(RunReason.InvalidContract)]));
        Assert.Equal(before, run.Events.Count);
    }
    private sealed class Feed(Func<RunObservation> capture) : IRunObservationFeed
    {
        public TaskCompletionSource Change { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(capture()); }
        public ValueTask WaitForChangeAsync(CancellationToken token) { Waiting.TrySetResult(); return new(Change.Task.WaitAsync(token)); }
    }
    [Fact]
    public async Task MonitorObservesFailureWhilePlannerDoesNotRespondAndRevokesPermit()
    {
        var clock = new Clock(); var run = Running(clock, failure: true); var permit = run.RequestPlanner()!;
        var isFailure = false; var feed = new Feed(() => new(clock.Elapsed, Unit("false"), Unit(isFailure ? "true" : "false")));
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(never.Task), _ => []).AsTask();
        await feed.Waiting.Task;
        isFailure = true; feed.Change.SetResult();
        Assert.False((await monitoring).Completed); Assert.Equal(RunReason.FailureCondition, run.Primary!.Cause.Reason);
        Assert.Null(permit.Approve(1, TimeSpan.FromSeconds(1))); never.SetResult(1);
    }
    [Fact]
    public async Task MonitorRealDeadlineContinuesWithPausedConditionClockAndNoNotifications()
    {
        var clock = new Clock(); var simulation = new Clock(); var run = Running(clock, conditionClock: simulation);
        var permit = run.RequestPlanner()!; var feed = new Feed(() => new(simulation.Elapsed, Unit("false"), Unit("false")));
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitoring = RunMonitor.AwaitAsync(run, clock, simulation, feed, _ => new ValueTask<int>(never.Task), _ => []).AsTask();
        await feed.Waiting.Task; clock.At(1000);
        Assert.False((await monitoring).Completed); Assert.Equal(RunReason.PlannerTimeout, run.Primary!.Cause.Reason);
        Assert.Equal(TimeSpan.Zero, simulation.Elapsed); Assert.Null(permit.Approve(0, TimeSpan.FromSeconds(1))); never.SetResult(1);
    }
    [Fact]
    public async Task MonitorArbitratesReadyWorkFailureAndSuccessAsOneUnit()
    {
        var clock = new Clock(); var run = Running(clock);
        var feed = new Feed(() => new(TimeSpan.Zero, Unit(), Unit("false")));
        var result = await RunMonitor.AwaitAsync(run, clock, clock, feed, _ => ValueTask.FromResult(1), _ => [Event(RunReason.ActionFailed)]);
        Assert.False(result.Completed); Assert.Equal(ResultStatus.Failed, run.Primary!.Status);
        Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    public static IEnumerable<object[]> Transitions()
    {
        string[,] table = {
            { "Preparing", "Rejected", "Created", "Completing", "Rejected" },
            { "Rejected", "Running", "Preparing", "Completing", "Rejected" },
            { "Rejected", "Rejected", "Running", "Completing", "Rejected" },
            { "Rejected", "Rejected", "Completing", "Completing", "Finished" },
            { "Rejected", "Rejected", "Finished", "Finished", "Rejected" }
        };
        for (var state = 0; state < 5; state++) for (var operation = 0; operation < 5; operation++)
            yield return [state, operation, table[state, operation]];
    }
    [Theory, MemberData(nameof(Transitions))]
    public async Task IndependentFullStateOperationTable(int state, int operation, string expected)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); var cleanup = new OwnedCleanup();
        if (state >= 1) run.BeginPreparation();
        if (state >= 2) run.BeginRunning();
        if (state >= 3) run.Evaluate(cancelled: true);
        if (state >= 4) await cleanup.CompleteAsync(run, clock);
        async Task Apply()
        {
            switch (operation)
            {
                case 0: run.BeginPreparation(); break;
                case 1: run.BeginRunning(); break;
                case 2: run.Evaluate(); break;
                case 3: run.Evaluate(cancelled: true); break;
                case 4: await cleanup.CompleteAsync(run, clock); break;
            }
        }
        if (expected == "Rejected") await Assert.ThrowsAsync<InvalidOperationException>(Apply);
        else { await Apply(); Assert.Equal(expected, run.State.ToString()); }
    }
    [Theory]
    [InlineData(false, ResultStatus.Unverified)]
    [InlineData(true, ResultStatus.Failed)]
    public void BudgetCompletionSeparatesExplorationFromUnconfirmedSuccess(bool success, ResultStatus expected)
    {
        var run = Running(new Clock(), success: success, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); Assert.True(operation.ConfirmResult()); operation.Complete();
        Assert.Equal(expected, run.Evaluate()!.Status);
    }
    [Fact]
    public void LateActionReplyCannotChangeUncertainOrBypassExpiredDeadline()
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); clock.At(1000);
        Assert.False(operation.ConfirmResult()); Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate()!.Cause.Reason);
        Assert.Equal(DeliveryState.Uncertain, operation.Actions!.Deliveries[0]);
    }
    [Fact]
    public void FutureCaptureFailsClosedAndKeepsOriginalEvidence()
    {
        var run = Running(new Clock());
        Assert.Equal(ResultStatus.Invalid, run.Evaluate(Unit(), TimeSpan.FromSeconds(1))!.Status);
        Assert.Equal(RunReason.ObservationContractViolation, run.Primary!.Cause.Reason);
        Assert.Equal("System.InvalidOperationException", run.Exceptions.Single().Type);
    }
    [Fact]
    public void CompletingUnconfirmedOperationCannotDiscardItsFailureAndPass()
    {
        var run = Running(new Clock()); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); operation.Complete();
        Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate(Unit(), TimeSpan.Zero)!.Cause.Reason);
    }
    [Fact]
    public void DispatchCannotStartAfterResultDeadlineBeforeNextEvaluation()
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        clock.At(1000); Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
        Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]);
    }
    private sealed class BlockedFeed : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => new(new TaskCompletionSource<RunObservation>().Task);
        public ValueTask WaitForChangeAsync(CancellationToken token) => throw new InvalidOperationException("unreachable");
    }
    [Fact]
    public async Task ReadyWorkFailureIsNotHiddenByBlockedCaptureAndGlobalDeadline()
    {
        var clock = new Clock(); var run = Running(clock);
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, new BlockedFeed(),
            _ => ValueTask.FromResult(1), _ => [Event(RunReason.ActionFailed)]).AsTask();
        clock.At(5000); Assert.False((await monitoring).Completed);
        Assert.Equal(RunReason.ActionFailed, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.MaxDuration);
    }
}
