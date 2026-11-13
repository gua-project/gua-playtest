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

    internal static bool Enabled(string phase)
    {
        try { return Listener?.IsEnabled(phase) == true; }
        catch { return false; }
    }

    internal static void MarkAt(object owner, string phase, long ticks, long? budgetTicks, long? remainingTicks)
    {
        try
        {
            if (Listener?.IsEnabled(phase) == true)
                Listener.Write(phase, (owner, ticks, 0L, budgetTicks, remainingTicks, -1, Thread.CurrentThread.IsThreadPoolThread));
        }
        catch { /* Diagnostics cannot change authority or provider behavior. */ }
    }

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

    // Publish references only; failure observers sample Status without waiting or
    // scheduling another continuation. Task state reads are independent, not atomic.
    internal static void TrackWait(object owner, string phase, long operation,
        Task timer, Task ownerTask, Task whenAny)
    {
        try
        {
            if (Listener?.IsEnabled(phase) == true)
                Listener.Write(phase, (owner, Stopwatch.GetTimestamp(), operation, timer, ownerTask, whenAny));
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
