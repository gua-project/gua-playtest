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
        // Never disclose a known value when no safe candidate can be represented.
        return redactor.CreateContentFreeJsonDocument();
    }
    internal static async Task<int> WriteKnownFailureAsync(TextWriter output, TextWriter diagnostics, bool json,
        string status, string code, int exit, IReadOnlyList<string> secrets)
    {
        // Policy construction itself may fail (including its size ceiling). Checking the
        // finite diagnostic document directly still protects every already-resolved value.
        bool Allowed(string value) => secrets.All(secret => !value.Contains(secret, StringComparison.Ordinal));
        var document = Serialize(status, code);
        if (!Allowed(document))
        {
            document = PersistenceRedactor.CreateContentFreeDiagnosticDocument(secrets);
        }
        var text = $"{status}: {code}";
        if (exit != 0 && Allowed(code)) await diagnostics.WriteAsync(code);
        await output.WriteAsync(json ? document : Allowed(text) ? text : string.Empty);
        return exit;
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
