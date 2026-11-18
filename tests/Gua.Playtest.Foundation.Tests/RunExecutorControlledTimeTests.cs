using System.Diagnostics;
using System.Collections.Concurrent;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    // The watchdog detects a hung test; only explicit advancement below consumes
    // the independent budget. Provider (IClock) time remains a separate input.
    private static async Task AdvanceInterruptedExecutorAsync(ControlledExecutorTime time,
        ExecutorTimingCapture timing, Task executing)
    {
        var watchdog = Stopwatch.StartNew();
        while (!executing.IsCompleted)
        {
            if (watchdog.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Controlled executor did not reach settlement: " + timing.FailureSnapshot());
            var phases = timing.Entries.ToArray();
            // Do not advance a second controller poll until the already-created
            // clock probe has actually completed. This is scheduling, not time.
            bool probePending = phases.Count(p => p.Phase == "executor.clock-probe.create") >
                phases.Count(p => p.Phase == "executor.clock-probe.completion-observed");
            if (!probePending && time.AdvanceNext() is { } duration && duration > TimeSpan.FromMilliseconds(10))
                return; // Evidence share elapsed. Freeze neither clock implicitly: the test now explicitly leaves both unchanged.
            await Task.Delay(1);
        }
    }

    [Theory]
    [InlineData(999, 0, 1)] [InlineData(1000, 0, 0)] [InlineData(1001, 0, -1)]
    [InlineData(0, 999, 1)] [InlineData(0, 1000, 0)] [InlineData(0, 1001, -1)]
    public void SettlementCountsControlledPhysicalAndProviderTimeSeparately(int physicalMs, int providerMs, int remainingMs)
    {
        using var physical = new ControlledExecutorTime();
        var provider = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), provider, new Clock());
        var settlement = new ExecutionSettlement(run, new OwnedCleanup(), physical);
        settlement.Begin();
        physical.AdvanceTo(TimeSpan.FromMilliseconds(physicalMs));
        provider.At(TimeSpan.FromMilliseconds(providerMs));
        run.ReadAuthoritativeReal();
        Assert.Equal(TimeSpan.FromMilliseconds(remainingMs), settlement.CleanupRemaining);
        Assert.Equal(TimeSpan.FromMilliseconds(physicalMs), physical.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(providerMs), run.LastValidatedReal);
    }

    [Fact]
    public async Task ControlledIndependentTimerRequiresPhysicalAdvanceAndContainsCancellation()
    {
        using var physical = new ControlledExecutorTime();
        var settled = physical.DelayAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var cancelled = physical.DelayAsync(TimeSpan.FromMilliseconds(20), cancel.Token);
        physical.AdvanceTo(TimeSpan.FromMilliseconds(9));
        Assert.False(settled.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        physical.AdvanceTo(TimeSpan.FromMilliseconds(10));
        Assert.True(settled.IsCompletedSuccessfully);
        // Timer transition precedes this observer; a deliberately late observer
        // cannot rebase the independent epoch or resurrect a cancelled timer.
        physical.AdvanceTo(TimeSpan.FromMilliseconds(1001));
        var owner = new object();
        using var phases = new ControlledTimePhases(owner, physical);
        RuntimeTiming.ObserveCompletion(owner, "controlled.timer.observed", 1, settled);
        await settled;
        var observed = Assert.Single(phases.Entries);
        Assert.Equal("controlled.timer.observed", observed.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(1001).Ticks, observed.PhysicalTicks);
        Assert.True(cancelled.IsCanceled);
        Assert.Equal(TimeSpan.FromMilliseconds(1001), physical.Elapsed);
    }

    [Fact]
    public async Task ClosedSettlementDoesNotInvokeAnotherProvider()
    {
        using var physical = new ControlledExecutorTime();
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, new Clock());
        var settlement = new ExecutionSettlement(run, new OwnedCleanup(), physical);
        settlement.Begin();
        int calls = 0;
        var exception = await Record.ExceptionAsync(() => FiniteOperation.RunUntilAsync(clock,
            TimeSpan.FromSeconds(1), _ => { calls++; return ValueTask.FromResult(true); },
            CancellationToken.None, null, settlement).AsTask());
        Assert.Equal(0, calls);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(TimeSpan.Zero, physical.Elapsed);
    }

    [Theory] [InlineData(999, true)] [InlineData(1000, false)] [InlineData(1001, false)]
    public async Task ControlledReleaseConfirmationDoesNotEraseIndependentDeadline(int physicalMs, bool complete)
    {
        using var physical = new ControlledExecutorTime();
        var clock = new Clock(); var run = Running(clock);
        run.Evaluate(Unit(), TimeSpan.Zero);
        var cleanup = new OwnedCleanup();
        int calls = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ =>
        {
            calls++;
            physical.AdvanceTo(TimeSpan.FromMilliseconds(physicalMs));
            return ValueTask.FromResult(true);
        });
        var settlement = new ExecutionSettlement(run, cleanup, physical);
        settlement.Begin();
        var primary = run.Primary;
        var result = await cleanup.CompleteAsync(run, clock, CancellationToken.None, null, settlement).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(complete, result.PostProcessingComplete);
        Assert.Equal(!complete, result.PostProcessing.Any(p => p.Reason == PostProcessingReason.CleanupTimeout));
        Assert.DoesNotContain(result.PostProcessing, p => p.Reason == PostProcessingReason.ResourceReleaseUnconfirmed);
        Assert.Equal(primary, result.Primary);
        Assert.Equal(TimeSpan.Zero, run.LastValidatedReal);
        Assert.Equal(TimeSpan.FromMilliseconds(physicalMs), physical.Elapsed);
    }

    [Fact]
    public async Task InterruptedProviderLateCompletionCannotAcquireNewAuthority()
    {
        using var physical = new ControlledExecutorTime();
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, new Clock());
        var settlement = new ExecutionSettlement(run, new OwnedCleanup(), physical);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var operation = FiniteOperation.RunUntilAsync(clock, TimeSpan.FromSeconds(1),
            _ => { calls++; return new ValueTask<bool>(completion.Task); }, CancellationToken.None, null, settlement).AsTask();
        settlement.Begin();
        // Drain exactly the evidence share, without consuming a second cleanup epoch.
        var watchdog = Stopwatch.StartNew();
        while (!operation.IsCompleted)
        {
            if (watchdog.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Late provider control did not settle");
            physical.AdvanceNext();
            await Task.Delay(1);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var before = run.Events.ToArray();
        completion.SetResult(true);
        Assert.True(completion.Task.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.Equal(before, run.Events);
        Assert.Null(run.Primary);
        Assert.True(physical.Elapsed < run.Limits.CleanupTimeout);
    }

    [Fact]
    public void ExecutorTimeBoundaryIsNotPublicAndDefaultSettlementCountsPhysicalTime()
    {
        Assert.DoesNotContain(typeof(RunExecutor).GetMethods(), method => method.Name == "ExecuteWithTimeAsync");
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, new Clock());
        var settlement = new ExecutionSettlement(run, new OwnedCleanup());
        settlement.Begin();
        var first = settlement.Elapsed;
        Thread.Sleep(20); // Positive elapsed floor, not a one-second performance assertion.
        Assert.True(settlement.Elapsed > first);
        Assert.Equal(TimeSpan.Zero, run.LastValidatedReal);
        Assert.Equal(TimeSpan.FromSeconds(1), run.Limits.CleanupTimeout);
    }

    private sealed class ControlledExecutorTime : IExecutorTime, IDisposable
    {
        private readonly object gate = new();
        private TimeSpan now;
        private readonly List<(TimeSpan Due, TimeSpan Duration, TaskCompletionSource Completion)> timers = [];
        private readonly List<(long DueTicks, long CompletedAtTicks)> history = [];
        public object[] History { get { lock (gate) return history.Select(t => (object)new { t.DueTicks, t.CompletedAtTicks }).ToArray(); } }
        public TimeSpan Elapsed { get { lock (gate) return now; } }
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (duration <= TimeSpan.Zero) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (timers.Count >= 256) throw new InvalidOperationException("Controlled timer limit exceeded");
                timers.Add((now + duration, duration, completion));
            }
            var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion.Task;
        }
        public TimeSpan? AdvanceNext()
        {
            lock (gate)
            {
                var next = timers.Where(t => !t.Completion.Task.IsCompleted).OrderBy(t => t.Due).FirstOrDefault();
                if (next.Completion is null) return null;
                AdvanceTo(next.Due);
                return next.Duration;
            }
        }
        public void AdvanceTo(TimeSpan value)
        {
            lock (gate)
            {
                if (value < now) throw new InvalidOperationException("Controlled independent time cannot reverse");
                now = value;
                foreach (var timer in timers.Where(t => t.Due <= now))
                    if (timer.Completion.TrySetResult()) history.Add((timer.Due.Ticks, now.Ticks));
            }
        }
        public void Dispose() { lock (gate) foreach (var timer in timers) timer.Completion.TrySetCanceled(); }
    }

    // Each diagnostic observer samples the controlled physical axis at delivery.
    // Wall timestamp is retained separately; it is never used as simulated time.
    private sealed class ControlledTimePhases : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly object owner;
        private readonly ControlledExecutorTime time;
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private int received;
        internal readonly ConcurrentQueue<(string Phase, long Operation, long PhysicalTicks, long WallTimestamp)> Entries = [];
        internal ControlledTimePhases(object owner, ControlledExecutorTime time)
        {
            this.owner = owner; this.time = time;
            subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Gua.Playtest.RuntimeTiming") subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is ValueTuple<object, long, long, long?, long?, int, bool> value && ReferenceEquals(value.Item1, owner) &&
                Interlocked.Increment(ref received) <= 256)
                Entries.Enqueue((item.Key, value.Item3, time.Elapsed.Ticks, value.Item2));
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}
