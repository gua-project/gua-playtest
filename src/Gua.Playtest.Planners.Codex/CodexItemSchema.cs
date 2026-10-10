using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Gua.Playtest.Planners.Codex;

// Derived from rust-v0.150.1 ThreadStartResponse.json,
// ItemCompletedNotification.json and ErrorNotification.json (TurnError),
// including reachable references.
// Existing derived schemas remove annotations; additional telemetry schemas are copied unchanged. Apache-2.0.
internal static class CodexItemSchema
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, JsonSchema> StartupSchemas = new(StringComparer.Ordinal)
    {
        ["deprecationNotice"] = Load("DeprecationNoticeNotification"),
        ["guardianWarning"] = Load("GuardianWarningNotification"),
        ["configWarning"] = Load("ConfigWarningNotification")
    };
    public static bool IsStartupNotice(string? method) => method is not null && StartupSchemas.ContainsKey(method);
    public static bool ValidStartupNotice(string method, JsonElement parameters) => Evaluate(StartupSchemas[method], parameters);
    private static readonly Dictionary<string, JsonSchema> TelemetrySchemas = new(StringComparer.Ordinal)
    {
        ["rawResponse/completed"] = Load("RawResponseCompletedNotification"),
        ["rawResponseItem/completed"] = Load("RawResponseItemCompletedNotification"),
        ["model/verification"] = Load("ModelVerificationNotification"),
        ["model/safetyBuffering/updated"] = Load("ModelSafetyBufferingUpdatedNotification")
    };
    public static bool IsTelemetry(string? method) => method is not null && TelemetrySchemas.ContainsKey(method);
    public static bool ValidTelemetry(string method, JsonElement parameters) => Evaluate(TelemetrySchemas[method], parameters);
    private static readonly JsonSchema Schema = Load("ThreadItem");
    private static readonly JsonSchema ErrorSchema = Load("TurnError");
    private static readonly JsonSchema ThreadStartSchema = Load("ThreadStartResponse");
    private static readonly JsonSchema ThreadSchema = Load("ThreadStartResponse", "Thread");
    private static readonly JsonSchema ItemCompletedSchema = Load("ItemCompleted");
    private static JsonSchema Load(string name, string? definition = null)
    {
        using var stream = typeof(CodexItemSchema).Assembly.GetManifestResourceStream("Gua.Playtest.Codex." + name)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (definition is not null)
        {
            var source = JsonNode.Parse(json)!;
            json = new JsonObject { ["$schema"] = "http://json-schema.org/draft-07/schema#",
                ["$ref"] = "#/definitions/" + definition, ["definitions"] = source["definitions"]!.DeepClone() }.ToJsonString();
        }
        return JsonSchema.FromText(json);
    }
    public static bool Valid(JsonElement item) => Evaluate(Schema, item);
    public static bool ValidThreadStart(JsonElement response) => Evaluate(ThreadStartSchema, response);
    public static bool ValidThread(JsonElement thread) => Evaluate(ThreadSchema, thread);
    public static bool ValidItemCompleted(JsonElement parameters) => Evaluate(ItemCompletedSchema, parameters);
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
