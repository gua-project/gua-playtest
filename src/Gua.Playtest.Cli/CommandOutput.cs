using System.Text.Json;
using Gua.Playtest.Runner.Artifacts;

namespace Gua.Playtest.Cli;

internal static class CommandOutput
{
    internal static string Serialize(string status, string code, object? details = null)
        => JsonSerializer.Serialize(new { status, code, details }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    internal static string PolicyFailureDocument(PersistenceRedactor redactor)
    {
        var result = Serialize(redactor.Redact("Invalid"), redactor.Redact("PlanSecretCollision"));
        if (redactor.Redact(result) == result) return result;
        // If a credential collides with protocol field names, use a content-free document.
        // An unrepresentable policy must remain silent rather than disclose any known value.
        return new[] { "{}", "[]", "null", "false", "true", "0", "1", "2" }
            .FirstOrDefault(value => redactor.Redact(value) == value) ?? string.Empty;
    }
    internal static async Task<int> WriteAsync(TextWriter output, TextWriter diagnostics, bool json,
        string status, string code, int exit, object? details = null)
    {
        if (exit != 0) await diagnostics.WriteLineAsync(code);
        await output.WriteLineAsync(json
            ? Serialize(status, code, details)
            : $"{status}: {code}");
        return exit;
    }
}
