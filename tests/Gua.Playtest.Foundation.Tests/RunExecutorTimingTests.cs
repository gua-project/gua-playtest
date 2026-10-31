using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Fact]
    public void ExecutorTimingFailureSnapshotIsBoundedAndSerializesTupleFieldsExplicitly()
    {
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, clock);
        using var timing = new ExecutorTimingCapture(run);
        for (var index = 0; index < 520; index++)
            timing.OnNext(new KeyValuePair<string, object?>("controlled.phase",
                ((object)run, (long)index, (long)index + 1, (long?)100, (long?)50, (int)TaskStatus.Running, true)));
        using var snapshot = JsonDocument.Parse(timing.FailureSnapshot());
        var root = snapshot.RootElement;
        Assert.Equal(8, root.GetProperty("overflow").GetInt64());
        Assert.Equal(512, root.GetProperty("phases").GetArrayLength());
        var phase = root.GetProperty("phases")[0];
        Assert.Equal("controlled.phase", phase.GetProperty("phase").GetString());
        Assert.Equal(1, phase.GetProperty("operation").GetInt64());
        Assert.Equal(100, phase.GetProperty("budgetTicks").GetInt64());
        Assert.Equal(50, phase.GetProperty("remainingTicks").GetInt64());
        Assert.Equal((int)TaskStatus.Running, phase.GetProperty("detail").GetInt32());
        Assert.True(phase.GetProperty("pool").GetBoolean());
    }

    [Fact]
    public async Task ExecutorTimingCorrelatesExistingPollAndGateFreePrimaryObservation()
    {
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, clock);
        using var timing = new ExecutorTimingCapture(run);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(),
            (_, _) => ValueTask.FromResult(true), (_, _) => new ValueTask<bool>(release.Task)).AsTask();
        try
        {
            await timing.PollCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));
            release.TrySetResult(true);
            await execution.WaitAsync(TimeSpan.FromSeconds(1));
            await timing.PollCompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var entries = timing.Entries.ToArray();
            var created = entries.First(item => item.Phase == "executor.poll-wait.create");
            Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks, created.Budget);
            Assert.Contains(entries, item => item.Phase == "executor.poll-wait.resume" && item.Operation == created.Operation);
            Assert.Contains(entries, item => item.Phase == "executor.poll-wait.completion-observed" && item.Operation == created.Operation);
            Assert.Contains(entries, item => item.Phase == "executor.primary.observed");
            Assert.All(entries, item => Assert.False(item.InAuthorityGate));
        }
        finally { release.TrySetResult(true); await execution.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorTimingDistinguishesReachedLifecycleReturnAndThrow(bool throws)
    {
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, clock);
        using var timing = new ExecutorTimingCapture(run);
        var reached = 0;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/timing-probe", true);
            boundary.InitialLifecycleFailure = () =>
            {
                Interlocked.Increment(ref reached);
                if (throws) throw new InvalidOperationException("controlled lifecycle failure");
                return null;
            };
            return ValueTask.FromResult(boundary);
        }, (_, _) => ValueTask.FromResult(true)).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var entries = timing.Entries.ToArray();
        Assert.True(reached > 0);
        var entered = entries.First(item => item.Phase == "session.lifecycle-probe.enter");
        Assert.Contains(entries, item => item.Phase == (throws ? "session.lifecycle-probe.throw" : "session.lifecycle-probe.return")
            && item.Operation == entered.Operation && item.Timestamp >= entered.Timestamp);
        Assert.Contains(entries, item => item.Phase == "executor.primary.observed");
        Assert.Same(outcome.Primary, run.Primary);
        Assert.All(entries, item => Assert.False(item.InAuthorityGate));
    }

    private sealed class ExecutorTimingCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly RunSession owner;
        private readonly object authorityGate;
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private long firstPollOperation;
        private long received;
        public readonly ConcurrentQueue<(string Phase, long Timestamp, long Operation, long? Budget, long? Remaining, int Detail, bool Pool, bool InAuthorityGate)> Entries = [];
        public readonly TaskCompletionSource PollCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PollCompletionObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExecutorTimingCapture(RunSession owner)
        {
            this.owner = owner;
            authorityGate = typeof(RunSession).GetField("postedExceptionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Gua.Playtest.RuntimeTiming") subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is not ValueTuple<object, long, long, long?, long?, int, bool> value || !ReferenceEquals(value.Item1, owner)) return;
            if (Interlocked.Increment(ref received) <= 512)
                Entries.Enqueue((item.Key, value.Item2, value.Item3, value.Item4, value.Item5, value.Item6, value.Item7, Monitor.IsEntered(authorityGate)));
            if (item.Key == "executor.poll-wait.create")
            {
                Interlocked.CompareExchange(ref firstPollOperation, value.Item3, 0);
                PollCreated.TrySetResult();
            }
            if (item.Key == "executor.poll-wait.completion-observed" && value.Item3 == Volatile.Read(ref firstPollOperation))
                PollCompletionObserved.TrySetResult();
        }
        public string FailureSnapshot() => JsonSerializer.Serialize(new
        {
            snapshotTimestamp = Stopwatch.GetTimestamp(),
            stopwatchFrequency = Stopwatch.Frequency,
            overflow = Math.Max(0, Volatile.Read(ref received) - 512),
            phases = Entries.ToArray().Select(item => new
            {
                phase = item.Phase, timestamp = item.Timestamp, operation = item.Operation,
                budgetTicks = item.Budget, remainingTicks = item.Remaining,
                detail = item.Detail, pool = item.Pool, inAuthorityGate = item.InAuthorityGate
            })
        });
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}
