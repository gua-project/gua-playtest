using System.Text.Json;

namespace Gua.Playtest.Cli;

internal static class CommandOutput
{
    internal static async Task<int> WriteAsync(TextWriter output, TextWriter diagnostics, bool json,
        string status, string code, int exit, object? details = null)
    {
        if (exit != 0) await diagnostics.WriteLineAsync(code);
        await output.WriteLineAsync(json
            ? JsonSerializer.Serialize(new { status, code, details }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : $"{status}: {code}");
        return exit;
    }
}
