using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Gua.Playtest.Core.Contracts;

/// <summary>Pinned offline schemas with serialized access to the evaluator's mutable caches.</summary>
public static class ContractSchemas
{
    private static readonly Assembly Assembly = typeof(ContractSchemas).Assembly;
    private const string Prefix = "Gua.Playtest.Schemas.";
    // JsonSchema.Net 7.3.4 mutates BaseUri/constraint caches during registration/evaluation.
    // A reference graph spans multiple schemas, so per-root locks would not protect its children.
    private static readonly object EvaluationGate = new();
    private static readonly IReadOnlyDictionary<string, JsonSchema> Schemas = Load();
    private static readonly IReadOnlyDictionary<string, string> KindNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["scenario"]="scenario", ["environment"]="environment", ["replayPlan"]="replay-plan",
        ["plannerInput"]="planner-input", ["plannerDecision"]="planner-decision", ["run"]="run",
        ["result"]="result", ["scenarioRegistry"]="scenario-registry", ["adoptionEvidence"]="adoption-evidence"
    };
    public static IReadOnlyList<string> Names { get; } = Schemas.Keys.Order(StringComparer.Ordinal).ToArray();

    public static string ReadSchema(string name)
    {
        var resource = Assembly.GetManifestResourceNames().SingleOrDefault(n => n.StartsWith(Prefix, StringComparison.Ordinal)
            && (n.EndsWith("." + name, StringComparison.Ordinal) || n.EndsWith("/" + name, StringComparison.Ordinal) || n.EndsWith("\\" + name, StringComparison.Ordinal)));
        if (resource is null) throw new ArgumentException("Unknown packaged schema.", nameof(name));
        using var stream = Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void ValidateDocument(JsonObject document)
    {
        var kind = document["kind"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? document["kind"]!.GetValue<string>() : "";
        if (!KindNames.TryGetValue(kind, out var name)) throw new ContractException("SchemaInvalid");
        Validate(name + ".schema.json", Assertions.ValueReader.SchemaDocument(document));
    }

    internal static void Validate(string schemaName, JsonNode document)
    {
        lock (EvaluationGate) Evaluate(Schemas[schemaName], document);
    }

    internal static void ValidateCondition(JsonObject condition)
    {
        lock (EvaluationGate)
        {
            var conditionSchema = JsonSchema.FromText("{\"$ref\":\"https://gua-playtest.dev/schema/common.schema.json#/$defs/condition\"}");
            Evaluate(conditionSchema, condition);
        }
    }

    private static void Evaluate(JsonSchema root, JsonNode document)
    {
        var options = new EvaluationOptions { RequireFormatValidation = true };
        options.SchemaRegistry.Fetch = _ => throw new ContractException("SchemaReferenceMissing");
        foreach (var schema in Schemas.Values) options.SchemaRegistry.Register(schema);
        if (!root.Evaluate(document, options).IsValid) throw new ContractException("SchemaInvalid");
    }

    private static IReadOnlyDictionary<string, JsonSchema> Load()
    {
        var result = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var resource in Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".schema.json", StringComparison.Ordinal)))
        {
            using var stream = Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            // Public filename and schema $id are distinct in upstream Trace schemas.
            var name = resource[Prefix.Length..].Replace('\\', '/').Split('/')[^1];
            result.Add(name, JsonSchema.FromText(json));
        }
        return result;
    }
}
