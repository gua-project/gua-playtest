using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoalBeforeDispatchedReceiptRetainsFactAndFinalActionBudget(bool receiptArrives)
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero)); Assert.True(run.GoalVerified); Assert.True(operation.IsOpen);
        if (receiptArrives)
        {
            clock.At(500); Assert.True(operation.ConfirmResult()); operation.Complete();
            Assert.Equal(RunReason.GoalSatisfied, run.Evaluate()!.Cause.Reason);
        }
        else
        {
            clock.At(1000); Assert.False(operation.ConfirmResult());
            Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate()!.Cause.Reason);
        }
        Assert.Equal(1, run.Budget.Snapshot.Actions); Assert.True(run.GoalVerified);
    }
    [Theory]
    [InlineData(RunReason.Cancelled)]
    [InlineData(RunReason.FailureCondition)]
    [InlineData(RunReason.MaxDuration)]
    public void PendingGoalReceiptNeverSuppressesHigherPriorityTerminalEvidence(RunReason reason)
    {
        var clock = new Clock(); var run = Running(clock); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero));
        var primary = run.Evaluate(candidates: [Event(reason)])!;
        // Abandoning a sent operation on cancellation/deadline still retains higher-priority uncertainty.
        Assert.Equal(reason == RunReason.FailureCondition ? reason : RunReason.ActionUnconfirmed, primary.Cause.Reason);
        Assert.Contains(run.Events, x => x.Reason == reason); Assert.Contains(run.Events, x => x.Reason == RunReason.ActionUnconfirmed);
        Assert.False(operation.IsOpen); Assert.False(operation.ConfirmResult());
    }
    [Fact]
    public void ElapsedWaitCannotConfirmEarlyAndHasFinitePostElapsedObservation()
    {
        var clock = new Clock(); var run = Running(clock, success: false);
        var wait = run.RequestPlanner()!.ApproveElapsedWait(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))!;
        Assert.Equal(TimeSpan.FromMilliseconds(2500), wait.Deadline);
        clock.At(499); Assert.False(wait.ConfirmResult()); Assert.Null(run.Evaluate());
        clock.At(500); Assert.True(wait.ConfirmResult()); Assert.Null(run.Evaluate());
        clock.At(2499); Assert.Null(run.Evaluate());
        clock.At(2500); Assert.Equal(RunReason.WaitExpired, run.Evaluate()!.Cause.Reason);
    }
    [Fact]
    public void GoalDoesNotShortenLegitimateElapsedWaitAndTimeoutNeverRebasesRun()
    {
        var clock = new Clock(); var run = Running(clock);
        var wait = run.RequestPlanner()!.ApproveElapsedWait(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))!;
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero)); Assert.True(run.GoalVerified);
        clock.At(499); Assert.Null(run.Evaluate()); Assert.False(wait.ConfirmResult());
        clock.At(500); Assert.True(wait.ConfirmResult()); wait.Complete();
        Assert.Equal(RunReason.GoalSatisfied, run.Evaluate()!.Cause.Reason);
        var anotherClock = new Clock(); var another = Running(anotherClock, success: false);
        anotherClock.At(4800);
        var clamped = another.RequestPlanner()!.ApproveElapsedWait(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))!;
        Assert.Equal(TimeSpan.FromMilliseconds(5000), clamped.Deadline);
        anotherClock.At(5000); Assert.False(clamped.ConfirmResult());
        Assert.Equal(RunReason.MaxDuration, another.Evaluate()!.Cause.Reason);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void ElapsedWaitCannotRaiseExistingWaitCeilings(int duration)
    {
        var clock = new Clock(); var run = Running(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => run.RequestPlanner()!.ApproveElapsedWait(TimeSpan.FromMilliseconds(duration), TimeSpan.FromSeconds(2)));
        Assert.Equal(1, run.Budget.Snapshot.Decisions); Assert.Equal(0, run.Budget.Snapshot.Actions);
    }
}
