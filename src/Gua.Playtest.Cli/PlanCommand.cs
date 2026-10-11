using System.Text.Json;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Cli;

/// <summary>Plan-only command boundary. The composition root supplies its private secret
/// rejection policy and wires this handler without changing shared dispatch here.</summary>
public static class PlanCommand
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output,
        PersistenceRedactor redactor, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args is ["plan", "create", "--managed-root", var managed, "--allow-root", var allowed,
                "--scenario", var scenario, "--recording", var recording, "--timing", var timing])
            {
                var plan = await new PlanManager(managed, redactor).CreateAsync(scenario, recording, [allowed], timing, token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "Candidate", plan }));
                return 0;
            }
            if (args is ["plan", "accept", "--managed-root", var root, "--plan", var path,
                "--trial", var trial, "--approve", "--approve-expectations"])
            {
                await new PlanManager(root, redactor).AcceptAsync(path, trial, true, true, token);
                await output.WriteLineAsync("{\"status\":\"Accepted\"}");
                return 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await output.WriteLineAsync("{\"status\":\"Interrupted\",\"code\":\"PlanInterrupted\"}"); return 4; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { await output.WriteLineAsync("{\"status\":\"Invalid\",\"code\":\"PlanRejected\"}"); return 2; }
        await output.WriteLineAsync("{\"status\":\"Invalid\",\"code\":\"PlanArgumentsRequired\"}");
        return 2;
    }
}
