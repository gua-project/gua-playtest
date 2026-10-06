using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Gua.Playtest.Core.Contracts;

/// <summary>Immutable pinned schema set. No URI retrieval or native runtime use.</summary>
public static class ContractSchemas
{
    private static readonly Assembly Assembly = typeof(ContractSchemas).Assembly;
    private const string Prefix = "Gua.Playtest.Schemas.";
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
        Validate(name + ".schema.json", document);
    }

    internal static void Validate(string schemaName, JsonNode document)
    {
        var options = new EvaluationOptions { RequireFormatValidation = true };
        options.SchemaRegistry.Fetch = _ => throw new ContractException("SchemaReferenceMissing");
        foreach (var schema in Schemas.Values) options.SchemaRegistry.Register(schema);
        if (!Schemas[schemaName].Evaluate(document, options).IsValid) throw new ContractException("SchemaInvalid");
    }

    private static IReadOnlyDictionary<string, JsonSchema> Load()
    {
        var result = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var resource in Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".schema.json", StringComparison.Ordinal)))
        {
            using var stream = Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var id = JsonNode.Parse(json)!["$id"]!.GetValue<string>();
            var name = new Uri(id).Segments[^1];
            result.Add(name, JsonSchema.FromText(json));
        }
        return result;
    }
}
