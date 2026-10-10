using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gua.Playtest.Core.Contracts;

/// <summary>Wire serialization uses the same names and omitted optional fields as the pinned schemas.</summary>
public static class ContractJson
{
    /// <summary>Bounded JSON/YAML decoding for read-only admission before reference validation.
    /// Decoded is not schema validity, filesystem authority or runtime acceptance.</summary>
    public static (string Code, System.Text.Json.Nodes.JsonObject? Document) Inspect(byte[] bytes, string extension)
    {
        try { return ("Decoded", ContractDecoder.Decode(bytes, extension)); }
        catch (ContractException e) { return (e.Code, null); }
        catch (JsonException) { return ("MalformedDocument", null); }
        catch (YamlDotNet.Core.YamlException) { return ("MalformedDocument", null); }
    }
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
