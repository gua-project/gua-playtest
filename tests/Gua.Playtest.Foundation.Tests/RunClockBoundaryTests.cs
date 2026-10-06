using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
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
        var clock = new EarlyWakeClock(); var run = new RunSession(Limits(), clock, new Clock());
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
}
