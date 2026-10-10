using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Fact]
    public void OnGoalClosesReservedSuffixWhileSentReceiptRetainsSettlement()
    {
        var run = Running(new Clock()); var operation = run.ApproveOperation(2, TimeSpan.FromSeconds(2))!;
        operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero));
        Assert.Equal(DeliveryState.NotSent, operation.Actions.Deliveries[1]);
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(1));
        Assert.True(operation.IsOpen); Assert.Equal(1, run.Budget.Snapshot.Actions); Assert.Equal(0, run.Budget.Snapshot.ReservedActions);
        Assert.True(operation.ConfirmResult()); operation.Complete();
        Assert.Equal(ResultStatus.Passed, run.Evaluate()!.Status);
    }
    private static RunSession.ApprovedOperation? ObservationAuthority(RunSession run, TimeSpan window)
        => run.ApproveObservation(window);
    [Fact]
    public void ReachedObservationRenewsFiniteAuthorityAfterLastActionWithoutReopeningBudget()
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var action = run.ApproveOperation(1, TimeSpan.FromSeconds(2))!;
        action.BeginDispatch(0); action.Actions!.ConfirmSent(0); Assert.True(action.ConfirmResult()); action.Complete();
        clock.At(500);
        var observation = ObservationAuthority(run, TimeSpan.FromSeconds(2)); Assert.NotNull(observation);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), observation.Deadline);
        Assert.Null(run.ApproveOperation(1, TimeSpan.FromSeconds(2))); Assert.Null(run.RequestPlanner());
        Assert.Null(run.Evaluate()); clock.At(2499); Assert.Null(run.Evaluate());
        clock.At(2500); Assert.Equal(RunReason.ActionsExhausted, run.Evaluate()!.Cause.Reason);
        // Preserve the normative equal-priority budget tie while retaining the checkpoint expiry fact.
        Assert.Contains(run.Events, x => x.Reason == RunReason.WaitExpired);
        Assert.Equal(ResultStatus.Failed, run.Primary!.Status);
        Assert.Equal(new BudgetSnapshot(1, 0, 0, 1, 0), run.Budget.Snapshot);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ReachedObservationBlocksGoalUntilActualConfirmationOrExpiry(bool confirmed)
    {
        var clock = new Clock(); var run = Running(clock);
        var observation = ObservationAuthority(run, TimeSpan.FromSeconds(2))!;
        Assert.Null(run.Evaluate(Unit(), TimeSpan.Zero)); Assert.True(run.GoalVerified);
        if (confirmed) { Assert.True(observation.ConfirmResult()); observation.Complete(); Assert.Equal(ResultStatus.Passed, run.Evaluate()!.Status); }
        else { clock.At(2000); Assert.Equal(RunReason.WaitExpired, run.Evaluate()!.Cause.Reason); }
        Assert.Equal(0, run.Budget.Snapshot.Actions); Assert.Equal(0, run.Budget.Snapshot.Decisions);
    }
    [Fact]
    public void ObservationAuthorityCannotRebaseGlobalDeadlineOrHideCancellation()
    {
        var clock = new Clock(); var run = Running(clock, success: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => run.ApproveObservation(TimeSpan.FromMilliseconds(2001)));
        var first = run.ApproveObservation(TimeSpan.FromSeconds(2))!;
        Assert.Null(run.ApproveObservation(TimeSpan.FromSeconds(1)));
        Assert.True(first.ConfirmResult()); first.Complete(); clock.At(4900);
        var final = run.ApproveObservation(TimeSpan.FromSeconds(2))!;
        Assert.Equal(TimeSpan.FromMilliseconds(5000), final.Deadline);
        Assert.Equal(RunReason.Cancelled, run.Evaluate(cancelled: true)!.Cause.Reason);
        Assert.False(final.ConfirmResult()); Assert.False(final.IsOpen);
        Assert.Throws<InvalidOperationException>(() => run.ApproveObservation(TimeSpan.FromSeconds(1)));
    }
    [Fact]
    public void ReportedSendEvidenceConsumesOneAttemptWithoutApprovingDispatch()
    {
        var run = Running(new Clock(), success: false); var operation = run.ApproveOperation(2, TimeSpan.FromSeconds(2))!;
        operation.RecordUnconfirmedSend(0); operation.RecordUnconfirmedSend(0);
        Assert.Equal(1, run.Budget.Snapshot.Actions); Assert.Equal(1, run.Budget.Snapshot.ReservedActions);
        Assert.Equal(DeliveryState.Uncertain, operation.Actions!.Deliveries[0]);
        Assert.Throws<InvalidOperationException>(() => operation.BeginDispatch(0));
        operation.Complete();
        Assert.Equal(0, run.Budget.Snapshot.ReservedActions); Assert.Equal(DeliveryState.NotSent, operation.Actions.Deliveries[1]);
        Assert.Equal(RunReason.ActionUnconfirmed, run.Evaluate()!.Cause.Reason);
        Assert.Throws<InvalidOperationException>(() => operation.RecordUnconfirmedSend(1));
        Assert.Equal(1, run.Budget.Snapshot.Actions);
    }
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
