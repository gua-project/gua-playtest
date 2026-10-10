using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Gua.Playtest.Planners.Codex;

// Derived from rust-v0.150.1 ThreadStartResponse.json (ThreadItem) and
// ErrorNotification.json (TurnError), including reachable references.
// Only annotations were removed. Apache-2.0.
internal static class CodexItemSchema
{
    private static readonly object Gate = new();
    private static readonly JsonSchema Schema = Load("ThreadItem");
    private static readonly JsonSchema ErrorSchema = Load("TurnError");
    private static JsonSchema Load(string name)
    {
        using var stream = typeof(CodexItemSchema).Assembly.GetManifestResourceStream("Gua.Playtest.Codex." + name)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
    public static bool Valid(JsonElement item) => Evaluate(Schema, item);
    public static bool ValidError(JsonElement error)
    {
        if (!Evaluate(ErrorSchema, error)) return false;
        if (error.TryGetProperty("codexErrorInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            foreach (var variant in info.EnumerateObject())
                if (variant.Value.TryGetProperty("httpStatusCode", out var code) && code.ValueKind != JsonValueKind.Null &&
                    (!code.TryGetInt32(out var number) || number is < 0 or > ushort.MaxValue)) return false;
        return true;
    }
    private static bool Evaluate(JsonSchema schema, JsonElement item)
    {
        lock (Gate)
        {
            var options = new EvaluationOptions { RequireFormatValidation = true };
            options.SchemaRegistry.Fetch = _ => throw new InvalidOperationException("CodexSchemaReferenceMissing");
            return schema.Evaluate(JsonNode.Parse(item.GetRawText()), options).IsValid;
        }
    }
}
