using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentExecutorsKeepIndependentTimeTimersAndCancellation(bool betaFirst)
    {
        using var alphaTime = new ControlledExecutorTime();
        using var betaTime = new ControlledExecutorTime();
        var alphaClock = new CancellationArbitrationClock();
        var betaClock = new CancellationArbitrationClock();
        var alpha = new RunSession(Limits(), alphaClock, new Clock());
        var beta = new RunSession(Limits(), betaClock, new Clock());
        using var alphaTiming = new ExecutorTimingCapture(alpha);
        using var betaTiming = new ExecutorTimingCapture(beta);
        using var alphaCancel = new CancellationTokenSource();
        using var betaCancel = new CancellationTokenSource();
        var alphaReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alphaProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alphaStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int alphaReleases = 0, betaReleases = 0;

        Task<RunOutcome> Start(RunSession run, CancellationArbitrationClock clock, ControlledExecutorTime time,
            CancellationToken token, TaskCompletionSource ready, TaskCompletionSource stopped,
            Task<bool> provider, Action release)
        {
            var cleanup = new OwnedCleanup();
            cleanup.Register(CleanupStage.ResourceRelease, _ => { release(); return ValueTask.FromResult(true); });
            return RunExecutor.ExecuteWithTimeAsync(run, clock, cleanup, (owner, _, _) =>
            {
                var capture = owner.ArmRunningBoundary();
                return ValueTask.FromResult(capture.Certify(capture.RequestId, TimeSpan.Zero,
                    new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/isolation", true));
            }, async (_, operationToken) =>
            {
                using var registration = operationToken.Register(() => stopped.TrySetResult());
                ready.TrySetResult();
                return await provider;
            }, token, null, time).AsTask();
        }

        var alphaTask = Start(alpha, alphaClock, alphaTime, alphaCancel.Token, alphaReady, alphaStopped,
            alphaProvider.Task, () => Interlocked.Increment(ref alphaReleases));
        var betaTask = Start(beta, betaClock, betaTime, betaCancel.Token, betaReady, betaStopped,
            betaProvider.Task, () => Interlocked.Increment(ref betaReleases));
        try
        {
            await Task.WhenAll(alphaReady.Task, betaReady.Task, alphaTiming.PollCreated.Task, betaTiming.PollCreated.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var firstTime = betaFirst ? betaTime : alphaTime;
            var secondTime = betaFirst ? alphaTime : betaTime;
            var firstTask = betaFirst ? betaTask : alphaTask;
            var secondTask = betaFirst ? alphaTask : betaTask;
            var firstRun = betaFirst ? beta : alpha;
            var secondRun = betaFirst ? alpha : beta;
            var firstTiming = betaFirst ? betaTiming : alphaTiming;
            var secondTiming = betaFirst ? alphaTiming : betaTiming;
            var firstStopped = betaFirst ? betaStopped : alphaStopped;
            var secondStopped = betaFirst ? alphaStopped : betaStopped;
            var firstCancel = betaFirst ? betaCancel : alphaCancel;
            var secondCancel = betaFirst ? alphaCancel : betaCancel;

            firstCancel.Cancel();
            await firstStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AdvanceInterruptedExecutorAsync(firstTime, firstTiming, firstTask);
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(first.PostProcessingComplete);
            Assert.Equal(RunReason.Cancelled, first.Primary.Cause.Reason);
            Assert.False(secondTask.IsCompleted);
            Assert.False(secondStopped.Task.IsCompleted);
            Assert.False(secondCancel.IsCancellationRequested);
            Assert.Null(secondRun.Primary);
            Assert.Equal(ExecutionState.Running, secondRun.State);
            Assert.Equal(TimeSpan.Zero, secondTime.Elapsed);
            Assert.Empty(secondTime.History);
            Assert.Equal(betaFirst ? 0 : 1, alphaReleases);
            Assert.Equal(betaFirst ? 1 : 0, betaReleases);

            // Even advancing the finished Run beyond its cleanup deadline cannot
            // complete the other Run's pending poll or consume its cleanup epoch.
            var frozen = firstRun.Events.ToArray();
            firstTime.AdvanceTo(TimeSpan.FromMilliseconds(1500));
            Assert.False(secondTask.IsCompleted);
            Assert.Empty(secondTime.History);
            secondCancel.Cancel();
            await secondStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AdvanceInterruptedExecutorAsync(secondTime, secondTiming, secondTask);
            var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(second.PostProcessingComplete);
            Assert.Equal(RunReason.Cancelled, second.Primary.Cause.Reason);
            Assert.Equal(1, alphaReleases); Assert.Equal(1, betaReleases);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), firstTime.Elapsed);
            Assert.True(secondTime.Elapsed < secondRun.Limits.CleanupTimeout);
            Assert.Equal(frozen, firstRun.Events);
            Assert.Equal(TimeSpan.Zero, alphaClock.Elapsed);
            Assert.Equal(TimeSpan.Zero, betaClock.Elapsed);
        }
        finally
        {
            alphaCancel.Cancel(); betaCancel.Cancel();
            alphaProvider.TrySetResult(true); betaProvider.TrySetResult(true);
            await Task.WhenAll(alphaTask, betaTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
