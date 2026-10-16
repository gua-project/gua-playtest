using System.Text;
using System.Text.Json;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Admission for JSON received by Gua adapters, never a Trace encoder.</summary>
internal static class GuaJsonAdmission
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static JsonDocument Parse(string text, int bytes, int nodes, int chars)
    {
        if (Utf8.GetByteCount(text) > bytes) throw new InvalidDataException("GuaJsonByteLimit");
        var encoding = Utf8.GetBytes(text);
        Validate(encoding, nodes, chars);
        return JsonDocument.Parse(encoding, new JsonDocumentOptions { MaxDepth = 33 });
    }
    internal static void Validate(ReadOnlySpan<byte> encoding, int nodes, int chars)
    {
        var reader = new Utf8JsonReader(encoding, new JsonReaderOptions { MaxDepth = 33 });
        var count = 0;
        Span<char> small = stackalloc char[256];
        while (reader.Read())
        {
            if (reader.CurrentDepth > 32) throw new InvalidDataException("GuaJsonDepthLimit");
            if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndArray or JsonTokenType.EndObject) && ++count > nodes)
                throw new InvalidDataException("GuaJsonNodeLimit");
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                var size = Math.Min(reader.ValueSpan.Length, chars);
                Span<char> buffer = size <= 256 ? small : new char[size];
                try { reader.CopyString(buffer[..size]); }
                catch (ArgumentException) { throw new InvalidDataException("GuaJsonStringLimit"); }
            }
        }
    }
}
