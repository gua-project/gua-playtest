using System.Diagnostics;

namespace Gua.Playtest.Cli;

// A bounded, best-effort tail of numeric events. Delivery does not take application
// locks, inspect owners, or perform I/O. The owner references never leave this collector.
internal sealed class LifecycleTiming : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly object run, store;
    private readonly long origin;
    private readonly Entry?[] entries;
    private readonly IDisposable all;
    private IDisposable? runtime, artifact;
    private long sequence;

    internal LifecycleTiming(object run, object store, long origin, int capacity = 1024)
    {
        this.run = run; this.store = store; this.origin = origin;
        entries = new Entry[Math.Clamp(capacity, 1, 1024)];
        all = DiagnosticListener.AllListeners.Subscribe(this);
    }
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Gua.Playtest.RuntimeTiming") runtime = listener.Subscribe(this);
        if (listener.Name == "Gua.Playtest.ArtifactTiming") artifact = listener.Subscribe(this);
    }
    public void OnNext(KeyValuePair<string, object?> notification)
    {
        if (notification.Value is not ValueTuple<object, long, long, long?, long?, int, bool> value ||
            !ReferenceEquals(value.Item1, run) && !ReferenceEquals(value.Item1, store)) return;
        long number = Interlocked.Increment(ref sequence);
        var entry = new Entry(number, notification.Key, value.Item2 - origin, value.Item3,
            value.Item4, value.Item5, value.Item6, value.Item7);
        int index = (int)((number - 1) % entries.Length);
        while (true)
        {
            var previous = Volatile.Read(ref entries[index]);
            if (previous is not null && previous.Number > number) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref entries[index], entry, previous), previous)) return;
        }
    }
    internal object Snapshot()
    {
        long started = Stopwatch.GetTimestamp() - origin;
        var copied = Enumerable.Range(0, entries.Length).Select(index => Volatile.Read(ref entries[index])).Where(item => item is not null)
            .OrderBy(item => item!.Number).ToArray();
        long count = Volatile.Read(ref sequence);
        return new
        {
            schema = "playtest.lifecycle-timing.v1", frequency = Stopwatch.Frequency,
            snapshotStartedTicks = started, snapshotEndedTicks = Stopwatch.GetTimestamp() - origin,
            limit = entries.Length, totalObserved = count, overwritten = Math.Max(0, count - entries.Length),
            sampling = "bounded-best-effort tail; concurrent delivery may race snapshot",
            missingPhaseMeaning = "not-observed-at-snapshot; not proof of never-started",
            completionMeaning = "completion-observed is observer delivery, not timer fire or task transition",
            primaryMeaning = "primary.observed is arrival after arbitration, not exact primary publication",
            timeUnits = "ticks use monotonic frequency; budgetTicks and remainingTicks use TimeSpan ticks (100ns)",
            events = copied.Select(item => new
            {
                number = item!.Number, phase = item.Phase, ticks = item.Ticks, operation = item.Operation,
                budgetTicks = item.BudgetTicks, remainingTicks = item.RemainingTicks,
                detail = item.Detail, onThreadPool = item.OnThreadPool
            })
        };
    }
    private sealed record Entry(long Number, string Phase, long Ticks, long Operation,
        long? BudgetTicks, long? RemainingTicks, int Detail, bool OnThreadPool);
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() { all.Dispose(); runtime?.Dispose(); artifact?.Dispose(); }
}
