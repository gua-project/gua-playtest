using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    private sealed class CancellationArbitrationClock : IClock
    {
        public AsyncLocal<bool> Driver { get; } = new();
        public Action? OnDriverRead;
        public bool ActiveDriverRead;
        private long ticks;
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        public TimeSpan Elapsed
        {
            get
            {
                if (Driver.Value && Interlocked.Exchange(ref OnDriverRead, null) is { } callback)
                { ActiveDriverRead = true; try { callback(); } finally { ActiveDriverRead = false; } }
                return TimeSpan.FromTicks(Interlocked.Read(ref ticks));
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            lock (timers) timers.Add((Elapsed + duration, completion));
            return new(completion.Task);
        }
        public void At(TimeSpan position)
        {
            Interlocked.Exchange(ref ticks, position.Ticks);
            lock (timers) foreach (var timer in timers.Where(t => t.Due <= position)) timer.Completion.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecutorInterruptionCannotFinalizeInsideDriverArbitration(bool confirmedSent, bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var cleanupStarted = new ManualResetEventSlim();
        PrimaryResult? cleanupPrimary = null; var cleanupOverlappedOwner = false;
        cleanup.Register(CleanupStage.ResourceRelease, _ =>
        {
            cleanupPrimary = run.Primary; cleanupOverlappedOwner = real.ActiveDriverRead;
            cleanupStarted.Set(); return ValueTask.FromResult(true);
        });
        var outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), async (owner, _) =>
        {
            await Task.Yield(); real.Driver.Value = true;
            var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
            operation.BeginDispatch(0); if (confirmedSent) operation.Actions!.ConfirmSent(0);
            operation.Complete();
            // Evaluate has already moved ActionUnconfirmed into its local evidence unit
            // before reading the clock. Hold that owner unit while caller cancellation
            // gives the outer executor a bounded opportunity to attempt finalization.
            real.OnDriverRead = () =>
            {
                if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
                cleanupStarted.Wait(TimeSpan.FromMilliseconds(100));
            };
            owner.Evaluate(cancelled: !deadline);
            real.Driver.Value = false;
            return false;
        }, caller.Token);
        Assert.NotNull(cleanupPrimary);
        Assert.Equal(ResultStatus.Failed, cleanupPrimary.Status);
        Assert.Equal(RunReason.ActionUnconfirmed, cleanupPrimary.Cause.Reason);
        Assert.False(cleanupOverlappedOwner);
        Assert.Same(cleanupPrimary, outcome.Primary);
        Assert.Contains(outcome.Events, evidence => evidence.Reason == (deadline ? RunReason.MaxDuration : RunReason.Cancelled));
        Assert.Equal(1, run.Budget.Snapshot.Actions);
    }

    [Fact]
    public async Task CancellationArrivingInsideGoalUnitRetainsItsHigherPriority()
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition());
        using var caller = new CancellationTokenSource(); using var cleanupStarted = new ManualResetEventSlim();
        var cleanup = new OwnedCleanup(); PrimaryResult? observed = null;
        cleanup.Register(CleanupStage.ResourceRelease, _ =>
        { observed = run.Primary; cleanupStarted.Set(); return ValueTask.FromResult(true); });
        var outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), async (owner, _) =>
        {
            await Task.Yield(); real.Driver.Value = true;
            // The cancellation flag was sampled before this in-flight unit. A waiting
            // executor must post the new causal evidence before the unit freezes Goal.
            real.OnDriverRead = () => { caller.Cancel(); cleanupStarted.Wait(TimeSpan.FromMilliseconds(100)); };
            owner.Evaluate(Unit(), TimeSpan.Zero, cancelled: false);
            real.Driver.Value = false; return true;
        }, caller.Token);
        Assert.NotNull(observed); Assert.Equal(RunReason.Cancelled, observed.Cause.Reason);
        Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status); Assert.Same(observed, outcome.Primary);
        Assert.Contains(outcome.Events, evidence => evidence.Reason == RunReason.GoalSatisfied);
    }

    [Fact]
    public async Task CertifiedInitialGoalRetainsCancellationBeforeConfirmation()
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition()); using var caller = new CancellationTokenSource();
        var executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            return ValueTask.FromResult(capture.Certify(capture.RequestId, real.Elapsed,
                new(condition.Elapsed, Unit(), Unit("false")), "synchronized/source/epoch", true));
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, caller.Token, null, _ =>
        {
            real.Driver.Value = true;
            real.OnDriverRead = caller.Cancel;
        });
        Assert.False(executed); Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status);
        Assert.Equal(new(RunReason.Cancelled, RunPhase.Execution, RunOrigin.User), outcome.Primary.Cause);
        Assert.True(run.GoalVerified);
    }
}
