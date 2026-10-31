using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

internal static class ObservationEvaluationDiagnostics
{
    private static readonly DiagnosticListener Listener = new("Gua.Playtest.ObservationEvaluation");
    internal static void Notify(RunObservation? observation, int phase)
    {
        if (observation is null) return;
        try
        {
            if (Listener.IsEnabled("Evaluation"))
                Listener.Write("Evaluation", (observation, Stopwatch.GetTimestamp(), phase));
        }
        catch { /* Diagnostics never replace evaluation or its original exception. */ }
    }
}
