using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ConfirmedPlannerResponseHasFiniteNonRebasedObservationWindow(bool globalClamp)
    {
        var clock = new Clock(); var defaults = Limits(); var limits = new RunLimits(globalClamp ? TimeSpan.FromMilliseconds(1500) : defaults.MaxDuration,
            defaults.PreparationTimeout, defaults.CleanupTimeout, defaults.PlannerTimeout, defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, clock, clock); run.BeginPreparation(); run.BeginRunning(); var permit = run.RequestPlanner()!;
        clock.At(900); Assert.True(permit.ConfirmResponse()); var deadline = run.NextRealEvaluationAt;
        Assert.Equal(TimeSpan.FromMilliseconds(globalClamp ? 1500 : 2900), deadline);
        clock.At(950); Assert.True(permit.ConfirmResponse()); Assert.Equal(deadline, run.NextRealEvaluationAt);
        clock.At(globalClamp ? 1500 : 2900); var primary = run.Evaluate()!;
        Assert.Equal(globalClamp ? RunReason.MaxDuration : RunReason.WaitExpired, primary.Cause.Reason);
        Assert.DoesNotContain(run.Events, x => x.Reason == RunReason.PlannerTimeout); Assert.Null(permit.Approve(0, TimeSpan.FromSeconds(1)));
    }
    private sealed class FaultingDelayClock(bool asynchronous) : IClock
    {
        public bool Enabled = true;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (!Enabled) return new(Task.Delay(duration, token));
            Called.TrySetResult(); var fault = new IOException("clock timer");
            if (asynchronous) return ValueTask.FromException(fault); throw fault;
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CleanupTimerFaultDoesNotCancelCooperativeReleaseEarly(bool asynchronous)
    {
        var clock = new FaultingDelayClock(asynchronous); var run = new RunSession(Limits(), clock, clock, Condition());
        run.BeginPreparation(); run.BeginRunning(); run.Evaluate(Unit(), TimeSpan.Zero);
        var cleanup = new OwnedCleanup(); var release = new TaskCompletionSource<bool>(); bool earlyCancel = false;
        cleanup.Register(CleanupStage.ResourceRelease, token =>
        {
            token.Register(() => { if (!release.Task.IsCompletedSuccessfully) { earlyCancel = true; release.TrySetCanceled(token); } });
            return new(release.Task);
        });
        var finishing = cleanup.CompleteAsync(run, clock).AsTask(); await clock.Called.Task;
        Assert.False(finishing.IsCompleted); release.SetResult(true); var outcome = await finishing;
        Assert.False(earlyCancel); Assert.Equal(11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid && x.Exception?.Type == "System.IO.IOException");
        Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task TimedOutBoundaryRecoveryRetainsClockContractRejection(int kind)
    {
        var condition = new LaunchReadClock { Now = TimeSpan.FromMilliseconds(100) };
        var pending = new TaskCompletionSource<RunStartBoundary>(); RunStartBoundary? certificate = null;
        var real = new FiniteRaceClock(() =>
        {
            if (kind == 3) condition.OnRead = () => condition.OnRead = () => condition.Now = TimeSpan.FromMilliseconds(50);
            else condition.Now = kind switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
            pending.SetResult(certificate!);
        });
        var run = new RunSession(Limits(), real, condition, Condition(), Condition()); bool executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (session, _, _) =>
        {
            var request = session.ArmRunningBoundary();
            certificate = request.Certify(request.RequestId, TimeSpan.Zero,
                new(TimeSpan.FromMilliseconds(100), Unit(), Unit()), "fresh/source", true);
            return new(pending.Task);
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Clock), outcome.Primary.Cause);
        Assert.Equal(2, outcome.ExitCode); Assert.False(executed); Assert.False(run.GoalVerified); Assert.Null(run.RunningOrigin);
        Assert.Contains(outcome.Events, x => x.Reason == RunReason.PreparationTimeout);
        Assert.DoesNotContain(outcome.Events, x => x.Reason == RunReason.ObservationContractViolation);
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OwnerConfirmsOnTimeWorkBeforePostWorkCapture(bool planner, bool late)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, planner ? null : Condition());
        run.BeginPreparation(); run.BeginRunning(); var permit = planner ? run.RequestPlanner() : null;
        var operation = planner ? null : run.ApproveOperation(1, TimeSpan.FromSeconds(2));
        if (operation is not null) { operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0); }
        var old = new TaskCompletionSource<RunObservation>(); var final = new TaskCompletionSource<RunObservation>();
        var work = new TaskCompletionSource<int>(); var accepted = new TaskCompletionSource(); bool confirmed = false; int captures = 0;
        var feed = new CallbackCaptureFeed(_ => new(++captures == 1 ? old.Task : final.Task));
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => [],
            confirmCompletedWork: _ =>
            {
                if (planner) Assert.Null(permit!.Approve(0, TimeSpan.FromSeconds(1)));
                confirmed = planner ? permit!.ConfirmResponse() : operation!.ConfirmResult(); accepted.TrySetResult();
            }).AsTask();
        clock.At(late ? 1100 : 900); work.SetResult(1);
        if (!late)
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(confirmed);
            clock.At(1200); final.SetResult(new(clock.Elapsed, Unit(planner ? "false" : "true"), Unit("false")));
        }
        await monitoring.WaitAsync(TimeSpan.FromSeconds(2));
        if (late)
        {
            Assert.False(confirmed);
            Assert.Equal(planner ? RunReason.PlannerTimeout : RunReason.ActionUnconfirmed, run.Primary!.Cause.Reason);
        }
        else
        {
            Assert.DoesNotContain(run.Events, x => x.Reason is RunReason.PlannerTimeout or RunReason.ActionUnconfirmed);
            if (planner) Assert.NotNull(permit!.Approve(0, TimeSpan.FromSeconds(1)));
            else Assert.Equal(RunReason.GoalSatisfied, run.Primary!.Cause.Reason);
        }
        old.TrySetResult(new(TimeSpan.Zero, Unit("false"), Unit("false"))); final.TrySetResult(new(clock.Elapsed, Unit("false"), Unit("false")));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CleanupEvidenceLimitIsAggregateAndDoesNotSkipRelease(bool failedPrimary)
    {
        var clock = new Clock(); var defaults = Limits();
        var limits = new RunLimits(defaults.MaxDuration, defaults.PreparationTimeout, defaults.CleanupTimeout, defaults.PlannerTimeout,
            defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 32);
        var run = Running(clock, limits: limits); var primary = failedPrimary ? run.Evaluate(candidates: [Event(RunReason.ActionFailed)])! : run.Evaluate(Unit(), TimeSpan.Zero)!;
        var cleanup = new OwnedCleanup(); int stages = 0; bool released = false;
        for (int i = 0; i < 40; i++) cleanup.Register(CleanupStage.Diagnostics, token =>
        {
            stages++; for (int j = 0; j < 4; j++) token.Register(() => throw new IOException("callback")); return ValueTask.FromResult(true);
        });
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Same(primary, outcome.Primary); Assert.Equal(failedPrimary ? 1 : 11, outcome.ExitCode);
        Assert.Equal(40, stages); Assert.True(released); Assert.Equal(32, outcome.PostProcessing.Count);
        Assert.Single(outcome.PostProcessing, x => x.Reason == PostProcessingReason.EvidenceLimitExceeded);
        Assert.Contains(outcome.PostProcessing, x => x.Exception?.Type == "System.IO.IOException");
    }
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    public void OnGoalClosesActiveOperationAndArbitratesUncertainty(int operationKind, bool afterPlan)
    {
        var clock = new Clock(); var run = Running(clock, policy: afterPlan ? CompletionPolicy.AfterPlan : CompletionPolicy.OnGoal);
        RunSession.PlannerPermit? planner = null; RunSession.ApprovedOperation? operation = null;
        if (operationKind == 0) planner = run.RequestPlanner();
        else operation = run.ApproveOperation(operationKind == 1 ? 0 : 1, TimeSpan.FromSeconds(1));
        if (operationKind >= 3)
        {
            operation!.BeginDispatch(0);
            if (operationKind == 4) { operation.Actions!.ConfirmSent(0); Assert.True(operation.ConfirmResult()); }
        }
        var primary = run.Evaluate(Unit(), TimeSpan.Zero);
        Assert.True(run.GoalVerified);
        if (afterPlan)
        {
            Assert.Null(primary); Assert.Equal(ExecutionState.Running, run.State);
            if (operation is not null) Assert.True(operation.IsOpen);
            else Assert.True(planner!.Deadline > clock.Elapsed);
        }
        else
        {
            Assert.Equal(operationKind == 3 ? RunReason.ActionUnconfirmed : RunReason.GoalSatisfied, primary!.Cause.Reason);
            Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
            Assert.Equal(ExecutionState.Completing, run.State); Assert.True(run.ActionsClosing);
            if (operation is not null) Assert.False(operation.IsOpen);
            else Assert.Null(planner!.Approve(0, TimeSpan.FromSeconds(1)));
            Assert.DoesNotContain(run.Events, x => x.Reason is RunReason.PlannerTimeout or RunReason.WaitExpired);
        }
    }
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public async Task InvalidCleanupClockCannotEscapeOrChangePrimary(int kind, bool later)
    {
        var good = TimeSpan.FromMilliseconds(100); var real = new LaunchReadClock { Now = good }; var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition()); run.BeginPreparation(); run.BeginRunning();
        var primary = run.Evaluate(Unit(), TimeSpan.Zero)!; var cleanup = new OwnedCleanup(); bool released = false;
        void RejectNextRead() => real.OnRead = () =>
        { real.Now = kind switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue }; real.OnRead = () => real.Now = good; };
        if (!later) RejectNextRead();
        cleanup.Register(CleanupStage.Diagnostics, _ => { if (later) RejectNextRead(); return ValueTask.FromResult(true); });
        cleanup.Register(CleanupStage.ResourceRelease, token => { Assert.False(token.IsCancellationRequested); released = true; return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, real);
        Assert.Same(primary, outcome.Primary); Assert.Equal(11, outcome.ExitCode); Assert.True(released);
        Assert.Equal(ExecutionState.Finished, run.State);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid && x.Exception?.Type == "System.InvalidOperationException");
        Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StalledCleanupClockStillBoundsBlockedStageAndAttemptsRelease(bool failedPrimary)
    {
        var clock = new Clock(); var defaults = Limits();
        var limits = new RunLimits(defaults.MaxDuration, defaults.PreparationTimeout, TimeSpan.FromMilliseconds(200), defaults.PlannerTimeout,
            defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = Running(clock, limits: limits);
        var primary = failedPrimary ? run.Evaluate(candidates: [Event(RunReason.ActionFailed)])! : run.Evaluate(Unit(), TimeSpan.Zero)!;
        var cleanup = new OwnedCleanup(); bool released = false; var never = new TaskCompletionSource<bool>();
        cleanup.Register(CleanupStage.Diagnostics, _ => new(never.Task));
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        // A stalled real-time epoch cannot grant an additional duration to each cleanup share.
        var outcome = await cleanup.CompleteAsync(run, clock).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(primary, outcome.Primary); Assert.Equal(failedPrimary ? 1 : 11, outcome.ExitCode);
        Assert.True(released); Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupTimeout);
        Assert.Equal(ExecutionState.Finished, run.State); never.SetResult(true);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedWorkStartsFreshCaptureWithoutWaitingForOldDelivery(bool failure)
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition, Condition(), Condition());
        run.BeginPreparation(); run.BeginRunning(); var operation = run.ApproveOperation(0, TimeSpan.FromSeconds(1))!;
        var old = new TaskCompletionSource<RunObservation>(); var work = new TaskCompletionSource<int>(); int captures = 0;
        var feed = new CallbackCaptureFeed(_ => ++captures == 1 ? new(old.Task) : ValueTask.FromResult(
            new RunObservation(condition.Elapsed, Unit(), Unit(failure ? "true" : "false"))));
        var monitoring = RunMonitor.AwaitAsync(run, real, condition, feed, _ => new ValueTask<int>(work.Task), _ => []).AsTask();
        real.At(900); condition.At(900); work.SetResult(1);
        await monitoring.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, captures); Assert.False(old.Task.IsCompleted);
        Assert.True(run.GoalVerified);
        if (!failure) { operation.Complete(); run.Evaluate(); }
        Assert.Equal(failure ? RunReason.FailureCondition : RunReason.GoalSatisfied, run.Primary!.Cause.Reason);
        Assert.DoesNotContain(run.Events, x => x.Reason is RunReason.WaitExpired or RunReason.MaxDuration);
        var primary = run.Primary; old.SetResult(new(TimeSpan.Zero, Unit("false"), Unit("false")));
        Assert.Same(primary, run.Primary); Assert.False(operation.IsOpen);
    }
    private sealed class WakeActionFeed(Action wake) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => ValueTask.FromResult(
            new RunObservation(TimeSpan.FromMilliseconds(100), Unit(time: true), Unit("false")));
        public ValueTask WaitForChangeAsync(CancellationToken token) { wake(); return new(new TaskCompletionSource().Task); }
    }
    [Fact]
    public async Task ConditionClockRegressionBeforeWakeCannotRecoverSilently()
    {
        var real = new Clock(); var condition = new LaunchReadClock { Now = TimeSpan.FromMilliseconds(100) };
        var node = JsonNode.Parse("""{"kind":"time","forMilliseconds":200,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject();
        var run = new RunSession(Limits(), real, condition, PreparedCondition.Create(node, new(10, 1000)));
        run.BeginPreparation(); run.BeginRunning(); var work = new TaskCompletionSource<int>();
        var feed = new WakeActionFeed(() => condition.OnRead = () =>
        { condition.Now = TimeSpan.FromMilliseconds(50); condition.OnRead = () => condition.Now = TimeSpan.FromMilliseconds(100); });
        await RunMonitor.AwaitAsync(run, real, condition, feed, _ => new ValueTask<int>(work.Task), _ => []);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), run.Primary!.Cause);
        Assert.Equal(2, run.Primary.ExitCode); Assert.False(run.GoalVerified); work.SetResult(1);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedCleanupAndCancellationDoNotInventArtifactFailure(bool cancelFirst)
    {
        using var cancel = new CancellationTokenSource(); var completed = new TaskCompletionSource<bool>();
        var real = new CallbackDelayClock(() =>
        { if (cancelFirst) cancel.Cancel(); completed.SetResult(true); if (!cancelFirst) cancel.Cancel(); });
        var condition = new Clock(); var run = new RunSession(Limits(), real, condition, Condition());
        run.BeginPreparation(); run.BeginRunning(); run.Evaluate(Unit(), TimeSpan.Zero);
        var cleanup = new OwnedCleanup(); bool released = false;
        cleanup.Register(CleanupStage.Artifacts, _ => new(completed.Task));
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, real, cancel.Token);
        Assert.Equal(0, outcome.ExitCode); Assert.True(released);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
        Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.ArtifactFailed);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task FiniteProviderCallbackFaultsReachOwnerEvidence(int boundary, int ending)
    {
        var clock = new Clock(); using var cancel = new CancellationTokenSource(); var pending = new TaskCompletionSource<bool>();
        var capturePending = new TaskCompletionSource<RunObservation>(); var workPending = new TaskCompletionSource<int>();
        var run = new RunSession(Limits(), clock, clock); RunOutcome outcome;
        ValueTask<bool> Provider(CancellationToken token)
        {
            token.Register(() => throw new IOException("callback"));
            return ending == 2 ? ValueTask.FromResult(true) : new(pending.Task);
        }
        void Interrupt() { if (ending == 0) cancel.Cancel(); else if (ending == 1) clock.At(boundary == 0 || boundary == 3 ? 1000 : 5000); }
        if (boundary < 2)
        {
            var executing = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(),
                (_, token) => boundary == 0 ? Provider(token) : ValueTask.FromResult(true), (_, token) => Provider(token), cancel.Token).AsTask();
            Interrupt(); outcome = await executing;
        }
        else if (boundary == 2)
        {
            run.BeginPreparation(); run.BeginRunning();
            var feed = new CallbackCaptureFeed(token =>
            {
                token.Register(() => throw new IOException("callback"));
                return ending == 2 ? ValueTask.FromResult(new RunObservation(clock.Elapsed, Unit("false"), Unit("false"))) : new(capturePending.Task);
            });
            var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed,
                _ => ending == 2 ? ValueTask.FromResult(1) : new(workPending.Task), _ => [], cancel.Token).AsTask();
            Interrupt(); await monitoring;
            if (run.Primary is null) run.Evaluate(executionComplete: true);
            outcome = await new OwnedCleanup().CompleteAsync(run, clock);
        }
        else
        {
            run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup();
            cleanup.Register(CleanupStage.Diagnostics, Provider);
            var finishing = cleanup.CompleteAsync(run, clock, cancel.Token).AsTask(); Interrupt(); outcome = await finishing;
        }
        if (boundary == 3) Assert.Contains(outcome.PostProcessing, x => x.Exception?.Type == "System.IO.IOException" && x.Exception.StackTrace is not null);
        else Assert.Contains(outcome.Exceptions, x => x.Type == "System.IO.IOException" && x.StackTrace is not null);
        Assert.Equal(ExecutionState.Finished, run.State);
        pending.TrySetResult(true); capturePending.TrySetResult(new(clock.Elapsed, Unit(), Unit())); workPending.TrySetResult(1);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task InvalidRealClockPreventsMonitoredProviderLaunch(int kind)
    {
        var good = TimeSpan.FromMilliseconds(100); var real = new LaunchReadClock { Now = good }; var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); run.BeginPreparation(); run.BeginRunning(); bool worked = false, captured = false;
        real.OnRead = () =>
        {
            real.Now = kind switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
            real.OnRead = () => real.Now = good;
        };
        await RunMonitor.AwaitAsync(run, real, condition, new CallbackCaptureFeed(_ =>
        { captured = true; return ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit(), Unit())); }), _ =>
        { worked = true; return ValueTask.FromResult(1); }, _ => []);
        Assert.False(worked); Assert.False(captured); Assert.Equal(2, run.Primary!.ExitCode);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), run.Primary.Cause);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 3)]
    public void CaptureTimeContractDoesNotDependOnConditionTrees(int trees, int invalid)
    {
        var real = new Clock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, (trees & 1) != 0 ? Condition() : null,
            (trees & 2) != 0 ? Condition() : null, CompletionPolicy.AfterPlan);
        run.BeginPreparation(); run.BeginRunning(); condition.At(100);
        Assert.Null(run.Evaluate(Unit("false"), TimeSpan.FromMilliseconds(100), failureUnit: Unit("false")));
        var bad = invalid switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(101),
            2 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
        var result = run.Evaluate(Unit("false"), bad, executionComplete: true, failureUnit: Unit("false"))!;
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(new RunEvent(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract), result.Cause);
        Assert.False(run.GoalVerified);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ExplorationFinalCaptureRequiresValidSourceTime(int invalid)
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        run.BeginPreparation(); run.BeginRunning(); condition.At(100); int captures = 0;
        var bad = invalid switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(101),
            2 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
        var feed = new CallbackCaptureFeed(_ => ValueTask.FromResult(new RunObservation(
            ++captures == 1 ? TimeSpan.FromMilliseconds(100) : bad, Unit("false"), Unit("false"))));
        await RunMonitor.AwaitAsync(run, real, condition, feed, _ => ValueTask.FromResult(1), _ => []);
        Assert.Equal(2, captures); Assert.Equal(2, run.Primary!.ExitCode);
        Assert.Equal(RunReason.ObservationContractViolation, run.Primary.Cause.Reason);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void OverflowRetainsFullPhaseAndOriginTieBreak(bool cumulative)
    {
        foreach (var reverse in new[] { false, true })
        {
            var clock = new Clock(); var defaults = Limits();
            var limits = new RunLimits(defaults.MaxDuration, defaults.PreparationTimeout, defaults.CleanupTimeout,
                defaults.PlannerTimeout, defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 32);
            var run = Running(clock, failure: cumulative, limits: limits, policy: CompletionPolicy.AfterPlan);
            var expected = new RunEvent(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Contract);
            var entries = Enumerable.Repeat(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Host), cumulative ? 30 : 31)
                .Append(new(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Runner)).Append(expected).ToArray();
            var result = run.Evaluate(Unit("false"), TimeSpan.Zero, reverse ? entries.Reverse() : entries, failureUnit: Unit())!;
            Assert.Equal(expected, result.Cause); Assert.True(run.Events.Count <= 32);
        }
    }
    [Theory] [InlineData(-1)] [InlineData(2)]
    public void UndefinedCompletionPolicyIsRejectedBeforeRunAuthority(int value)
    {
        var clock = new Clock();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunSession(Limits(), clock, clock, policy: (CompletionPolicy)value));
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task RecoveredRealClockCannotEraseOutsideEvaluateContractRejection(int boundary, int invalidKind)
    {
        var good = TimeSpan.FromMilliseconds(100); var real = new LaunchReadClock { Now = good }; var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup(); bool released = false, executed = false;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        void Transient(int reads)
        {
            void Read()
            {
                if (--reads != 0) { real.OnRead = Read; return; }
                real.Now = invalidKind switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
                real.OnRead = () => real.Now = good;
            }
            real.OnRead = Read;
        }
        RunOutcome outcome;
        if (boundary is 0 or 3)
            outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) =>
            {
                // Finite completion reads twice, preparation Evaluate once, then BeginRunning.
                if (boundary == 0) Transient(4); return ValueTask.FromResult(true);
            }, (session, _) =>
            {
                executed = true; Transient(1); session.RequestPlanner(); return ValueTask.FromResult(true);
            });
        else
            outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (session, _, _) =>
            {
                if (boundary == 1) Transient(1);
                var request = session.ArmRunningBoundary();
                if (boundary == 2) Transient(1);
                return ValueTask.FromResult(request.Certify(request.RequestId, good,
                    new(TimeSpan.Zero, Unit(), Unit("false")), "fresh/source/epoch", true));
            }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, boundary == 3 ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock), outcome.Primary.Cause);
        Assert.Equal(boundary == 3, executed); Assert.True(released); Assert.False(run.GoalVerified);
        Assert.Equal(ExecutionState.Finished, run.State); Assert.Equal(good, real.Now);
        Assert.Contains(outcome.Exceptions, x => x.Type == "System.InvalidOperationException");
    }
    [Fact]
    public async Task FalseExecutionFallbackRetainsCancellationAfterFirstEvaluationSample()
    {
        using var cancel = new CancellationTokenSource(); var real = new LaunchReadClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); int reads = 0;
        var result = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), (_, _) =>
        {
            void Read()
            {
                // Two finite-operation reads precede Evaluate's real-clock read. Its cancellation
                // argument has already been sampled when this third read interrupts the caller.
                if (++reads == 3) cancel.Cancel(); else real.OnRead = Read;
            }
            real.OnRead = Read; return ValueTask.FromResult(false);
        }, cancel.Token);
        Assert.Equal(10, result.ExitCode); Assert.Equal(RunReason.ExecutionError, result.Primary.Cause.Reason);
        Assert.Contains(result.Events, x => x.Reason == RunReason.Cancelled && x.Origin == RunOrigin.User);
        Assert.Equal(ExecutionState.Finished, run.State);
    }
    private sealed class StartSequenceClock(params TimeSpan[] values) : IClock
    {
        private int reads;
        public TimeSpan Elapsed => values[Math.Min(reads++, values.Length - 1)];
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(new TaskCompletionSource().Task);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)]
    public async Task PreparationClockRejectionIsInvalidContractAndStillReleasesOwnedResources(int boundary)
    {
        var zero = TimeSpan.Zero; var good = TimeSpan.FromMilliseconds(100); var reset = TimeSpan.FromMilliseconds(50);
        TimeSpan[] readings = boundary switch
        {
            0 => [good, reset], 1 => [zero, good, reset], 2 => [zero, good, good, reset],
            3 => [zero, good, good, reset], 4 => [zero, good, good, good, reset],
            5 => [zero, good, good, good, good, reset], 6 => [good, reset],
            7 => [zero, good, reset], 8 => [zero, TimeSpan.FromTicks(-1)], _ => [zero, TimeSpan.MaxValue]
        };
        var condition = new StartSequenceClock(readings); var real = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition(), Condition());
        var cleanup = new OwnedCleanup(); bool released = false, executed = false;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        RunOutcome result;
        if (boundary is <= 2 or >= 8)
            result = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), (_, _) =>
            { executed = true; return ValueTask.FromResult(true); });
        else
            result = await RunExecutor.ExecuteAsync(run, real, cleanup, (session, _, _) =>
            {
                var request = session.ArmRunningBoundary();
                return ValueTask.FromResult(request.Certify(request.RequestId, real.Elapsed,
                    new(good, Unit(), Unit("false")), "fresh/source/epoch", true));
            }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.Equal(2, result.ExitCode); Assert.Equal(ResultStatus.Invalid, result.Primary.Status);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Clock), result.Primary.Cause);
        Assert.False(executed); Assert.True(released); Assert.Null(run.RunningOrigin);
        Assert.False(run.GoalVerified); Assert.Equal(ExecutionState.Finished, run.State);
        Assert.Contains(result.Exceptions, x => x.Type is "System.InvalidOperationException" or "System.ArgumentOutOfRangeException");
    }
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    public async Task ProviderTimeoutProvenanceTableNeverClaimsSafetyDeadline(int boundary, bool asynchronous)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); var pending = new TaskCompletionSource<int>();
        ValueTask<T> Fault<T>()
        {
            // Matching message text is deliberately insufficient to forge a safety deadline.
            var error = new TimeoutException("OperationDeadlineReached");
            if (asynchronous) return ValueTask.FromException<T>(error);
            throw error;
        }
        RunOutcome result;
        if (boundary <= 1)
            result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(),
                (_, _) => boundary == 0 ? Fault<bool>() : ValueTask.FromResult(true), (_, _) => Fault<bool>());
        else
        {
            run.BeginPreparation(); run.BeginRunning();
            var feed = new CallbackCaptureFeed(_ => boundary == 2 ? Fault<RunObservation>()
                : ValueTask.FromResult(new RunObservation(clock.Elapsed, Unit("false"), Unit("false"))));
            await RunMonitor.AwaitAsync(run, clock, clock, feed, _ => boundary == 3 ? Fault<int>() : new ValueTask<int>(pending.Task), _ => []);
            result = await new OwnedCleanup().CompleteAsync(run, clock); pending.SetResult(1);
        }
        Assert.Equal(10, result.ExitCode); Assert.Equal(RunReason.ExecutionError, result.Primary.Cause.Reason);
        Assert.Contains(result.Exceptions, x => x.Type == "System.TimeoutException");
        Assert.DoesNotContain(result.Events, x => x.Reason is RunReason.MaxDuration or RunReason.PreparationTimeout);
    }
    [Theory]
    [InlineData(RunReason.ActionUnconfirmed)]
    [InlineData(RunReason.PlannerTimeout)]
    [InlineData(RunReason.WaitExpired)]
    public async Task PendingTerminalEvidenceClosesAuthorityBeforeNextEvaluate(RunReason reason)
    {
        var clock = new Clock(); var run = Running(clock, success: false);
        if (reason == RunReason.PlannerTimeout) { var permit = run.RequestPlanner()!; clock.At(1000); permit.CompleteWithoutOperation(); }
        else
        {
            var operation = run.ApproveOperation(reason == RunReason.ActionUnconfirmed ? 1 : 0, TimeSpan.FromSeconds(1))!;
            if (reason == RunReason.ActionUnconfirmed) operation.BeginDispatch(0); else clock.At(1000);
            operation.Complete();
        }
        Assert.Null(run.Primary); Assert.True(run.ActionsClosing);
        Assert.Null(run.ApproveOperation(1, TimeSpan.FromSeconds(1))); Assert.Null(run.ApproveOperation(0, TimeSpan.FromSeconds(1)));
        Assert.Null(run.RequestPlanner()); bool invoked = false;
        await RunMonitor.AwaitAsync(run, clock, clock, new Feed(() => throw new InvalidOperationException("no capture")),
            _ => { invoked = true; return ValueTask.FromResult(1); }, _ => []);
        Assert.False(invoked); Assert.Equal(reason, run.Primary!.Cause.Reason);
    }
    [Fact]
    public void MissingCaptureTimestampCannotConsumePendingTerminalEvidence()
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); operation.Complete();
        Assert.Throws<ArgumentException>(() => run.Evaluate(Unit()));
        Assert.True(run.ActionsClosing); Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate()!.Cause.Reason);
    }
    [Theory]
    [InlineData("false", "true", 1)]
    [InlineData("true", "false", 1)]
    [InlineData("false", "1", 2)]
    public async Task FinalPostWorkCaptureArbitratesBothBoundariesBeforeGoal(string firstFailure, string finalFailure, int expectedExit)
    {
        var clock = new Clock(); var run = Running(clock, failure: true); var work = new TaskCompletionSource<int>(); int captures = 0;
        var feed = new CallbackCaptureFeed(_ =>
        {
            captures++; var unit = new RunObservation(clock.Elapsed, Unit(), Unit(captures == 1 ? firstFailure : finalFailure));
            if (captures == 1) work.SetResult(1); return ValueTask.FromResult(unit);
        });
        var result = await RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => []);
        Assert.False(result.Completed); Assert.Equal(2, captures); Assert.Equal(expectedExit, run.Primary!.ExitCode);
        Assert.True(run.GoalVerified); Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task FinalPostWorkCaptureRemainsBoundedAndCannotPassOnStaleGoal(bool cancelled, int expectedExit)
    {
        var clock = new Clock(); var run = Running(clock); var work = new TaskCompletionSource<int>();
        var final = new TaskCompletionSource<RunObservation>(); using var cancel = new CancellationTokenSource(); int captures = 0;
        var feed = new CallbackCaptureFeed(_ =>
        {
            if (++captures != 1) return new(final.Task);
            var unit = new RunObservation(clock.Elapsed, Unit(), Unit("false")); work.SetResult(1); return ValueTask.FromResult(unit);
        });
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => [], cancel.Token).AsTask();
        Assert.Equal(2, captures); if (cancelled) cancel.Cancel(); else clock.At(5000);
        Assert.False((await monitoring).Completed); Assert.Equal(expectedExit, run.Primary!.ExitCode);
        final.SetResult(new(clock.Elapsed, Unit(), Unit("false")));
    }
    [Fact]
    public async Task FinalCaptureProviderTimeoutOutranksPreviouslyCapturedGoal()
    {
        var clock = new Clock(); var run = Running(clock); var work = new TaskCompletionSource<int>(); int captures = 0;
        var feed = new CallbackCaptureFeed(_ =>
        {
            if (++captures != 1) throw new TimeoutException("OperationDeadlineReached");
            var unit = new RunObservation(clock.Elapsed, Unit(), Unit("false")); work.SetResult(1); return ValueTask.FromResult(unit);
        });
        await RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => []);
        Assert.Equal(2, captures); Assert.Equal(10, run.Primary!.ExitCode); Assert.True(run.GoalVerified);
        Assert.Contains(run.Exceptions, x => x.Type == "System.TimeoutException");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorUsesSessionTimebaseDespiteForeignClockEpoch(bool execution)
    {
        var owner = new Clock(); owner.At(10000); var foreign = new Clock();
        var run = new RunSession(Limits(), owner, owner); var pending = new TaskCompletionSource<bool>(); bool released = false;
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, foreign, cleanup, (_, _) => execution ? ValueTask.FromResult(true) : new(pending.Task),
            (_, _) => new(pending.Task)).AsTask();
        owner.At(execution ? 15000 : 11000);
        var result = await executing; Assert.True(released);
        Assert.Equal(execution ? RunReason.MaxDuration : RunReason.PreparationTimeout, result.Primary.Cause.Reason);
        Assert.Equal(TimeSpan.Zero, foreign.Elapsed); pending.SetResult(true);
    }
    [Fact]
    public async Task MonitorUsesSessionDeadlineWithForeignClockArguments()
    {
        var owner = new Clock(); owner.At(10000); var foreign = new Clock();
        var run = Running(owner, success: false); var pending = new TaskCompletionSource<int>();
        var monitoring = RunMonitor.AwaitAsync(run, foreign, foreign,
            new Feed(() => new(owner.Elapsed, Unit("false"), Unit("false"))), _ => new ValueTask<int>(pending.Task), _ => []).AsTask();
        owner.At(15000); Assert.False((await monitoring).Completed);
        Assert.Equal(RunReason.MaxDuration, run.Primary!.Cause.Reason); Assert.Equal(TimeSpan.Zero, foreign.Elapsed); pending.SetResult(1);
    }
    [Fact]
    public async Task CleanupUsesSessionDeadlineAndStillAttemptsLaterRelease()
    {
        var owner = new Clock(); owner.At(10000); var foreign = new Clock(); var run = Running(owner);
        run.Evaluate(candidates: [Event(RunReason.ActionFailed)]); var pending = new TaskCompletionSource<bool>(); bool released = false;
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.Artifacts, _ => new(pending.Task));
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var finishing = cleanup.CompleteAsync(run, foreign).AsTask(); owner.At(10500); var result = await finishing;
        Assert.True(released); Assert.Contains(result.PostProcessing, x => x.Reason == PostProcessingReason.ArtifactFailed);
        Assert.DoesNotContain(result.PostProcessing, x => x.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
        Assert.Equal(TimeSpan.Zero, foreign.Elapsed); pending.SetResult(true);
    }
    [Theory]
    [InlineData(1, 2, false, false, "ActionsExhausted,DecisionsExhausted")]
    [InlineData(1, 2, false, true, "ActionsExhausted,DecisionsExhausted")]
    [InlineData(3, 1, true, false, "ActionsExhausted,RecoveryExhausted")]
    [InlineData(3, 1, true, true, "ActionsExhausted,RecoveryExhausted")]
    [InlineData(1, 1, true, false, "ActionsExhausted,DecisionsExhausted,RecoveryExhausted")]
    [InlineData(1, 1, true, true, "ActionsExhausted,DecisionsExhausted,RecoveryExhausted")]
    public void FinalPermitRetainsEveryExhaustedCeilingEvenAfterRefund(long decisions, long recovery, bool recovering, bool sent, string expected)
    {
        var clock = new Clock(); var run = Running(clock, success: false, limits: Limits(actions: 1, decisions: decisions, recovery: recovery));
        var permit = run.RequestPlanner(recovering)!; var operation = permit.Approve(1, TimeSpan.FromSeconds(1))!;
        if (sent) { operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0); Assert.True(operation.ConfirmResult()); }
        else operation.Actions!.ConfirmNotSent(0);
        operation.Complete(); var result = run.Evaluate(candidates: [Event(RunReason.WaitExpired)]);
        Assert.Equal(RunReason.ActionsExhausted, result!.Cause.Reason); Assert.Equal(ResultStatus.Unverified, result.Status);
        Assert.Equal(expected, string.Join(',', run.Events.Where(x => x.Origin == RunOrigin.Budget).Select(x => x.Reason)));
        Assert.Equal(sent ? 1 : 0, run.Budget.Snapshot.Actions);
    }
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 3)]
    public async Task CompletedDriverEvidenceSurvivesDeadlineWithoutExtendingAuthority(bool completed, int expectedExit)
    {
        var pending = new TaskCompletionSource<bool>(); var clock = new DriverDeadlineClock(() => pending.SetResult(completed));
        var condition = new Clock(); var run = new RunSession(Limits(), clock, condition, Condition(), policy: CompletionPolicy.AfterPlan);
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true),
            (session, _) => { Assert.Null(session.Evaluate(Unit(), condition.Elapsed)); return new(pending.Task); });
        Assert.Equal(expectedExit, result.ExitCode); Assert.Contains(result.Events, x => x.Reason == RunReason.MaxDuration);
        if (completed) Assert.Contains(result.Events, x => x.Reason == RunReason.GoalSatisfied);
        else Assert.Contains(result.Events, x => x.Reason == RunReason.ExecutionError);
    }
    private sealed class DriverDeadlineClock(Action finish) : IClock
    {
        private int delays; public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (++delays == 2) { Elapsed += duration; finish(); return ValueTask.CompletedTask; }
            return new(new TaskCompletionSource().Task);
        }
    }
    [Fact]
    public async Task CompletedFalsePreparationRetainsHostFailureAlongsideTimeout()
    {
        var pending = new TaskCompletionSource<bool>(); var clock = new FiniteRaceClock(() => pending.TrySetResult(false));
        var condition = new Clock(); var run = new RunSession(Limits(), clock, condition);
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => new(pending.Task),
            (_, _) => throw new InvalidOperationException("no driver"));
        Assert.Equal(RunReason.PreparationTimeout, result.Primary.Cause.Reason);
        Assert.Contains(result.Events, x => x.Reason == RunReason.ExecutionError && x.Origin == RunOrigin.Host);
    }
    // Literal expected exit codes across preparation/execution/observation/work boundaries.
    [Theory]
    [InlineData(0, 0, 1)] [InlineData(0, 1, 4)] [InlineData(0, 2, 10)]
    [InlineData(1, 0, 1)] [InlineData(1, 1, 4)] [InlineData(1, 2, 10)]
    [InlineData(2, 0, 1)] [InlineData(2, 1, 4)] [InlineData(2, 2, 10)]
    [InlineData(3, 0, 1)] [InlineData(3, 1, 4)] [InlineData(3, 2, 10)]
    public async Task ProviderBoundaryCancellationProvenanceTable(int boundary, int interruption, int expectedExit)
    {
        var clock = new Clock(); using var caller = new CancellationTokenSource();
        using var provider = new CancellationTokenSource(); provider.Cancel();
        var run = new RunSession(Limits(), clock, clock); var cleanup = new OwnedCleanup(); bool released = false;
        cleanup.Register(CleanupStage.InputRelease, _ => { released = true; return ValueTask.FromResult(true); });
        ValueTask<T> Result<T>(CancellationToken supplied)
        {
            caller.Cancel();
            if (interruption == 0) return ValueTask.FromException<T>(new RunFailureException(
                new(RunReason.ActionFailed, boundary == 0 ? RunPhase.Preparation : RunPhase.Execution, RunOrigin.Host)));
            return ValueTask.FromCanceled<T>(interruption == 1 ? supplied : provider.Token);
        }
        RunOutcome outcome;
        if (boundary <= 1)
            outcome = await RunExecutor.ExecuteAsync(run, clock, cleanup,
                (_, token) => boundary == 0 ? Result<bool>(token) : ValueTask.FromResult(true), (_, token) => Result<bool>(token), caller.Token);
        else
        {
            run.BeginPreparation(); run.BeginRunning(); var pending = new TaskCompletionSource<int>();
            var feed = new CallbackCaptureFeed(token => boundary == 2 ? Result<RunObservation>(token)
                : ValueTask.FromResult(new RunObservation(clock.Elapsed, Unit("false"), Unit("false"))));
            await RunMonitor.AwaitAsync(run, clock, clock, feed,
                token => boundary == 3 ? Result<int>(token) : new ValueTask<int>(pending.Task), _ => [], caller.Token);
            outcome = await cleanup.CompleteAsync(run, clock, caller.Token); pending.SetResult(1);
        }
        Assert.True(released); Assert.Equal(expectedExit, outcome.ExitCode); Assert.Equal(ExecutionState.Finished, run.State);
        Assert.Contains(outcome.Events, x => x.Reason == RunReason.Cancelled);
        if (interruption == 2) Assert.Contains(outcome.Exceptions, x => x.Type == "System.Threading.Tasks.TaskCanceledException");
    }
    private sealed class CallbackCaptureFeed(Func<CancellationToken, ValueTask<RunObservation>> capture) : IIndependentRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => capture(token);
        public ValueTask WaitForChangeAsync(CancellationToken token) => throw new InvalidOperationException("unexpected wait");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchGuardRechecksCancellationAndAbsoluteDeadlineAfterRegistration(bool cancelled)
    {
        var clock = new LaunchReadClock(); var condition = new Clock(); using var cancel = new CancellationTokenSource();
        var run = new RunSession(Limits(), clock, condition); run.BeginPreparation(); run.BeginRunning(); bool invoked = false;
        clock.OnRead = () => { if (cancelled) cancel.Cancel(); else clock.Now = TimeSpan.FromSeconds(5); };
        var result = await RunMonitor.AwaitAsync(run, clock, condition, new Feed(() => throw new InvalidOperationException()),
            _ => { invoked = true; return ValueTask.FromResult(1); }, _ => [], cancel.Token);
        Assert.False(invoked); Assert.False(result.Completed);
        Assert.Equal(cancelled ? RunReason.Cancelled : RunReason.MaxDuration, run.Primary!.Cause.Reason);
        if (!cancelled) Assert.DoesNotContain(run.Events, x => x.Reason == RunReason.Cancelled);
    }
    private sealed class LaunchReadClock : IClock
    {
        public Action? OnRead; public TimeSpan Now;
        public TimeSpan Elapsed { get { var callback = OnRead; OnRead = null; callback?.Invoke(); return Now; } }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(new TaskCompletionSource().Task);
    }
    [Theory]
    [InlineData("true", RunReason.FailureCondition)]
    [InlineData("1", RunReason.ObservationContractViolation)]
    [InlineData("false", RunReason.PreparationTimeout)]
    public async Task CompletedTimedOutCertificateRetainsOnlyFailureEvidenceWithoutRunning(string failureValue, RunReason expected)
    {
        var pending = new TaskCompletionSource<RunStartBoundary>(); var condition = new Clock(); RunStartBoundary? boundary = null;
        var clock = new FiniteRaceClock(() => pending.SetResult(boundary!)); var sameCondition = Condition();
        var run = new RunSession(Limits(), clock, condition, sameCondition, sameCondition); bool executed = false, released = false;
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (session, _, _) =>
        {
            var request = session.ArmRunningBoundary();
            boundary = request.Certify(request.RequestId, clock.Elapsed, new(condition.Elapsed, Unit(), Unit(failureValue)), "live", true);
            return new(pending.Task);
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.False(executed); Assert.True(released); Assert.Null(run.RunningOrigin); Assert.False(run.GoalVerified);
        Assert.Equal(expected, result.Primary.Cause.Reason); Assert.Contains(result.Events, x => x.Reason == RunReason.PreparationTimeout);
        Assert.DoesNotContain(result.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderInternalCancellationRacingCallerRemainsFailure(bool execution, bool asynchronous)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); using var cancel = new CancellationTokenSource();
        using var internalCancel = new CancellationTokenSource(); internalCancel.Cancel(); bool released = false;
        ValueTask<bool> Fault()
        {
            cancel.Cancel();
            if (asynchronous) return new(Task.FromCanceled<bool>(internalCancel.Token));
            throw new OperationCanceledException(internalCancel.Token);
        }
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.InputRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => execution ? ValueTask.FromResult(true) : Fault(),
            (_, _) => Fault(), cancel.Token);
        Assert.True(released); Assert.Equal(10, result.ExitCode); Assert.Equal(RunReason.ExecutionError, result.Primary.Cause.Reason);
        Assert.Equal(execution ? RunPhase.Execution : RunPhase.Preparation, result.Primary.Cause.Phase);
        Assert.Contains(result.Events, x => x.Reason == RunReason.Cancelled);
        Assert.Contains(result.Exceptions, x => x.Type == (asynchronous ? "System.Threading.Tasks.TaskCanceledException" : "System.OperationCanceledException"));
    }
    [Theory]
    [InlineData(RunReason.ActionsExhausted)]
    [InlineData(RunReason.DecisionsExhausted)]
    [InlineData(RunReason.RecoveryExhausted)]
    public void TerminalClosureMakesRetainedBudgetCauseEligibleBeforeArbitration(RunReason expected)
    {
        var clock = new Clock(); var run = Running(clock, success: false,
            limits: Limits(actions: expected == RunReason.ActionsExhausted ? 1 : 3,
                decisions: expected == RunReason.DecisionsExhausted ? 1 : 3, recovery: 1));
        if (expected == RunReason.ActionsExhausted) Assert.NotNull(run.ApproveOperation(1, TimeSpan.FromSeconds(1)));
        else Assert.NotNull(run.RequestPlanner(recovering: expected == RunReason.RecoveryExhausted));
        var result = run.Evaluate(candidates: [Event(RunReason.WaitExpired)]);
        Assert.Equal(expected, result!.Cause.Reason); Assert.Equal(ResultStatus.Unverified, result.Status);
        Assert.Contains(run.Events, x => x.Reason == RunReason.WaitExpired);
        Assert.Single(run.Events, x => x.Reason == expected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedPreparationEvidenceSurvivesCallerCancellation(bool certified)
    {
        using var cancel = new CancellationTokenSource(); var condition = new Clock();
        var preparation = new TaskCompletionSource<bool>(); var boundary = new TaskCompletionSource<RunStartBoundary>();
        RunStartBoundary? certificate = null;
        var clock = new CallbackDelayClock(() => { cancel.Cancel(); if (certified) boundary.SetResult(certificate!); else preparation.SetResult(false); });
        var run = new RunSession(Limits(), clock, condition, failure: certified ? Condition() : null); bool executed = false, released = false;
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        RunOutcome result;
        if (certified)
            result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (session, _, _) =>
            {
                var request = session.ArmRunningBoundary();
                certificate = request.Certify(request.RequestId, clock.Elapsed, new(condition.Elapsed, Unit("false"), Unit()), "live", true);
                return new(boundary.Task);
            }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, cancel.Token);
        else result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => new(preparation.Task),
            (_, _) => { executed = true; return ValueTask.FromResult(true); }, cancel.Token);
        Assert.False(executed); Assert.True(released); Assert.Equal(1, result.ExitCode);
        Assert.Equal(certified ? RunReason.FailureCondition : RunReason.ExecutionError, result.Primary.Cause.Reason);
        Assert.Contains(result.Events, x => x.Reason == RunReason.Cancelled);
    }
    private sealed class CallbackDelayClock(Action callback, int invokeAt = 1) : IClock
    {
        private int delays;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (++delays == invokeAt) callback();
            return new(new TaskCompletionSource().Task);
        }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LosingWakeFaultIsArbitratedWithReadyWork(bool actionFailed, bool internallyCancelled)
    {
        var condition = new Clock(); var work = new TaskCompletionSource<int>(); var wake = new TaskCompletionSource();
        var clock = new CallbackDelayClock(() => { work.SetResult(1); if (internallyCancelled) wake.SetCanceled(); else wake.SetException(new IOException("lost notification")); }, invokeAt: 2);
        var run = new RunSession(Limits(), clock, condition); run.BeginPreparation(); run.BeginRunning();
        var result = await RunMonitor.AwaitAsync(run, clock, condition, new LosingWakeFeed(wake.Task), _ => new ValueTask<int>(work.Task),
            _ => actionFailed ? [Event(RunReason.ActionFailed)] : []);
        Assert.False(result.Completed); Assert.Equal(actionFailed ? RunReason.ActionFailed : RunReason.ExecutionError, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.ExecutionError);
        Assert.Contains(run.Exceptions, x => x.Type == (internallyCancelled ? "System.Threading.Tasks.TaskCanceledException" : "System.IO.IOException"));
    }
    private sealed class LosingWakeFeed(Task wake) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit("false"), Unit("false")));
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(wake);
    }
    [Fact]
    public async Task AbsoluteDeadlineCannotBeRebasedAfterOwnerComputesRemainder()
    {
        var clock = new Clock(); clock.At(999); var deadline = TimeSpan.FromSeconds(1); var invoked = false;
        var previouslyComputedRemainder = deadline - clock.Elapsed; Assert.Equal(TimeSpan.FromMilliseconds(1), previouslyComputedRemainder);
        clock.At(1001);
        await Assert.ThrowsAsync<TimeoutException>(() => FiniteOperation.RunUntilAsync(clock, deadline, _ =>
        { invoked = true; return ValueTask.FromResult(true); }).AsTask());
        Assert.False(invoked);
    }
    [Theory]
    [InlineData(RunReason.Cancelled, false, RunReason.ActionUnconfirmed)]
    [InlineData(RunReason.Cancelled, true, RunReason.ActionUnconfirmed)]
    [InlineData(RunReason.InvalidContract, false, RunReason.InvalidContract)]
    [InlineData(RunReason.InvalidContract, true, RunReason.InvalidContract)]
    [InlineData(RunReason.ActionFailed, false, RunReason.ActionFailed)]
    [InlineData(RunReason.ActionFailed, true, RunReason.ActionFailed)]
    public void ClosingUnitArbitratesAbandonedSentOrUncertainActions(RunReason terminal, bool sent, RunReason expected)
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(2, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); if (sent) operation.Actions!.ConfirmSent(0);
        var result = run.Evaluate(Unit(), clock.Elapsed, terminal == RunReason.Cancelled ? [] : [Event(terminal)],
            cancelled: terminal == RunReason.Cancelled);
        Assert.Equal(expected, result!.Cause.Reason); Assert.True(run.GoalVerified); Assert.False(operation.IsOpen);
        Assert.Single(run.Events, x => x.Reason == RunReason.ActionUnconfirmed);
        Assert.Equal(new[] { sent ? DeliveryState.Sent : DeliveryState.Uncertain, DeliveryState.NotSent }, operation.Actions!.Deliveries);
        Assert.False(operation.ConfirmResult()); Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(1));
    }
    [Fact]
    public async Task PreCancelledMonitorCannotInvokeWorkOrTransport()
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var invoked = false;
        var result = await RunMonitor.AwaitAsync(run, clock, clock, new Feed(() => throw new InvalidOperationException("capture")), _ =>
        { invoked = true; operation.BeginDispatch(0); return ValueTask.FromResult(1); }, _ => [], cancel.Token);
        Assert.False(invoked); Assert.False(result.Completed); Assert.Equal(RunReason.Cancelled, run.Primary!.Cause.Reason);
        Assert.Equal(ExecutionState.Completing, run.State); Assert.Equal(0, run.Budget.Snapshot.Actions);
        Assert.Equal(DeliveryState.NotSent, operation.Actions!.Deliveries.Single());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectedWorkEventProjectionFailsClosedAndIsNotRetried(int invalid)
    {
        var clock = new Clock(); var run = Running(clock); var projections = 0;
        var result = await RunMonitor.AwaitAsync(run, clock, clock, new Feed(() => new(TimeSpan.Zero, Unit(), Unit())),
            _ => ValueTask.FromResult(1), _ =>
            { projections++; return invalid == 2 ? null! : [Event(invalid == 0 ? RunReason.GoalSatisfied : (RunReason)int.MaxValue)]; });
        Assert.False(result.Completed); Assert.Equal(1, projections); Assert.Equal(RunReason.ExecutionError, run.Primary!.Cause.Reason);
        Assert.Equal(10, run.Primary.ExitCode); Assert.Equal(ExecutionState.Completing, run.State);
        Assert.Single(run.Exceptions, x => x.Type == "System.ArgumentException");
    }
    [Fact]
    public void CertifiedStartCannotOutliveCurrentPreparationDeadline()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); run.BeginPreparation();
        clock.At(200); var boundary = Certify(run, clock, clock); clock.At(1000);
        Assert.Throws<TimeoutException>(() => run.BeginRunning(boundary));
        Assert.Equal(ExecutionState.Preparing, run.State); Assert.Null(run.RunningOrigin);
        Assert.Equal(RunReason.PreparationTimeout, run.Evaluate()!.Cause.Reason);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LiveCaptureRequiresObservationObjectAndBothMaps(int missing)
    {
        var clock = new Clock(); var run = Running(clock, failure: true);
        RunObservation observation = missing == 3 ? null! : new(TimeSpan.Zero,
            missing is 0 or 2 ? null! : Unit(), missing is 1 or 2 ? null! : Unit());
        var result = await RunMonitor.AwaitAsync(run, clock, clock, new Feed(() => observation),
            _ => ValueTask.FromResult(1), _ => [Event(RunReason.ActionFailed)]);
        Assert.False(result.Completed); Assert.Equal(RunReason.ObservationContractViolation, run.Primary!.Cause.Reason);
        Assert.Equal(ResultStatus.Invalid, run.Primary.Status); Assert.Contains(run.Events, x => x.Reason == RunReason.ActionFailed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCompletedCaptureCannotBypassNullMapValidation(bool cancelled)
    {
        var capture = new TaskCompletionSource<RunObservation>(); using var cancel = new CancellationTokenSource();
        var real = new FiniteRaceClock(() => { if (cancelled) cancel.Cancel(); capture.SetResult(new(TimeSpan.Zero, null!, null!)); });
        var condition = new Clock(); var run = new RunSession(Limits(), real, condition, failure: Condition());
        run.BeginPreparation(); run.BeginRunning(); var never = new TaskCompletionSource<int>();
        await RunMonitor.AwaitAsync(run, real, condition, new PendingCaptureFeed(capture.Task),
            _ => new ValueTask<int>(never.Task), _ => [], cancel.Token);
        Assert.Equal(RunReason.ObservationContractViolation, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.MaxDuration); never.SetResult(1);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPublicCandidateCannotConsumePendingUnconfirmedAction(bool fabricatedGoal)
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); operation.Complete();
        var candidate = Event(fabricatedGoal ? RunReason.GoalSatisfied : (RunReason)int.MaxValue);
        Assert.Throws<ArgumentException>(() => run.Evaluate(candidates: [candidate]));
        Assert.Null(run.Primary); Assert.Empty(run.Events);
        Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate(Unit(), clock.Elapsed)!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Fact]
    public async Task ThrowingWakeCancellationCallbackCannotEscapeCaller()
    {
        var clock = new Clock(); var run = Running(clock); using var cancel = new CancellationTokenSource();
        var feed = new CallbackWakeFeed(); var pending = new TaskCompletionSource<int>();
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(pending.Task), _ => [], cancel.Token).AsTask();
        Assert.Null(await Task.Run(() => Record.Exception(cancel.Cancel))); Assert.False((await monitoring).Completed);
        Assert.Equal(RunReason.Cancelled, run.Primary!.Cause.Reason);
        Assert.Contains(run.Exceptions, x => x.Type == "System.AggregateException"); pending.SetResult(1);
    }
    private sealed class CallbackWakeFeed : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
            => ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit("false"), Unit("false")));
        public ValueTask WaitForChangeAsync(CancellationToken token)
        { token.Register(() => throw new IOException("wake callback")); return new(new TaskCompletionSource().Task); }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletedCaptureRetainsFailureOrViolationDuringInterruption(bool cancelled, bool violation)
    {
        var capture = new TaskCompletionSource<RunObservation>(); using var cancel = new CancellationTokenSource();
        var real = new FiniteRaceClock(() =>
        {
            if (cancelled) cancel.Cancel();
            capture.SetResult(new(TimeSpan.Zero, Unit("false"), Unit(violation ? "1" : "true")));
        });
        var condition = new Clock(); var run = new RunSession(Limits(), real, condition, failure: Condition());
        run.BeginPreparation(); run.BeginRunning(); var never = new TaskCompletionSource<int>();
        var result = await RunMonitor.AwaitAsync(run, real, condition, new PendingCaptureFeed(capture.Task),
            _ => new ValueTask<int>(never.Task), _ => [], cancel.Token);
        Assert.False(result.Completed);
        Assert.Equal(violation ? RunReason.ObservationContractViolation : RunReason.FailureCondition, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.MaxDuration);
        if (cancelled) Assert.Contains(run.Events, x => x.Reason == RunReason.Cancelled);
        never.SetResult(1);
    }
    private sealed class PendingCaptureFeed(Task<RunObservation> capture) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => new(capture);
        public ValueTask WaitForChangeAsync(CancellationToken token) => throw new NotSupportedException();
    }
    [Fact]
    public async Task FalsePreparationRetainsCancellationThatArrivesDuringFinalization()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); using var cancel = new CancellationTokenSource();
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, token) =>
        { token.Register(cancel.Cancel); return ValueTask.FromResult(false); }, (_, _) => throw new InvalidOperationException(), cancel.Token);
        Assert.Equal(RunOrigin.Host, result.Primary.Cause.Origin); Assert.Equal(RunReason.ExecutionError, result.Primary.Cause.Reason);
        Assert.Contains(result.Events, x => x.Reason == RunReason.Cancelled);
    }
    [Fact]
    public async Task FaultCompletedDuringDeadlineReadIsNotReplacedByTimeout()
    {
        var work = new TaskCompletionSource<int>(); var clock = new FaultOnReadClock(() => work.SetException(new IOException("original")));
        await Assert.ThrowsAsync<IOException>(() => FiniteOperation.RunAsync(clock, TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(work.Task)).AsTask());
    }
    private sealed class FaultOnReadClock(Action fault) : IClock
    {
        private int reads;
        // Deadline creation and the pre-invocation absolute check precede provider work.
        public TimeSpan Elapsed { get { if (++reads == 3) fault(); return reads >= 3 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero; } }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => throw new NotSupportedException();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyActionFailureWinsCaptureOrWakeInfrastructureFault(bool wake)
    {
        var clock = new Clock(); var run = Running(clock); var work = new TaskCompletionSource<int>();
        if (!wake) work.SetResult(1);
        var result = await RunMonitor.AwaitAsync(run, clock, clock, new FaultFeed(wake, () => work.SetResult(1)),
            _ => new ValueTask<int>(work.Task), _ => [Event(RunReason.ActionFailed)]);
        Assert.False(result.Completed); Assert.Equal(RunReason.ActionFailed, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.ExecutionError);
        Assert.Contains(run.Exceptions, x => x.Type == "System.IO.IOException");
    }
    private sealed class FaultFeed(bool wake, Action ready) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => wake
            ? ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit("false"), Unit("false"))) : throw new IOException("capture");
        public ValueTask WaitForChangeAsync(CancellationToken token) { ready(); throw new IOException("wake"); }
    }
    [Fact]
    public void ResultConfirmationClosesDispatchWhileObservationRemainsOpen()
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(2, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0); Assert.True(operation.ConfirmResult());
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(1));
        clock.At(1500); Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(1));
        Assert.True(operation.IsOpen); operation.Complete(); Assert.Equal(1, run.Budget.Snapshot.Actions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedFaultWinsFiniteTimerOrCallerCancellation(bool cancelled)
    {
        var operation = new TaskCompletionSource<int>(); using var cancel = new CancellationTokenSource();
        var clock = new FiniteRaceClock(() =>
        {
            if (cancelled) cancel.Cancel();
            operation.SetException(new RunFailureException(Event(RunReason.ActionFailed)));
        });
        var exception = await Assert.ThrowsAsync<RunFailureException>(() => FiniteOperation.RunAsync(clock,
            TimeSpan.FromSeconds(1), _ => new ValueTask<int>(operation.Task), cancel.Token).AsTask());
        Assert.Equal(RunReason.ActionFailed, exception.Cause.Reason);
    }
    private sealed class FiniteRaceClock(Action finish) : IClock
    {
        public TimeSpan Elapsed { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        { Elapsed += duration; finish(); return ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task ExecutionRemainderAlreadyExpiredIsTimedOutWithoutDispatch()
    {
        var clock = new TickClock(); var condition = new Clock(); var normal = Limits();
        var limits = new RunLimits(TimeSpan.FromTicks(1), normal.PreparationTimeout, normal.CleanupTimeout,
            normal.PlannerTimeout, normal.WaitTimeout, normal.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, clock, condition); var executed = false;
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true),
            (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.False(executed); Assert.Equal(RunReason.MaxDuration, result.Primary.Cause.Reason); Assert.Equal(ResultStatus.TimedOut, result.Primary.Status);
    }
    private sealed class TickClock : IClock
    {
        private long tick;
        public TimeSpan Elapsed => TimeSpan.FromTicks(tick++);
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(new TaskCompletionSource().Task);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CertifiedBoundaryRequiresBothObservationMaps(bool successNull)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); run.BeginPreparation();
        var request = run.ArmRunningBoundary();
        Assert.Throws<ArgumentNullException>(() => request.Certify(request.RequestId, clock.Elapsed,
            new(clock.Elapsed, successNull ? null! : Unit(), successNull ? Unit() : null!), "sync", true));
        Assert.Equal(ExecutionState.Preparing, run.State); Assert.Null(run.RunningOrigin);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingAbandonedCancellationCallbackCannotReplaceFiniteOutcome(bool timeout)
    {
        var clock = new Clock(); var never = new TaskCompletionSource<bool>();
        var work = FiniteOperation.RunAsync(clock, TimeSpan.FromSeconds(1), token =>
        {
            token.Register(() => throw new IOException("callback"));
            return timeout ? new ValueTask<bool>(never.Task) : ValueTask.FromResult(true);
        }).AsTask();
        if (timeout) { clock.At(1000); await Assert.ThrowsAsync<TimeoutException>(() => work); never.SetResult(true); }
        else Assert.True(await work);
    }
    [Fact]
    public async Task ReadyFailureWinsCancellationDuringWake()
    {
        var clock = new Clock(); var run = Running(clock); using var cancel = new CancellationTokenSource();
        var work = new TaskCompletionSource<int>();
        var feed = new CancelWakeFeed(() => { cancel.Cancel(); work.SetResult(1); });
        var result = await RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task),
            _ => [Event(RunReason.ActionFailed)], cancel.Token);
        Assert.False(result.Completed); Assert.Equal(RunReason.ActionFailed, run.Primary!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.Cancelled);
    }
    private sealed class CancelWakeFeed(Action onWake) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
            => ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit("false"), Unit("false")));
        public ValueTask WaitForChangeAsync(CancellationToken token)
        { onWake(); return ValueTask.FromCanceled(token); }
    }
    [Fact]
    public async Task NullProductionBoundaryFailsClosedAndReleasesOwnedResource()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock); var cleanup = new OwnedCleanup();
        var released = false; var executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, cleanup, (_, owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            return ValueTask.FromResult<RunStartBoundary>(null!);
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.False(executed); Assert.True(released); Assert.Null(run.RunningOrigin);
        Assert.Equal(RunReason.ExecutionError, outcome.Primary.Cause.Reason); Assert.Equal(10, outcome.ExitCode);
        Assert.Equal(ExecutionState.Finished, run.State);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RefundedFinalReservationRetainsClosingReasonAndFinalGoalOpportunity(bool cancelUnsent, bool goal)
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        if (cancelUnsent) operation.Actions!.CancelUnsent(); else operation.Actions!.ConfirmNotSent(0);
        Assert.Null(run.Evaluate(Unit("false"), clock.Elapsed));
        operation.Complete(); Assert.Equal(0, run.Budget.Snapshot.Actions); Assert.Equal(0, run.Budget.Snapshot.ReservedActions);
        Assert.Null(run.RequestPlanner()); Assert.Null(run.ApproveOperation(0, TimeSpan.FromSeconds(1)));
        Assert.Equal(goal ? RunReason.GoalSatisfied : RunReason.ActionsExhausted,
            run.Evaluate(Unit(goal ? "true" : "false"), clock.Elapsed)!.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == RunReason.ActionsExhausted);
    }
    [Fact]
    public void PlannerCompletionAtDeadlineCannotAuthorizeTransport()
    {
        var clock = new ReadSequenceClock(); var run = new RunSession(Limits(decisions: 1), clock, clock);
        run.BeginPreparation(); run.BeginRunning(); var permit = run.RequestPlanner()!;
        clock.Reads.Enqueue(TimeSpan.FromMilliseconds(999)); clock.Reads.Enqueue(TimeSpan.FromMilliseconds(1000));
        clock.Reads.Enqueue(TimeSpan.FromMilliseconds(1001));
        Assert.Null(permit.Approve(1, TimeSpan.FromSeconds(1))); Assert.Equal(0, run.Budget.Snapshot.ReservedActions);
        Assert.Equal(RunReason.PlannerTimeout, run.Evaluate()!.Cause.Reason); Assert.Equal(ExecutionState.Completing, run.State);
    }
    private sealed class ReadSequenceClock : IClock
    {
        public Queue<TimeSpan> Reads { get; } = new();
        private TimeSpan current;
        public TimeSpan Elapsed => Reads.Count == 0 ? current : current = Reads.Dequeue();
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => throw new NotSupportedException();
    }
    [Fact]
    public async Task SynchronousMonitorWorkFaultIsArbitratedAndPreserved()
    {
        var clock = new Clock(); var run = Running(clock); var feed = new Feed(() => new(TimeSpan.Zero, Unit(), Unit()));
        var result = await RunMonitor.AwaitAsync<int>(run, clock, clock, feed, _ => throw new IOException("private"), _ => []);
        Assert.False(result.Completed); Assert.Equal(RunReason.ExecutionError, run.Primary!.Cause.Reason);
        Assert.Equal("System.IO.IOException", run.Exceptions.Single().Type); Assert.Equal(ExecutionState.Completing, run.State);
    }
    [Theory]
    [InlineData(true, RunReason.PlannerTimeout)]
    [InlineData(false, RunReason.WaitExpired)]
    public void OverdueCompletionCannotRemoveDeadlineEvidence(bool planner, RunReason expected)
    {
        var clock = new Clock(); var run = Running(clock);
        var permit = planner ? run.RequestPlanner() : null;
        var wait = planner ? null : run.ApproveOperation(0, TimeSpan.FromSeconds(1));
        clock.At(1000);
        if (planner) permit!.CompleteWithoutOperation(); else wait!.Complete();
        Assert.Equal(expected, run.Evaluate(Unit(), clock.Elapsed)!.Cause.Reason);
    }
    [Fact]
    public void OversizedEvidenceFailsClosedAndRetainsPendingActionFailure()
    {
        var clock = new Clock(); var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 3, 3, 2, 32);
        var run = Running(clock, limits: limits); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); operation.Complete();
        run.Evaluate(candidates: Enumerable.Repeat(Event(RunReason.ActionsExhausted), 100));
        Assert.Equal(RunReason.ActionUnconfirmed, run.Primary!.Cause.Reason); Assert.Equal(ExecutionState.Completing, run.State);
        Assert.InRange(run.Events.Count, 1, 32); Assert.Contains(run.Events, x => x.Reason == RunReason.ExecutionError);
    }
    [Fact]
    public async Task ReadyFailureWinsCancellationDuringBlockedCapture()
    {
        var clock = new Clock(); var run = Running(clock); using var cancel = new CancellationTokenSource();
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, new BlockedFeed(), _ => ValueTask.FromResult(1),
            _ => [Event(RunReason.ActionFailed)], cancel.Token).AsTask();
        cancel.Cancel(); Assert.False((await monitoring).Completed);
        Assert.Equal(RunReason.ActionFailed, run.Primary!.Cause.Reason); Assert.Contains(run.Events, x => x.Reason == RunReason.Cancelled);
    }
    [Fact]
    public async Task InternalCleanupCancellationIsFaultWithOriginalTypeAndStack()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.InputRelease, _ => throw new OperationCanceledException("provider internal"));
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(11, outcome.ExitCode); Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
        var issue = Assert.Single(outcome.PostProcessing);
        Assert.Equal(PostProcessingReason.InputReleaseUnconfirmed, issue.Reason);
        Assert.Equal("System.OperationCanceledException", issue.Exception!.Type); Assert.NotNull(issue.Exception.StackTrace);
    }
    [Fact]
    public async Task ConcurrentRegistrationIsEitherIncludedOrReliablyRejected()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var released = false;
            var registration = Task.Run(async () =>
            {
                await start.Task;
                try { cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); }); return true; }
                catch (InvalidOperationException exception) { Assert.Equal("CleanupRegistrationClosed", exception.Message); return false; }
            });
            start.SetResult(); var outcome = await cleanup.CompleteAsync(run, clock); var accepted = await registration;
            Assert.Equal(accepted, released); Assert.True(outcome.PostProcessingComplete);
        }
    }
    [Fact]
    public async Task SynchronousProviderSetupDoesNotResetItsSafetyDeadline()
    {
        var clock = new Clock(); var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = FiniteOperation.RunAsync(clock, TimeSpan.FromSeconds(1), _ =>
        { clock.At(900); return new ValueTask<bool>(never.Task); }).AsTask();
        clock.At(1000); await Assert.ThrowsAsync<TimeoutException>(() => operation); never.SetResult(true);
    }
    private static RunStartBoundary Certify(RunSession run, Clock real, Clock condition,
        RunObservation? observation = null)
    {
        var request = run.ArmRunningBoundary();
        return request.Certify(request.RequestId, real.Elapsed, observation ?? new(condition.Elapsed, Unit(), Unit("false")), "subscription/epoch/cursor", true);
    }
    [Fact]
    public void CertifiedBoundaryRejectsPreparationHistoryWrongRequestAndUnreadyPrerequisites()
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        run.BeginPreparation(); real.At(200); condition.At(200); var request = run.ArmRunningBoundary();
        Assert.Throws<InvalidOperationException>(() => request.Certify(request.RequestId, TimeSpan.Zero,
            new(TimeSpan.Zero, Unit(), Unit()), "subscription", true));
        Assert.Throws<InvalidOperationException>(() => request.Certify("other-request", real.Elapsed,
            new(condition.Elapsed, Unit(), Unit()), "subscription", true));
        Assert.Throws<InvalidOperationException>(() => request.Certify(request.RequestId, real.Elapsed,
            new(condition.Elapsed, Unit(), Unit()), "subscription", false));
        Assert.Throws<InvalidOperationException>(() => run.BeginRunning());
    }
    [Fact]
    public void CertifiedBoundaryCannotCrossSessionsOrClockRegression()
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        run.BeginPreparation(); real.At(100); condition.At(100); var boundary = Certify(run, real, condition);
        var other = new RunSession(Limits(), real, condition); other.BeginPreparation();
        Assert.Throws<InvalidOperationException>(() => other.BeginRunning(boundary));
        condition.At(50); Assert.Throws<InvalidOperationException>(() => run.BeginRunning(boundary));
    }
    [Fact]
    public void CertifiedBoundaryKeepsOriginalRealBudgetAndExactWithinZeroCapture()
    {
        var real = new Clock(); var condition = new Clock();
        var node = JsonNode.Parse("""{"kind":"time","withinMilliseconds":0,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject();
        var goal = PreparedCondition.Create(node, new(10, 1000)); var defaults = Limits();
        // Isolate delivery lag against maxDuration while keeping preparation itself inside its explicit deadline.
        var limits = new RunLimits(defaults.MaxDuration, TimeSpan.FromSeconds(10), defaults.CleanupTimeout,
            defaults.PlannerTimeout, defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, real, condition, goal);
        run.BeginPreparation(); real.At(200); condition.At(200);
        var boundary = Certify(run, real, condition, new(condition.Elapsed, Unit(time: true), Unit("false")));
        real.At(5200); condition.At(5200); run.BeginRunning(boundary);
        Assert.Equal(TimeSpan.FromMilliseconds(200), run.RunningOrigin);
        Assert.Equal(ResultStatus.TimedOut, run.Evaluate(boundary.InitialObservation.Success, boundary.InitialObservation.CapturedAt)!.Status);
        Assert.True(run.GoalVerified); Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Fact]
    public void PreparationTimeCannotContributeToCertifiedForHold()
    {
        var real = new Clock(); var condition = new Clock();
        var node = JsonNode.Parse("""{"kind":"time","forMilliseconds":200,"withinMilliseconds":0,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject();
        var goal = PreparedCondition.Create(node, new(10, 1000)); var run = new RunSession(Limits(), real, condition, goal);
        run.BeginPreparation(); real.At(500); condition.At(500);
        var boundary = Certify(run, real, condition, new(condition.Elapsed, Unit(time: true), Unit("false")));
        condition.At(600); real.At(600); run.BeginRunning(boundary);
        Assert.Null(run.Evaluate(boundary.InitialObservation.Success, boundary.InitialObservation.CapturedAt)); Assert.False(run.GoalVerified);
        var continuous = new ConditionObservationUnit([KeyValuePair.Create("$/condition", new ConditionLeafObservation("scope", true,
            [new ConditionTargetObservation("target", "{\"type\":\"bool\",\"value\":true}")], continuousFromPrevious: true))]);
        condition.At(699); real.At(699); Assert.Null(run.Evaluate(continuous, condition.Elapsed));
        condition.At(700); real.At(700); Assert.Equal(ResultStatus.Passed, run.Evaluate(continuous, condition.Elapsed)!.Status);
    }
    [Fact]
    public async Task BoundaryExecutorEvaluatesInitialFailureBeforeAnyPlannerWork()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, Condition(), Condition()); var executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (session, _, _) =>
            ValueTask.FromResult(Certify(session, clock, clock, new(clock.Elapsed, Unit(), Unit()))), (_, _) =>
            { executed = true; return ValueTask.FromResult(true); });
        Assert.False(executed); Assert.Equal(RunReason.FailureCondition, outcome.Primary.Cause.Reason); Assert.Equal(ExecutionState.Finished, run.State);
    }
    [Fact]
    public async Task PrimarySnapshotRunsUnderCancelBeforeReleaseAndFailurePreservesPrimary()
    {
        var clock = new Clock(); var run = Running(clock); var primary = run.Evaluate(Unit(), TimeSpan.Zero)!;
        var cleanup = new OwnedCleanup(); var order = new List<string>(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        cleanup.Register(CleanupStage.ResourceRelease, token => { Assert.False(token.IsCancellationRequested); order.Add("release"); return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, clock, cancel.Token, (snapshot, token) =>
        { Assert.Same(primary, snapshot.Primary); Assert.False(token.IsCancellationRequested); order.Add("primary"); return ValueTask.FromResult(false); });
        Assert.Equal(new[] { "primary", "release" }, order); Assert.Same(primary, outcome.Primary); Assert.Equal(11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.PrimarySnapshotFailed);
    }
    [Fact]
    public async Task NoncooperativePrimarySnapshotIsBoundedAndLaterReleaseStillRuns()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(candidates: [Event(RunReason.ActionFailed)]);
        var cleanup = new OwnedCleanup(); var released = false; var never = new TaskCompletionSource<bool>();
        cleanup.Register(CleanupStage.InputRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var finishing = cleanup.CompleteAsync(run, clock, confirmPrimary: (_, _) => new(never.Task)).AsTask();
        clock.At(500); var outcome = await finishing; Assert.True(released); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.PrimarySnapshotFailed); never.SetResult(true);
    }
    [Theory]
    [InlineData(RunReason.PlannerOutputInvalid)]
    [InlineData(RunReason.PlannerUsageLimit)]
    [InlineData(RunReason.PlannerConnectionFailure)]
    public void NoncontinuablePlannerReasonsAreFailuresAndOutrankCancelDeadlineAndSuccess(RunReason reason)
    {
        var clock = new Clock(); var run = Running(clock); clock.At(5000);
        var result = run.Evaluate(Unit(), TimeSpan.Zero, [new(reason, RunPhase.Execution, RunOrigin.Planner)], cancelled: true);
        Assert.Equal(ResultStatus.Failed, result!.Status); Assert.Equal(reason, result.Cause.Reason); Assert.Equal(1, result.ExitCode);
    }
    [Fact]
    public async Task PostConfirmationCancellationAloneDoesNotInventIncompleteMandatoryWork()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await new OwnedCleanup().CompleteAsync(run, clock, cancel.Token);
        Assert.Contains(result.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
        Assert.True(result.PostProcessingComplete); Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task TypedHostPreparationFailurePreservesClassificationAndOriginalEvidence()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) =>
        {
            try { throw new IOException("private host detail"); }
            catch (IOException exception) { throw new RunFailureException(new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host), exception); }
        }, (_, _) => throw new InvalidOperationException("execute must not run"));
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Equal(1, outcome.ExitCode); Assert.Contains(outcome.Exceptions, x => x.Type == "System.IO.IOException" && x.StackTrace is not null);
    }
}
