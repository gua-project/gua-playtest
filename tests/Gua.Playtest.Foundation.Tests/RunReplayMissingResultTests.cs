using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Replay;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedReplayMissingReceiptDistinguishesUnsentDeadline(bool sent)
    {
        var clock = new Clock(); var run = Running(clock, limits: Limits(actions: 1));
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        int sends = 0;
        using var calls = new ReplayCalls(run, operation, () => ReplayCheck.Approved, () => { });
        var queued = calls.SendAsync(0, before =>
        {
            before(); sends++; return new ReplaySend<bool>(true, true);
        }, CancellationToken.None).AsTask();
        clock.At(999);
        if (sent)
        {
            calls.Pump(CancellationToken.None);
            Assert.True(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(run.Evaluate()); Assert.False(operation.ResultConfirmed); Assert.False(run.GoalVerified);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), operation.ResultDeadline);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), operation.Deadline);
        clock.At(1000);
        var primary = run.Evaluate()!;
        Assert.Equal(sent ? RunReason.ActionUnconfirmed : RunReason.ActionsExhausted, primary.Cause.Reason);
        if (!sent)
        {
            Assert.Contains(run.Events, e => e.Reason == RunReason.WaitExpired);
            Assert.DoesNotContain(run.Events, e => e.Reason == RunReason.ActionUnconfirmed);
            calls.Pump(new CancellationToken(true));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(sent ? 1 : 0, sends);
        Assert.Equal(sent ? 1 : 0, run.Budget.Snapshot.Actions);
        Assert.Equal(0, run.Budget.Snapshot.ReservedActions);
        Assert.False(operation.ConfirmResult()); // late receipt cannot replace the primary
        Assert.Throws<InvalidOperationException>(() => run.ApproveOperation(1, TimeSpan.FromSeconds(1)));
        var cleanup = new OwnedCleanup(); int released = 0;
        cleanup.Register(CleanupStage.InputRelease, _ => { released++; return ValueTask.FromResult(true); });
        cleanup.Register(CleanupStage.ResourceRelease, _ => { released++; return ValueTask.FromResult(true); });
        var result = await cleanup.CompleteAsync(run, clock);
        Assert.Same(primary, result.Primary); Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, released); Assert.Empty(result.PostProcessing);
        Assert.Equal(sent ? 1 : 0, sends);
    }
}
