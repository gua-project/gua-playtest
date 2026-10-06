using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Core.Assertions;

internal sealed record ValueTypeIdentity(string Kind, string ElementKind, string EnumType)
{
    public bool IsCollection => Kind is "list" or "set";
    public static ValueTypeIdentity Read(JsonNode node) => new(ContractSemantics.Text(node, "type"), ContractSemantics.Text(node, "elementType"), ContractSemantics.Text(node, "enumType"));
}

internal sealed record AssertionValue(ValueTypeIdentity Type, object[] Items)
{
    public object Scalar => Items[0];
}

/// <summary>Reads the existing Gua wire contract without native dependencies or lossy integer coercion.</summary>
internal static class ValueReader
{
    internal static bool IsValueException(Exception ex) => ex is ContractException or JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException;

    public static AssertionValue Read(JsonObject node, EnumCatalogSnapshot? catalog)
    {
        var type = ValueTypeIdentity.Read(node);
        if (type.Kind == "enum" || (type.IsCollection && type.ElementKind == "enum"))
        {
            if (type.EnumType.Length == 0) throw new ContractException("EnumTypeInvalid");
            if (catalog is null) throw new ContractException("EnumUnknown");
            catalog.RequireType(type.EnumType); // including empty enum collections
        }
        if (node["value"] is null || (type.IsCollection && node["value"] is not JsonArray)) throw new ContractException("ValueShapeInvalid");
        var items = type.IsCollection
            ? node["value"]!.AsArray().Select(item => item is null ? throw new ContractException("ValueShapeInvalid") : Scalar(item, type.ElementKind, type.EnumType, catalog)).ToArray()
            : [Scalar(node["value"]!, type.Kind, type.EnumType, catalog)];
        var value = new AssertionValue(type, items);
        if (type.Kind == "set" && items.Distinct().Count() != items.Length) throw new ContractException("SetDuplicate");
        ContractSchemas.Validate("value-v1.schema.json", SchemaValue(node, value));
        return value;
    }

    internal static JsonObject SchemaValue(JsonObject wire, AssertionValue value)
    {
        // JsonSchema.Net 7 uses decimal and cannot read valid Gua binary64 values such as 1e308.
        // Scalar/element semantics and set uniqueness were already checked above. Preserve all
        // fields/type metadata. Gua collections have no minimum length: an empty structural copy
        // avoids the schema library's quadratic uniqueItems scan without skipping element checks.
        var result = wire.DeepClone().AsObject();
        if (value.Type.IsCollection) result["value"] = new JsonArray();
        else if (value.Type.Kind == "number") result["value"] = JsonValue.Create(0d);
        else if (value.Type.Kind == "integer") result["value"] = JsonValue.Create((long)value.Scalar);
        return result;
    }

    internal static JsonObject SchemaDocument(JsonObject document)
    {
        var result = document.DeepClone().AsObject();
        Normalize(result);
        return result;

        static void Normalize(JsonNode node)
        {
            if (node is JsonObject map)
            {
                var type = ValueTypeIdentity.Read(map);
                if (map.ContainsKey("value") && (type.Kind is "number" or "integer" || (type.IsCollection && type.ElementKind is "number" or "integer")))
                {
                    var value = Read(map, null);
                    map["value"] = SchemaValue(map, value)["value"]!.DeepClone();
                }
                if (ContractSemantics.Text(map, "kind") == "assertion" && map["tolerance"] is { } tolerance)
                {
                    if (tolerance.GetValueKind() != JsonValueKind.Number) throw new ContractException("ToleranceInvalid");
                    double number = double.Parse(tolerance.ToJsonString(), CultureInfo.InvariantCulture);
                    if (!double.IsFinite(number) || number < 0) throw new ContractException("ToleranceInvalid");
                    map["tolerance"] = 0;
                }
                foreach (var item in map) if (item.Value is not null) Normalize(item.Value);
            }
            else if (node is JsonArray array) foreach (var item in array) if (item is not null) Normalize(item);
        }
    }

    private static object Scalar(JsonNode node, string kind, string enumType, EnumCatalogSnapshot? catalog)
    {
        switch (kind)
        {
            case "bool": return node.GetValue<bool>();
            case "integer": return ExactInteger(node.ToJsonString());
            case "number":
                if (node.GetValueKind() != JsonValueKind.Number) throw new ContractException("ValueTypeInvalid");
                var number = double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new ContractException("NonFiniteNumber");
                return number == 0 ? 0d : number;
            case "string":
            case "enum":
                var text = node.GetValue<string>();
                CheckUnicode(text);
                if (kind == "enum")
                {
                    if (enumType.Length == 0) throw new ContractException("EnumTypeInvalid");
                    if (catalog is null) throw new ContractException("EnumUnknown");
                    catalog.RequireMember(enumType, text);
                }
                return text;
            default: throw new ContractException("ValueTypeInvalid");
        }
    }

    internal static long ExactInteger(string token)
    {
        // Decide integrality on decimal digits before binary64 rounding. Bound work by token length.
        var negative = token.StartsWith('-');
        var unsigned = negative ? token[1..] : token;
        int exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var exponentText = exponentIndex < 0 ? "0" : unsigned[(exponentIndex + 1)..];
        int dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return 0;
        if (!int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)) throw new ContractException("IntegerRange");
        long scale = (long)exponent - (dot < 0 ? 0 : mantissa.Length - dot - 1);
        int trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (scale < -trailingZeros) throw new ContractException("IntegerRange");
        if (scale < 0) digits = digits[..(digits.Length + (int)scale)];
        else
        {
            if (digits.Length + scale > 16) throw new ContractException("IntegerRange");
            digits += new string('0', (int)scale);
        }
        if (digits.Length > 16 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 9007199254740991L)
            throw new ContractException("IntegerRange");
        return negative ? -value : value;
    }

    internal static void CheckUnicode(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])) throw new ContractException("UnicodeInvalid");
        }
    }
}
