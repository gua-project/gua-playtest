using System.Diagnostics;
using System.Text.Json;
using Gua.Playtest.TestSupport;

namespace Gua.Playtest.Foundation.Tests;

internal sealed class ReplayFailureDiagnostics : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    static readonly AsyncLocal<ReplayFailureDiagnostics?> Current = new();
    readonly ReplayFailureDiagnostics? previous;
    readonly ReplayTimingJournal journal;
    readonly AsyncLocal<int> run = new();
    readonly IDisposable all;
    readonly System.Collections.Concurrent.ConcurrentBag<IDisposable> listeners = [];
    int errors;
    internal ReplayFailureDiagnostics(string pidPath, int capacity = 1024)
    {
        journal = ReplayTimingJournal.Create(pidPath, capacity);
        previous = Current.Value; Current.Value = this;
        run.Value = 1; journal.Mark(2, a: 1);
        all = DiagnosticListener.AllListeners.Subscribe(this);
    }
    internal void NextHost() { journal.NextHost(); run.Value = 2; journal.Mark(2, a: 2); }
    static int Code(string phase) => phase switch
    {
        "replay.approval.before" => 10, "replay.approval.after" => 11,
        "replay.queue.request" => 20, "replay.queue.admitted" => 21,
        "replay.queue.invoke" => 22, "replay.queue.return" => 23,
        "replay.queue.delivered" => 24, "replay.queue.fault" => 25, _ => 0
    };
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name is "Gua.Playtest.RuntimeTiming" or "Gua.Playtest.ObservationSdk")
            listeners.Add(listener.Subscribe(this, phase => ReferenceEquals(Current.Value, this) &&
                (phase == "replay.approval" || phase == "Replay" || Code(phase) != 0)));
    }
    public void OnNext(KeyValuePair<string, object?> notification)
    {
        if (!ReferenceEquals(Current.Value, this)) return;
        try
        {
            int code = Code(notification.Key);
            if (code != 0 && notification.Value is ValueTuple<object, long, long, long?, long?, int, bool> value)
                journal.Mark(code, value.Item3, code == 25 ? value.Item6 : value.Item4 ?? 0,
                    value.Item5 ?? 0, run.Value, value.Item2);
            else if (notification.Key == "Replay" && notification.Value is ValueTuple<long, int, ulong, int> sdk && sdk.Item2 is >= 30 and <= 36)
                journal.Mark(sdk.Item2, unchecked((long)sdk.Item3), sdk.Item4, c: run.Value, at: sdk.Item1);
        }
        catch { Interlocked.Increment(ref errors); }
    }
    internal object Snapshot() => new { observerErrors = Volatile.Read(ref errors), journal = journal.Snapshot() };
    internal bool SaveFailure(string directory, bool failed)
    {
        if (!failed) return false;
        try { File.WriteAllText(Path.Combine(directory, "replay-timing.json"), JsonSerializer.Serialize(Snapshot())); return true; }
        catch { return false; } // Preserve the original test failure/stack, never replace it with diagnostics.
    }
    public void OnCompleted() { }
    public void OnError(Exception error) => Interlocked.Increment(ref errors);
    public void Dispose()
    {
        all.Dispose(); foreach (var listener in listeners) listener.Dispose();
        if (ReferenceEquals(Current.Value, this)) Current.Value = previous;
        journal.Dispose();
    }
}
