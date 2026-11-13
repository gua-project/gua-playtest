using System.Diagnostics;

namespace Gua.Playtest.GuaIntegration;

// Diagnostic notifications carry fixed operation names and monotonic timestamps only.
// They cannot fail, cancel, retry, or change an SDK call's result.
internal static class ObservationCallDiagnostics
{
    private static readonly DiagnosticListener Listener = new("Gua.Playtest.ObservationSdk");
    internal static void Replay(int phase, ulong requestId = 0, int state = -1)
    {
        try
        {
            if (Listener.IsEnabled("Replay"))
                Listener.Write("Replay", (Stopwatch.GetTimestamp(), phase, requestId, state));
        }
        catch { /* Numeric test observers cannot change transport results. */ }
    }
    internal static T Call<T>(string operation, Func<T> call)
    {
        Notify(operation, 0);
        try { var result = call(); Notify(operation, 1); return result; }
        catch { Notify(operation, 2); throw; }
    }
    private static void Notify(string operation, int phase)
    {
        try
        {
            if (Listener.IsEnabled("Call"))
                Listener.Write("Call", (operation, Stopwatch.GetTimestamp(), phase));
        }
        catch { /* Diagnostic consumers have no execution authority. */ }
    }
}
