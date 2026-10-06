using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Gua.Playtest.Core.Contracts;

internal sealed class ContractException(string code) : Exception
{
    public string Code { get; } = code;
}

internal static class ContractDecoder
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxNodes = 50000;

    public static JsonObject Decode(byte[] bytes, string extension)
    {
        if (bytes.Length > MaxBytes) throw new ContractException("DocumentTooLarge");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new ContractException("MalformedDocument"); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            return DecodeYaml(text);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) throw new ContractException("UnsupportedFormat");
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth });
        int count = 0;
        CheckJson(json.RootElement, ref count);
        return JsonNode.Parse(text) as JsonObject ?? throw new ContractException("SchemaInvalid");
    }

    private static void CheckJson(JsonElement node, ref int count)
    {
        if (++count > MaxNodes) throw new ContractException("DocumentTooComplex");
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in node.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new ContractException("DuplicateKey");
                CheckJson(item.Value, ref count);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckJson(child, ref count);
        else if (node.ValueKind == JsonValueKind.Number && (!node.TryGetDouble(out var d) || !double.IsFinite(d)))
            throw new ContractException("NonFiniteNumber");
    }

    private static JsonObject DecodeYaml(string text)
    {
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        int count = 0;
        var node = ReadYaml(parser, 0, ref count);
        parser.Consume<DocumentEnd>();
        if (!parser.TryConsume<StreamEnd>(out _)) throw new ContractException("YamlFeatureForbidden");
        return node as JsonObject ?? throw new ContractException("SchemaInvalid");
    }

    private static JsonNode? ReadYaml(IParser parser, int depth, ref int count)
    {
        if (depth >= MaxDepth || ++count > MaxNodes) throw new ContractException("DocumentTooComplex");
        if (parser.Accept<AnchorAlias>(out _)) throw new ContractException("YamlFeatureForbidden");
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            CheckFeatures(scalar);
            if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(scalar.Value);
            if (scalar.Value == "null") return null;
            if (scalar.Value is "true" or "false") return JsonValue.Create(scalar.Value == "true");
            if (scalar.Value.Equals(".inf", StringComparison.OrdinalIgnoreCase) || scalar.Value.Equals("-.inf", StringComparison.OrdinalIgnoreCase)
                || scalar.Value.Equals("+.inf", StringComparison.OrdinalIgnoreCase) || scalar.Value.Equals(".nan", StringComparison.OrdinalIgnoreCase))
                throw new ContractException("NonFiniteNumber");
            // Only JSON numeric spelling. YAML timestamps, yes/no, octal etc. remain strings.
            if (IsJsonNumber(scalar.Value))
            {
                if (!double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    throw new ContractException("NonFiniteNumber");
                return JsonNode.Parse(scalar.Value);
            }
            return JsonValue.Create(scalar.Value);
        }
        if (parser.TryConsume<MappingStart>(out var mapping))
        {
            CheckFeatures(mapping);
            var result = new JsonObject();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>(); CheckFeatures(key);
                if (key.Value == "<<") throw new ContractException("YamlFeatureForbidden");
                if (key.Style == ScalarStyle.Plain && (key.Value is "true" or "false" or "null" || IsJsonNumber(key.Value)))
                    throw new ContractException("YamlFeatureForbidden");
                if (result.ContainsKey(key.Value)) throw new ContractException("DuplicateKey");
                result[key.Value] = ReadYaml(parser, depth + 1, ref count);
            }
            return result;
        }
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            CheckFeatures(sequence);
            var result = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _)) result.Add(ReadYaml(parser, depth + 1, ref count));
            return result;
        }
        throw new ContractException("MalformedDocument");
    }

    private static void CheckFeatures(NodeEvent node)
    {
        if (!node.Tag.IsEmpty || !node.Anchor.IsEmpty) throw new ContractException("YamlFeatureForbidden");
    }

    private static bool IsJsonNumber(string value)
    {
        // Bounded linear pattern, no caller pattern or regex execution here.
        int i = 0;
        if (i < value.Length && value[i] == '-') i++;
        if (i == value.Length) return false;
        if (value[i] == '0') i++;
        else { if (value[i] < '1' || value[i] > '9') return false; while (i < value.Length && char.IsAsciiDigit(value[i])) i++; }
        if (i < value.Length && value[i] == '.') { i++; int start = i; while (i < value.Length && char.IsAsciiDigit(value[i])) i++; if (i == start) return false; }
        if (i < value.Length && (value[i] is 'e' or 'E')) { i++; if (i < value.Length && (value[i] is '+' or '-')) i++; int start = i; while (i < value.Length && char.IsAsciiDigit(value[i])) i++; if (i == start) return false; }
        return i == value.Length;
    }
}
