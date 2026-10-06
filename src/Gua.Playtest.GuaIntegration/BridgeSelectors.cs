using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Numerics;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

internal static class BridgeSelectors
{
    // Re-run the published native selector engine over the revision-bound tree. UI query
    // responses carry no revision, so their IDs alone cannot establish current membership.
    public static bool UiMatchesTree(JsonObject selector, JsonElement tree, IEnumerable<string> remoteIds)
    {
        var nodes = tree.GetProperty("nodes").EnumerateArray().ToArray();
        if (nodes.Select(n => n.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != nodes.Length) return false;
        using var local = new GuaContext();
        local.BeginFrame(tree.GetProperty("screen").GetString()!);
        foreach (var node in nodes)
            local.RegisterNode(new GuaNodeDescriptor(node.GetProperty("id").GetString()!, node.GetProperty("role").GetString()!,
                node.TryGetProperty("label", out var label) ? label.GetString()! : "", new(0, 0, 0, 0),
                Visible: node.GetProperty("visible").GetBoolean(), Enabled: node.GetProperty("enabled").GetBoolean(),
                ParentId: node.TryGetProperty("parentId", out var parent) ? parent.GetString() : null,
                Text: node.TryGetProperty("text", out var text) ? text.GetString() : null));
        local.EndFrame();
        var result = local.Query(Ui(selector));
        var ids = remoteIds.ToArray();
        return result.Valid && ids.Length == ids.Distinct(StringComparer.Ordinal).Count() &&
            ids.ToHashSet(StringComparer.Ordinal).SetEquals(result.Matches.Select(m => m.Id));
    }
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
                JsonValueKind.False => false, JsonValueKind.Number => Number(e), _ => null };
        }
        return new(Text(s, "id"), Match(s, "id"), Text(s, "kind"), Match(s, "kind"), Text(s, "label"), Match(s, "label"),
            Text(s, "tag"), Match(s, "tag"), s["scope"]?["parentId"]?.GetValue<string>(), s["scope"]?["directChild"]?.GetValue<bool>() ?? false,
            Bool(s, "visibleToPlayer"), Bool(s, "active"), s["state"] is null ? null : new(s["state"]!["key"]!.GetValue<string>(), scalar))
        { Near = s["near"] is null ? null : new(s["near"]!["relativeToObjectId"]!.GetValue<string>(), JsonSerializer.SerializeToElement(s["near"]!["maxDistance"]!).GetDouble()),
            Limit = s["limit"] is null ? null : JsonSerializer.SerializeToElement(s["limit"]!).GetUInt32() };
    }
    private static object Number(JsonElement number)
    {
        string token = number.GetRawText();
        double floating = number.GetDouble();
        if (!double.IsFinite(floating))
            throw new InvalidOperationException("World selector number cannot be represented exactly.");
        int exponentAt = token.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? token : token[..exponentAt];
        int dot = mantissa.IndexOf('.');
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        bool negative = digits.StartsWith('-');
        digits = digits.TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return 0L;
        if (exponentAt >= 0 && !int.TryParse(token[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("World selector exponent is out of range.");
        long scale = (exponentAt < 0 ? 0 : int.Parse(token[(exponentAt + 1)..], CultureInfo.InvariantCulture)) - (dot < 0 ? 0L : mantissa.Length - dot - 1);
        int trimmedLength = digits.TrimEnd('0').Length;
        scale += digits.Length - trimmedLength; digits = digits[..trimmedLength];
        if (scale < 0) return floating;
        if (scale > 309) throw new InvalidOperationException("World selector integer is out of range.");
        var integer = BigInteger.Parse((negative ? "-" : "") + digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)scale);
        if (integer >= long.MinValue && integer <= long.MaxValue) return (long)integer;
        if (integer >= 0 && integer <= ulong.MaxValue) return (ulong)integer;
        if (new BigInteger(floating) != integer)
            throw new InvalidOperationException("World selector number cannot be represented exactly.");
        return floating;
    }
}
