using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

// Optional diagnostics only. Observers cannot supply authority or replace an exception.
internal static class RuntimeTiming
{
    private static readonly DiagnosticListener? Listener = CreateListener();
    private static long nextOperation;

    private static DiagnosticListener? CreateListener()
    {
        try { return new("Gua.Playtest.RuntimeTiming"); }
        catch { return null; }
    }

    internal static long NextOperation() => Interlocked.Increment(ref nextOperation);

    internal static void Mark(object owner, string phase, long operation = 0,
        long? budgetTicks = null, long? remainingTicks = null, int detail = -1)
    {
        try
        {
            if (Listener?.IsEnabled(phase) == true)
                Listener.Write(phase, (owner, Stopwatch.GetTimestamp(), operation,
                    budgetTicks, remainingTicks, detail, Thread.CurrentThread.IsThreadPoolThread));
        }
        catch { /* Diagnostic delivery must not change execution. */ }
    }

    // This is the observer's delivery time, not the task's transition or timer fire time.
    internal static void ObserveCompletion(object owner, string phase, long operation, Task task)
    {
        try
        {
            if (Listener?.IsEnabled(phase) != true) return;
            var reference = new WeakReference<object>(owner);
            _ = task.ContinueWith(completed =>
            {
                if (reference.TryGetTarget(out var target)) Mark(target, phase, operation, detail: (int)completed.Status);
            },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { /* Diagnostics do not affect the original task or its exception. */ }
    }
}
