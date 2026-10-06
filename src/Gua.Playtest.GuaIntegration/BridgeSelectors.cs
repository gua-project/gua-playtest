using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

internal static class BridgeSelectors
{
    private static string? Text(JsonObject s, string key) => s[key]?["value"]?.GetValue<string>();
    private static GuaMatchMode Match(JsonObject s, string key) => s[key]?["match"]?.GetValue<string>() switch
    { "contains" => GuaMatchMode.Contains, "regex" => GuaMatchMode.Regex, _ => GuaMatchMode.Exact };
    private static bool? Bool(JsonObject s, string key) => s[key]?.GetValue<bool>();
    private static GuaStateFilter Filter(bool? b) => b is null ? GuaStateFilter.Any : b.Value ? GuaStateFilter.True : GuaStateFilter.False;
    public static GuaSelector Ui(JsonObject s)
    {
        if (!GuaDistribution.ValidateJson("selector.schema.json", s.ToJsonString())) throw new InvalidOperationException("Invalid selector.");
        return new(Text(s, "id"), Match(s, "id"), Text(s, "role"), Match(s, "role"), Text(s, "name"), Match(s, "name"),
            Text(s, "text"), Match(s, "text"), s["scope"]?["parentId"]?.GetValue<string>(), s["scope"]?["directChild"]?.GetValue<bool>() ?? false,
            Filter(Bool(s, "visible")), Filter(Bool(s, "enabled")));
    }
    public static GuaWorldSelector World(JsonObject s)
    {
        if (!GuaDistribution.ValidateJson("world-selector.schema.json", s.ToJsonString())) throw new InvalidOperationException("Invalid selector.");
        object? scalar = null;
        if (s["state"]?["value"] is { } v)
        {
            var e = JsonSerializer.SerializeToElement(v);
            scalar = e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.True => true,
                JsonValueKind.False => false, JsonValueKind.Number => e.GetDouble(), _ => null };
        }
        return new(Text(s, "id"), Match(s, "id"), Text(s, "kind"), Match(s, "kind"), Text(s, "label"), Match(s, "label"),
            Text(s, "tag"), Match(s, "tag"), s["scope"]?["parentId"]?.GetValue<string>(), s["scope"]?["directChild"]?.GetValue<bool>() ?? false,
            Bool(s, "visibleToPlayer"), Bool(s, "active"), s["state"] is null ? null : new(s["state"]!["key"]!.GetValue<string>(), scalar))
        { Near = s["near"] is null ? null : new(s["near"]!["relativeToObjectId"]!.GetValue<string>(), s["near"]!["maxDistance"]!.GetValue<double>()),
            Limit = s["limit"]?.GetValue<uint>() };
    }
}
