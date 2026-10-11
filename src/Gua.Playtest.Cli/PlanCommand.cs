using System.Text.Json;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Cli;

/// <summary>Plan-only command boundary. The composition root supplies its private secret
/// rejection policy and wires this handler without changing shared dispatch here.</summary>
public static class PlanCommand
{
    public sealed record InputHashes(string PlanSha256, string ScenarioSha256, string RecordingSha256);
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output,
        PersistenceRedactor redactor, CancellationToken token = default, InputHashes? inputHashes = null,
        Func<string, bool>? responseAllowed = null)
    {
        bool CanReply(string reply) => redactor.Redact(reply) == reply && (responseAllowed?.Invoke(reply) ?? true);
        async Task<int> Failure(string reply, int exit)
        {
            await output.WriteLineAsync(CanReply(reply) ? reply : CommandOutput.PolicyFailureDocument(redactor));
            return exit;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (new[] { "{\"status\":\"Invalid\",\"code\":\"PlanRejected\"}", "{\"status\":\"Interrupted\",\"code\":\"PlanInterrupted\"}",
                "{\"status\":\"Invalid\",\"code\":\"PlanArgumentsRequired\"}" }.Any(reply => !CanReply(reply)))
            { await output.WriteLineAsync(CommandOutput.PolicyFailureDocument(redactor)); return 2; }
            if (args is ["plan", "create", "--managed-root", var managed, "--allow-root", var allowed,
                "--scenario", var scenario, "--recording", var recording, "--timing", var timing])
            {
                var plan = await new PlanManager(managed, redactor).CreateAsync(scenario, recording, [allowed], timing, token,
                    inputHashes?.ScenarioSha256, inputHashes?.RecordingSha256,
                    path => CanReply(JsonSerializer.Serialize(new { status = "Candidate", plan = path })));
                await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "Candidate", plan }));
                return 0;
            }
            if (args is ["plan", "accept", "--managed-root", var root, "--plan", var path,
                "--trial", var trial, "--approve", "--approve-expectations"])
            {
                if (!CanReply("{\"status\":\"Accepted\"}")) { await output.WriteLineAsync(CommandOutput.PolicyFailureDocument(redactor)); return 2; }
                await new PlanManager(root, redactor).AcceptAsync(path, trial, true, true, token, inputHashes?.PlanSha256);
                await output.WriteLineAsync("{\"status\":\"Accepted\"}");
                return 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return await Failure("{\"status\":\"Interrupted\",\"code\":\"PlanInterrupted\"}", 4); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return await Failure("{\"status\":\"Invalid\",\"code\":\"PlanRejected\"}", 2); }
        return await Failure("{\"status\":\"Invalid\",\"code\":\"PlanArgumentsRequired\"}", 2);
    }
}
