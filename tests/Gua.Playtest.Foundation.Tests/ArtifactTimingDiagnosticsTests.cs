using System.Collections.Concurrent;
using System.Diagnostics;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ArtifactTimingDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "artifact-timing-" + Guid.NewGuid().ToString("N"));
    private static ArtifactLimits Limits => new(1024 * 1024, 8 * 1024 * 1024, 1000, 65536);
    private RunArtifactStore Store() => RunArtifactStore.Create(root, Limits, new([], []));
    private static PrimaryResult Primary => new(ResultStatus.Passed, new(RunReason.GoalSatisfied, RunPhase.Execution, RunOrigin.Condition));

    [Fact]
    public void Primary_phases_identify_one_durable_publish_without_content()
    {
        var store = Store();
        using var events = new Events(store);
        var result = store.ConfirmPrimary(Primary, [], [], []);
        Assert.True(result.Saved);
        var samples = events.Snapshot();
        Assert.Equal(new[] { "primary.confirm.start", "primary.encode.start", "primary.encode.returned",
            "primary.write.start", "primary.write.returned", "primary.flush.start", "primary.flush.returned",
            "primary.move.start", "primary.move.returned", "primary.saved", "primary.confirm.exit" }, samples.Select(x => x.Phase));
        Assert.Single(samples.Select(x => x.Operation).Distinct());
        Assert.True(samples[0].Operation > 0);
        Assert.Equal(1, samples.Single(x => x.Phase == "primary.saved").Detail);
        Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.Timestamp <= pair.Second.Timestamp));
        Assert.All(samples, sample => { Assert.Null(sample.Budget); Assert.Null(sample.Remaining); });
    }

    [Fact]
    public void Cancelled_publish_reports_unsaved_without_implying_flush_or_move()
    {
        var store = Store();
        using var events = new Events(store);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = store.ConfirmPrimary(Primary, [], [], [], cancellation.Token);
        Assert.False(result.Saved);
        Assert.Equal(PersistenceFailure.Cancelled, result.Failure);
        var samples = events.Snapshot();
        Assert.Equal(0, samples.Single(x => x.Phase == "primary.saved").Detail);
        Assert.DoesNotContain(samples, x => x.Phase is "primary.flush.start" or "primary.move.start");
        Assert.Equal("primary.confirm.exit", samples.Last().Phase);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
    }

    [Fact]
    public void Refused_publish_retains_failure_and_has_a_terminal_sample()
    {
        var store = Store();
        using var events = new Events(store);
        Assert.True(store.ConfirmPrimary(Primary, [], [], []).Saved);
        var second = store.ConfirmPrimary(Primary, [], [], []);
        Assert.False(second.Saved);
        Assert.Equal(PersistenceFailure.InvalidEvidence, second.Failure);
        var operation = events.Snapshot().Last().Operation;
        var samples = events.Snapshot().Where(x => x.Operation == operation).ToArray();
        Assert.Equal(new[] { "primary.confirm.start", "primary.saved", "primary.confirm.exit" }, samples.Select(x => x.Phase));
        Assert.Equal(0, samples[1].Detail);
    }

    [Fact]
    public void Throwing_numeric_diagnostic_observer_does_not_replace_saved_result()
    {
        var store = Store();
        using var events = new Events(store, throws: true);
        Assert.True(store.ConfirmPrimary(Primary, [], [], []).Saved);
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        // CLI listener has the same non-authoritative delivery contract.
        ArtifactTimingDiagnostics.Mark(store, "test.numeric", ArtifactTimingDiagnostics.NextOperation());
        Assert.True(events.Deliveries > 0);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    internal sealed record Sample(string Phase, long Timestamp, long Operation, long? Budget, long? Remaining, int Detail);
    internal sealed class Events : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly object owner;
        private readonly bool throws;
        private readonly Action<string>? controlledPhase;
        private readonly ConcurrentQueue<Sample> samples = new();
        private readonly ConcurrentBag<IDisposable> subscriptions = new();
        private readonly IDisposable all;
        private int deliveries;
        public int Deliveries => Volatile.Read(ref deliveries);
        public Events(object owner, bool throws = false, Action<string>? controlledPhase = null)
        { this.owner = owner; this.throws = throws; this.controlledPhase = controlledPhase; all = DiagnosticListener.AllListeners.Subscribe(this); }
        public Sample[] Snapshot() => samples.ToArray();
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name is "Gua.Playtest.RuntimeTiming" or "Gua.Playtest.ArtifactTiming")
                subscriptions.Add(value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not ValueTuple<object, long, long, long?, long?, int, bool> data || !ReferenceEquals(data.Item1, owner)) return;
            Interlocked.Increment(ref deliveries);
            if (throws) throw new InvalidOperationException("DiagnosticObserverFault");
            samples.Enqueue(new(value.Key, data.Item2, data.Item3, data.Item4, data.Item5, data.Item6));
            controlledPhase?.Invoke(value.Key);
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { all.Dispose(); foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}

public sealed partial class RunTests
{
    [Fact]
    public async Task CleanupTimingDoesNotTreatReturnedReleaseTaskAsCompleted()
    {
        var clock = new Clock(); var run = Running(clock);
        run.Evaluate(Unit(), TimeSpan.Zero);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.ResourceRelease, _ => new(pending.Task));
        using var events = new ArtifactTimingDiagnosticsTests.Events(run, controlledPhase: phase =>
        {
            if (phase == "cleanup.task.completed.observed") completedObserved.TrySetResult();
        });
        var finishing = cleanup.CompleteAsync(run, clock).AsTask();
        try
        {
            Assert.Contains(events.Snapshot(), x => x.Phase == "cleanup.task.returned");
            Assert.DoesNotContain(events.Snapshot(), x => x.Phase is "cleanup.task.completed.observed" or "cleanup.release.attempt-exit");
            Assert.False(pending.Task.IsCompleted);
            pending.SetResult(true);
            var outcome = await finishing.WaitAsync(TimeSpan.FromSeconds(1));
            await completedObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(outcome.PostProcessingComplete);
            var returned = Assert.Single(events.Snapshot(), x => x.Phase == "cleanup.task.returned");
            Assert.Contains(events.Snapshot(), x => x.Phase == "cleanup.task.completed.observed" && x.Operation == returned.Operation && x.Detail == (int)TaskStatus.RanToCompletion);
            Assert.Contains(events.Snapshot(), x => x.Phase == "cleanup.release.attempt-exit" && x.Operation == returned.Operation);
        }
        finally { pending.TrySetResult(true); await finishing.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    // Controlled delay/clock injection, not the historical natural I/O failure.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrimaryTimingDistinguishesSaveIntervalFromReturnedCallbackClockExpiry(bool insideSave)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "primary-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(); var run = Running(clock);
            run.Evaluate(Unit(), TimeSpan.Zero);
            Assert.Equal(ResultStatus.Passed, run.Primary!.Status);
            var store = RunArtifactStore.Create(directory, new(1024 * 1024, 8 * 1024 * 1024, 1000, 65536), new([], []));
            var injected = 0;
            var controlledClockSamples = new Dictionary<string, TimeSpan>();
            void Advance(string phase)
            {
                controlledClockSamples[phase] = clock.Elapsed;
                if (phase == (insideSave ? "primary.write.start" : "cleanup.callback.returned"))
                {
                    Interlocked.Increment(ref injected);
                    Thread.Sleep(20); // isolated diagnostic observer injection only
                    clock.At(2000);
                }
            }
            using var storage = new ArtifactTimingDiagnosticsTests.Events(store, controlledPhase: Advance);
            using var cleanup = new ArtifactTimingDiagnosticsTests.Events(run, controlledPhase: Advance);
            var outcome = await new OwnedCleanup().CompleteAsync(run, clock, confirmPrimary: (snapshot, token) =>
                ValueTask.FromResult(store.ConfirmPrimary(snapshot, [], [], token).Saved));
            Assert.Equal(1, injected);
            Assert.Same(run.Primary, outcome.Primary);
            Assert.Equal(ResultStatus.Passed, outcome.Primary.Status);
            Assert.Equal(new[] { PostProcessingReason.CleanupTimeout, PostProcessingReason.PrimarySnapshotFailed },
                outcome.PostProcessing.Select(x => x.Reason));
            Assert.Equal(11, outcome.ExitCode);
            var saved = Assert.Single(storage.Snapshot(), x => x.Phase == "primary.saved");
            Assert.Equal(1, saved.Detail);
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
            var samples = cleanup.Snapshot();
            var start = Assert.Single(samples, x => x.Phase == "cleanup.stage.start");
            Assert.True(start.Budget > 0);
            var remaining = Assert.Single(samples, x => x.Phase == "cleanup.ownercheck.after-return");
            Assert.True(remaining.Remaining < 0);
            Assert.Equal(start.Operation, remaining.Operation);
            Assert.Contains(samples, x => x.Phase == "cleanup.stage.timeout" && x.Operation == start.Operation);
            var callbackReturned = Assert.Single(samples, x => x.Phase == "cleanup.callback.returned");
            Assert.True(saved.Timestamp <= callbackReturned.Timestamp);
            Assert.True(callbackReturned.Timestamp <= remaining.Timestamp);
            Assert.Equal(insideSave ? TimeSpan.FromSeconds(2) : TimeSpan.Zero, controlledClockSamples["primary.flush.start"]);
            Assert.Equal(insideSave ? TimeSpan.FromSeconds(2) : TimeSpan.Zero, controlledClockSamples["cleanup.callback.returned"]);
            Assert.Equal(TimeSpan.FromSeconds(2), controlledClockSamples["cleanup.ownercheck.after-return"]);
            var delayedEnd = insideSave
                ? Assert.Single(storage.Snapshot(), x => x.Phase == "primary.write.returned")
                : remaining;
            var delayedStart = insideSave
                ? Assert.Single(storage.Snapshot(), x => x.Phase == "primary.write.start")
                : callbackReturned;
            double injectedIntervalMs = (delayedEnd.Timestamp - delayedStart.Timestamp) * 1000d / Stopwatch.Frequency;
            Assert.True(injectedIntervalMs >= 15, $"controlled interval {injectedIntervalMs}ms");
            string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../artifacts/tests/runtime-evidence/primary-timing"));
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, Guid.NewGuid().ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    kind = "controlled-delay-and-clock-injection", insideSave, injectedIntervalMs,
                    saved = saved.Detail == 1, ownerRemainingTicks = remaining.Remaining,
                    exitCode = outcome.ExitCode, frequency = Stopwatch.Frequency,
                    storage = storage.Snapshot(), cleanup = samples
                }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
