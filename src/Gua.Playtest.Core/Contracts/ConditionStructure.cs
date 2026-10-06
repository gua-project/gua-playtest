using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;

namespace Gua.Playtest.Core.Contracts;

/// <summary>Composite and target syntax. Comparison leaves must also be eagerly prepared.</summary>
public static class ConditionStructure
{
    /// <summary>Use the same exact wire-integer semantics as comparison Values, without binary64/decimal rounding.</summary>
    public static long ReadNonnegativeInteger(JsonNode value, long maximum)
    {
        try
        {
            if (value.GetValueKind() != System.Text.Json.JsonValueKind.Number) throw new ContractException("ConditionShapeInvalid");
            var number = ValueReader.ExactInteger(value.ToJsonString());
            if (number < 0 || number > maximum) throw new ContractException("ConditionShapeInvalid");
            return number;
        }
        catch (Exception ex) when (ex is ContractException or InvalidOperationException or FormatException or ArgumentException)
        { throw new AssertionConfigurationException(ex is ContractException ce ? ce.Code : "ConditionShapeInvalid"); }
    }

    public static void Validate(JsonObject condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        try
        {
            var shell = Copy(condition, 0);
            ContractSchemas.ValidateCondition(shell);
            ContractSemantics.Validate(new JsonObject { ["initial"] = shell });
        }
        catch (Exception ex) when (ex is ContractException or InvalidOperationException or FormatException or ArgumentException)
        { throw new AssertionConfigurationException(ex is ContractException ce ? ce.Code : "ConditionShapeInvalid"); }
    }

    private static JsonObject Copy(JsonObject node, int depth)
    {
        if (depth > 64) throw new AssertionConfigurationException("ConditionTooDeep");
        var kind = node["kind"]?.GetValue<string>();
        // PreparedAssertion owns complete leaf validation and large set normalization.
        if (kind == "assertion") return new JsonObject { ["kind"] = "targets", ["target"] = new JsonObject { ["source"] = "world" }, ["operator"] = "exists" };
        var copy = new JsonObject();
        foreach (var field in node)
        {
            if (field.Key == "conditions" && kind is "all" or "any" && field.Value is JsonArray children)
            {
                if (children.Count is < 1 or > 100) throw new AssertionConfigurationException("ConditionShapeInvalid");
                copy[field.Key] = new JsonArray(children.Select(child => child is JsonObject map ? (JsonNode)Copy(map, depth + 1)
                    : throw new AssertionConfigurationException("ConditionShapeInvalid")).ToArray());
            }
            else if (field.Key == "condition" && kind == "time" && field.Value is JsonObject child)
                copy[field.Key] = Copy(child, depth + 1);
            else if (field.Key is "count" or "withinMilliseconds" or "forMilliseconds" && field.Value is not null)
                copy[field.Key] = ReadNonnegativeInteger(field.Value, field.Key == "count" ? 9007199254740991 : 86400000);
            else copy[field.Key] = field.Value?.DeepClone();
        }
        return copy;
    }
}
