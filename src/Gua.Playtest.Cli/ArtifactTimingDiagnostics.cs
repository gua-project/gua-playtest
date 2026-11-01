using System.Diagnostics;

namespace Gua.Playtest.Cli;

// Optional numeric diagnostics. No artifact content, path, or exception text is emitted.
internal static class ArtifactTimingDiagnostics
{
    private static readonly DiagnosticListener? Listener = CreateListener();
    private static long nextOperation;

    private static DiagnosticListener? CreateListener()
    {
        try { return new("Gua.Playtest.ArtifactTiming"); }
        catch { return null; }
    }

    // Separate from the Runner's positive operation IDs.
    internal static long NextOperation() => Interlocked.Decrement(ref nextOperation);

    internal static void Mark(object owner, string phase, long operation = 0,
        long? budgetTicks = null, long? remainingTicks = null, int detail = -1)
    {
        try
        {
            if (Listener?.IsEnabled(phase) == true)
                Listener.Write(phase, (owner, Stopwatch.GetTimestamp(), operation,
                    budgetTicks, remainingTicks, detail, Thread.CurrentThread.IsThreadPoolThread));
        }
        catch { /* Diagnostic delivery has no persistence or Run authority. */ }
    }
}
