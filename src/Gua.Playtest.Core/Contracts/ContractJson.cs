using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gua.Playtest.Core.Contracts;

/// <summary>Wire serialization uses the same names and omitted optional fields as the pinned schemas.</summary>
public static class ContractJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize(ContractDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, document.GetType(), Options);
    }
}
