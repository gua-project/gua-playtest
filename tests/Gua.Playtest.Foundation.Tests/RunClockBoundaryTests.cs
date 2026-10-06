using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Conditions;
using System.Text.Json.Nodes;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task MissingStartupObservationMapsRetainContractCauseAndOwnedRelease(int missing)
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, Condition());
        bool released = false, executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (session, owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            var request = session.ArmRunningBoundary();
            var observation = missing == 0 ? null : new RunObservation(clock.Elapsed,
                missing == 1 ? null! : Unit(), missing == 2 ? null! : Unit("false"));
            return ValueTask.FromResult(request.Certify(request.RequestId, clock.Elapsed, observation!, "fresh/source", true));
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.True(released); Assert.False(executed); Assert.False(run.GoalVerified);
        Assert.Equal(new RunEvent(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract), outcome.Primary.Cause);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Exceptions, x => x.Type == "System.ArgumentNullException");
    }
    private sealed class SerializedCaptureFeed(bool ignoreCancellation, bool failure) : IRunObservationFeed
    {
        public int Captures; public bool Ended;
        public TaskCompletionSource<RunObservation> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        {
            if (++Captures == 1) return new(InitialAsync(token));
            if (!Ended) throw new InvalidOperationException("OverlappingSourceCapture");
            return ValueTask.FromResult(new RunObservation(TimeSpan.Zero, Unit(), Unit(failure ? "true" : "false")));
        }
        private async Task<RunObservation> InitialAsync(CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            try { return ignoreCancellation ? await First.Task : await First.Task.WaitAsync(token); }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token)
            {
                // WaitAsync's cancellation continuation may dispose our older registration
                // before that callback runs. The observed requested cancellation is definitive.
                Cancelled.TrySetResult();
                throw;
            }
            finally { Ended = true; Joined.TrySetResult(); }
        }
        public ValueTask WaitForChangeAsync(CancellationToken token) => throw new InvalidOperationException("unexpected wait");
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task SerializedSupersededCaptureEndsBeforeFreshPostWorkCapture(bool ignoreCancellation, bool failure)
    {
        var clock = new Clock(); var run = Running(clock, failure: true); var feed = new SerializedCaptureFeed(ignoreCancellation, failure);
        var work = new TaskCompletionSource<int>();
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => []).AsTask();
        await feed.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        work.SetResult(1); await feed.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (ignoreCancellation)
        {
            Assert.Equal(1, feed.Captures); Assert.False(monitoring.IsCompleted);
            feed.First.SetResult(new(TimeSpan.Zero, Unit("false"), Unit("false")));
        }
        await monitoring.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(feed.Ended); Assert.Equal(2, feed.Captures); Assert.True(run.GoalVerified);
        Assert.Equal(failure ? RunReason.FailureCondition : RunReason.GoalSatisfied, run.Primary!.Cause.Reason);
        Assert.DoesNotContain(run.Events, x => x.Reason == RunReason.ExecutionError);
    }
    [Fact]
    public async Task NeverEndingSerializedCaptureStopsAtExistingDeadlineWithoutOverlap()
    {
        var clock = new Clock(); var run = Running(clock); var feed = new SerializedCaptureFeed(true, false);
        var work = new TaskCompletionSource<int>();
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, feed, _ => new ValueTask<int>(work.Task), _ => []).AsTask();
        await feed.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        work.SetResult(1); await feed.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); clock.At(5000);
        await monitoring.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, feed.Captures); Assert.False(feed.Ended); Assert.Equal(RunReason.MaxDuration, run.Primary!.Cause.Reason);
        var primary = run.Primary; feed.First.SetResult(new(TimeSpan.Zero, Unit(), Unit())); await feed.Joined.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(primary, run.Primary); Assert.False(run.GoalVerified);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task RejectedStartupTimestampRetainsObservationContractCauseAndRelease(int kind)
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition, Condition());
        bool released = false, executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (session, owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            real.At(100); condition.At(100); var request = session.ArmRunningBoundary();
            var realAt = TimeSpan.FromMilliseconds(kind == 0 ? 50 : kind == 2 ? 101 : 100);
            var conditionAt = TimeSpan.FromMilliseconds(kind == 1 ? 50 : kind == 3 ? 101 : 100);
            return ValueTask.FromResult(request.Certify(request.RequestId, realAt, new(conditionAt, Unit(), Unit("false")), "fresh/source", true));
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); });
        Assert.True(released); Assert.False(executed); Assert.False(run.GoalVerified);
        Assert.Equal(new RunEvent(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract), outcome.Primary.Cause);
        Assert.Equal(2, outcome.ExitCode); Assert.Contains(outcome.Exceptions, x => x.Type == "System.InvalidOperationException");
    }
    private sealed class CountingClockFeed(Func<TimeSpan> current, bool hold) : IRunObservationFeed
    {
        public int Captures;
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        {
            Captures++;
            var unit = hold ? new ConditionObservationUnit([KeyValuePair.Create("$/condition", new ConditionLeafObservation("scope", true,
                [new ConditionTargetObservation("target", "{\"type\":\"bool\",\"value\":true}")], continuousFromPrevious: Captures > 1))]) : Unit("false");
            return ValueTask.FromResult(new RunObservation(current(), unit, Unit("false")));
        }
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(new TaskCompletionSource().Task.WaitAsync(token));
    }
    private static PreparedCondition ShortHold()
        => PreparedCondition.Create(JsonNode.Parse("""{"kind":"time","forMilliseconds":50,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject(), new(10, 1000));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AdvancingCleanupClockIsNotRejectedAtPhysicalBudgetExpiry(bool failedPrimary)
    {
        var clock = new Gua.Playtest.Runner.MonotonicClock(); var defaults = Limits();
        var limits = new RunLimits(defaults.MaxDuration, defaults.PreparationTimeout, TimeSpan.FromMilliseconds(20), defaults.PlannerTimeout,
            defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, clock, clock, Condition()); run.BeginPreparation(); run.BeginRunning();
        var primary = failedPrimary ? run.Evaluate(candidates: [Event(RunReason.ActionFailed)])! : run.Evaluate(Unit(), clock.Elapsed)!;
        var cleanup = new OwnedCleanup(); bool released = false;
        cleanup.Register(CleanupStage.Diagnostics, _ =>
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(25)) Thread.SpinWait(100);
            return ValueTask.FromResult(true);
        });
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.True(released); Assert.Same(primary, outcome.Primary); Assert.Equal(failedPrimary ? 1 : 11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupTimeout);
        Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MonitorEarlyRealOrConditionWakeDoesNotSpinCaptures(bool conditionFailure)
    {
        var stalled = new EarlyWakeClock(); var advancing = new Gua.Playtest.Runner.MonotonicClock();
        IClock real = conditionFailure ? advancing : stalled; IClock condition = conditionFailure ? stalled : advancing;
        var defaults = Limits(); var limits = new RunLimits(TimeSpan.FromMilliseconds(conditionFailure ? 500 : 100), defaults.PreparationTimeout,
            defaults.CleanupTimeout, defaults.PlannerTimeout, defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, real, condition, conditionFailure ? ShortHold() : Condition()); run.BeginPreparation(); run.BeginRunning();
        var feed = new CountingClockFeed(() => condition.Elapsed, conditionFailure); var pending = new TaskCompletionSource<int>();
        var result = await RunMonitor.AwaitAsync(run, real, condition, feed, _ => new ValueTask<int>(pending.Task), _ => []).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Completed); Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), run.Primary!.Cause);
        Assert.Equal(1, feed.Captures); pending.SetResult(1);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MonitorReschedulesEarlyAdvancingConditionWakeWithoutFalseFailure(bool sharedTimer)
    {
        var condition = new AdvancingEarlyWakeClock(); IClock real = sharedTimer ? condition : new Gua.Playtest.Runner.MonotonicClock();
        var run = new RunSession(Limits(), real, condition, ShortHold()); run.BeginPreparation(); run.BeginRunning();
        var feed = new CountingClockFeed(() => condition.Elapsed, true); var pending = new TaskCompletionSource<int>();
        var result = await RunMonitor.AwaitAsync(run, real, condition, feed, _ => new ValueTask<int>(pending.Task), _ => []).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Completed); Assert.Equal(RunReason.GoalSatisfied, run.Primary!.Cause.Reason);
        Assert.DoesNotContain(run.Events, x => x.Origin == RunOrigin.Clock && x.Reason == RunReason.InvalidContract);
        Assert.Equal(2, feed.Captures); pending.SetResult(1);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PromptSynchronousCleanupCallbacksShareIndependentTotalBudget(bool failedPrimary)
    {
        var clock = new Clock(); var defaults = Limits();
        var limits = new RunLimits(defaults.MaxDuration, defaults.PreparationTimeout, TimeSpan.FromMilliseconds(40), defaults.PlannerTimeout,
            defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = Running(clock, limits: limits);
        var primary = failedPrimary ? run.Evaluate(candidates: [Event(RunReason.ActionFailed)])! : run.Evaluate(Unit(), TimeSpan.Zero)!;
        var cleanup = new OwnedCleanup(); int attempts = 0; bool released = false;
        for (var i = 0; i < 20; i++) cleanup.Register(CleanupStage.Diagnostics, _ =>
        {
            attempts++; var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(5)) Thread.SpinWait(100);
            return ValueTask.FromResult(true);
        });
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.True(attempts < 20); Assert.True(released); Assert.Same(primary, outcome.Primary);
        Assert.Equal(failedPrimary ? 1 : 11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupTimeout);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DispatchRetryCannotOutrunPendingTerminalClockEvidence(int kind)
    {
        var clock = new LaunchReadClock { Now = TimeSpan.FromMilliseconds(100) };
        var run = new RunSession(Limits(), clock, new Clock()); run.BeginPreparation(); run.BeginRunning();
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        clock.Now = kind switch { 0 => TimeSpan.FromTicks(-1), 1 => TimeSpan.FromMilliseconds(50), _ => TimeSpan.MaxValue };
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
        clock.Now = TimeSpan.FromMilliseconds(100);
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
        Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]); Assert.Equal(0, run.Budget.Snapshot.Actions);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), run.Evaluate()!.Cause);
        Assert.Equal(DeliveryState.NotSent, operation.Actions.Deliveries[0]); Assert.Equal(2, run.Primary!.ExitCode);
    }
    private sealed class ReadFaultClock : IClock
    {
        public bool Broken;
        public TimeSpan Elapsed => Broken ? throw new IOException("clock unavailable") : TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PersistentClockReadFaultStillFreezesPrimaryAndReleasesOwnedResources(bool execution)
    {
        var clock = new ReadFaultClock(); var run = new RunSession(Limits(), clock, new Clock());
        bool released = false; var cleanup = new OwnedCleanup();
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            clock.Broken = !execution; return ValueTask.FromResult(true);
        }, (_, _) => { clock.Broken = true; return ValueTask.FromResult(true); });
        Assert.True(released); Assert.Equal(ExecutionState.Finished, run.State);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, execution ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock), result.Primary.Cause);
        Assert.Equal(2, result.ExitCode); Assert.Contains(result.Exceptions, x => x.Type == "System.IO.IOException");
        Assert.Contains(result.PostProcessing, x => x.Reason == PostProcessingReason.CleanupClockInvalid);
        Assert.DoesNotContain(result.PostProcessing, x => x.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void ConditionInternalReadCannotHideTransientClockRejection(bool high, bool failure)
    {
        var condition = new LaunchReadClock { Now = TimeSpan.FromMilliseconds(100) };
        var run = new RunSession(Limits(), new Clock(), condition, failure ? null : Condition(), failure ? Condition() : null);
        run.BeginPreparation(); run.BeginRunning();
        condition.OnRead = () => condition.OnRead = () =>
        {
            condition.Now = high ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(50);
            condition.OnRead = () => condition.Now = TimeSpan.FromMilliseconds(100);
        };
        var primary = run.Evaluate(Unit(), TimeSpan.FromMilliseconds(100), failureUnit: Unit())!;
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), primary.Cause);
        Assert.False(run.GoalVerified); Assert.DoesNotContain(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PlannerApprovalFinalReadChecksItsSpecificDeadlineBeforeReservation(bool confirmed)
    {
        var clock = new LaunchReadClock(); var run = new RunSession(Limits(), clock, new Clock());
        run.BeginPreparation(); run.BeginRunning(); var permit = run.RequestPlanner()!;
        if (confirmed) { clock.Now = TimeSpan.FromMilliseconds(900); Assert.True(permit.ConfirmResponse()); }
        var deadline = confirmed ? 2900 : 1000;
        clock.Now = TimeSpan.FromMilliseconds(deadline - 1);
        clock.OnRead = () => clock.OnRead = () => clock.OnRead = () => clock.Now = TimeSpan.FromMilliseconds(deadline);
        Assert.Null(permit.Approve(1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, run.Budget.Snapshot.ReservedActions); Assert.Equal(0, run.Budget.Snapshot.ActionDecisions);
        Assert.Equal(confirmed ? RunReason.WaitExpired : RunReason.PlannerTimeout, run.Evaluate()!.Cause.Reason);
    }
    private sealed class EarlyWakeClock : IClock
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) { Called.TrySetResult(); return ValueTask.CompletedTask; }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PrematureTimerWakeIsClockFailureAndStillCleansUp(bool execution)
    {
        var clock = new EarlyWakeClock(); var defaults = Limits();
        var limits = new RunLimits(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), defaults.CleanupTimeout,
            defaults.PlannerTimeout, defaults.WaitTimeout, defaults.ActionTimeout, 3, 3, 2, 1024);
        var run = new RunSession(limits, clock, new Clock());
        bool released = false; var cleanup = new OwnedCleanup(); var pending = new TaskCompletionSource<bool>();
        var result = await RunExecutor.ExecuteAsync(run, clock, cleanup, (owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            return execution ? ValueTask.FromResult(true) : new ValueTask<bool>(pending.Task);
        }, (_, _) => new(pending.Task));
        Assert.True(released); Assert.Equal(2, result.ExitCode); Assert.Equal(ExecutionState.Finished, run.State);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, execution ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock), result.Primary.Cause);
        Assert.DoesNotContain(result.Events, x => x.Reason is RunReason.PreparationTimeout or RunReason.MaxDuration);
        pending.SetResult(true);
    }
    [Fact]
    public async Task CleanupPrematureProviderWakeKeepsCooperativeReleaseShareAlive()
    {
        var clock = new EarlyWakeClock(); var run = new RunSession(Limits(), clock, clock, Condition());
        run.BeginPreparation(); run.BeginRunning(); run.Evaluate(Unit(), TimeSpan.Zero);
        var release = new TaskCompletionSource<bool>(); bool cancelledEarly = false; var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.ResourceRelease, token =>
        {
            token.Register(() => { if (!release.Task.IsCompletedSuccessfully) cancelledEarly = true; });
            return new(release.Task);
        });
        var finishing = cleanup.CompleteAsync(run, clock).AsTask(); await clock.Called.Task;
        Assert.False(finishing.IsCompleted); release.SetResult(true); var result = await finishing;
        Assert.False(cancelledEarly); Assert.DoesNotContain(result.PostProcessing, x => x.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task AuthoritativeDelayFaultRetainsClockCauseAndOriginalException(bool execution, bool asynchronous)
    {
        var clock = new FaultingDelayClock(asynchronous) { Enabled = !execution }; var run = new RunSession(Limits(), clock, new Clock());
        bool released = false; var pending = new TaskCompletionSource<bool>();
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (owned, _) =>
        {
            owned.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); });
            return execution ? ValueTask.FromResult(true) : new ValueTask<bool>(pending.Task);
        }, (_, _) => { clock.Enabled = true; return new ValueTask<bool>(pending.Task); });
        Assert.True(released); Assert.Equal(2, outcome.ExitCode);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, execution ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock), outcome.Primary.Cause);
        Assert.Contains(outcome.Exceptions, x => x.Type == "System.IO.IOException"); pending.SetResult(true);
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task MonitorRealAndConditionDelayFaultsRetainClockCause(bool conditionFailure, bool asynchronous)
    {
        var broken = new FaultingDelayClock(asynchronous); var valid = new Clock();
        IClock real = conditionFailure ? valid : broken; IClock condition = conditionFailure ? broken : valid;
        var run = new RunSession(Limits(), real, condition, Condition(time: true)); run.BeginPreparation(); run.BeginRunning();
        var feed = new Feed(() => new(TimeSpan.Zero, Unit("false", time: true), Unit("false")));
        var pending = new TaskCompletionSource<int>();
        var result = await RunMonitor.AwaitAsync(run, real, condition, feed, _ => new ValueTask<int>(pending.Task), _ => []);
        Assert.False(result.Completed); Assert.Equal(2, run.Primary!.ExitCode);
        Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), run.Primary.Cause);
        Assert.Contains(run.Exceptions, x => x.Type == "System.IO.IOException"); pending.SetResult(1);
    }
    private sealed class AdvancingEarlyWakeClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Elapsed => elapsed.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) { Called.TrySetResult(); return ValueTask.CompletedTask; }
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExpiredCleanupStillAttemptsOwnedReleaseWithoutNewWait(bool input, bool pending)
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero);
        var cleanup = new OwnedCleanup(); var completion = new TaskCompletionSource<bool>(); bool invoked = false, cancelled = false;
        var stage = input ? CleanupStage.InputRelease : CleanupStage.ResourceRelease;
        cleanup.Register(CleanupStage.Diagnostics, _ => { clock.At(1000); return ValueTask.FromResult(true); });
        cleanup.Register(stage, token =>
        {
            Assert.False(token.IsCancellationRequested); invoked = true;
            token.Register(() => cancelled = true);
            return pending ? new ValueTask<bool>(completion.Task) : ValueTask.FromResult(true);
        });
        var finishing = cleanup.CompleteAsync(run, clock).AsTask();
        Assert.True(finishing.IsCompletedSuccessfully); var outcome = await finishing;
        Assert.True(invoked); Assert.True(cancelled); Assert.Equal(11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.CleanupTimeout);
        Assert.Equal(pending, outcome.PostProcessing.Any(x => x.Reason == (input ? PostProcessingReason.InputReleaseUnconfirmed : PostProcessingReason.ResourceReleaseUnconfirmed)));
        completion.SetResult(true); Assert.Equal(11, outcome.ExitCode);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task EarlyNativeStyleWakeReschedulesAndKeepsWorkAndCancellationResponsive(int completion)
    {
        var clock = new AdvancingEarlyWakeClock(); var pending = new TaskCompletionSource<int>(); using var cancel = new CancellationTokenSource();
        var deadline = clock.Elapsed + TimeSpan.FromMilliseconds(50);
        var waiting = FiniteOperation.RunUntilAsync(clock, deadline, _ => new ValueTask<int>(pending.Task), cancel.Token).AsTask();
        await clock.Called.Task; Assert.False(waiting.IsCompleted);
        if (completion == 0)
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
            Assert.Equal("OperationDeadlineReached", exception.Message); Assert.True(clock.Elapsed >= deadline);
        }
        else if (completion == 1) { pending.SetResult(7); Assert.Equal(7, await waiting); }
        else { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); }
        if (!pending.Task.IsCompleted) pending.SetResult(1);
    }
}
