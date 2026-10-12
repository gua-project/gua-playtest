using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Host-only redaction policy. Apply before storage buffers, input-copy hashes or Trace queues.
/// Callers must enumerate resolved secrets and sensitive field names; absence is not proof of safety.</summary>
public sealed class PersistenceRedactor
{
    public const int MaxSecretCharacters = 65536;
    private sealed class Node
    {
        internal readonly Dictionary<char, int> Edges = [];
        internal int Failure;
        internal bool Terminal;
    }
    private readonly List<Node> nodes = [new()];
    private readonly HashSet<string> fields;
    private readonly string[] literals;
    public PersistenceRedactor(IEnumerable<string> secrets, IEnumerable<string> sensitiveFields)
        : this(secrets, sensitiveFields, MaxSecretCharacters) { }
    private PersistenceRedactor(IEnumerable<string> secrets, IEnumerable<string> sensitiveFields, int characterLimit)
    {
        var patterns = secrets.Take(1025).ToArray();
        var names = sensitiveFields.Take(1025).ToArray();
        if (patterns.Length > 1024 || names.Length > 1024 ||
            patterns.Any(x => string.IsNullOrEmpty(x) || x.Length > characterLimit) ||
            names.Any(x => string.IsNullOrEmpty(x) || x.Length > 256))
            throw new ArgumentException("RedactionPolicyInvalid");
        if (patterns.Sum(x => (long)x.Length) > characterLimit) throw new ArgumentException("RedactionPolicyInvalid");
        literals = patterns;
        foreach (var pattern in patterns)
        {
            var state = 0;
            foreach (var character in pattern)
            {
                if (!nodes[state].Edges.TryGetValue(character, out var next))
                { next = nodes.Count; nodes[state].Edges.Add(character, next); nodes.Add(new()); }
                state = next;
            }
            nodes[state].Terminal = true;
        }
        var pending = new Queue<int>(nodes[0].Edges.Values);
        while (pending.TryDequeue(out var state))
            foreach (var (character, next) in nodes[state].Edges)
            {
                var failure = nodes[state].Failure;
                while (failure != 0 && !nodes[failure].Edges.ContainsKey(character)) failure = nodes[failure].Failure;
                nodes[next].Failure = nodes[failure].Edges.TryGetValue(character, out var target) ? target : 0;
                nodes[next].Terminal |= nodes[nodes[next].Failure].Terminal;
                pending.Enqueue(next);
            }
        fields = new(names, StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>Refuse literal policies intersecting a provider's conservative generated
    /// token language. Secrets stay private; this does not generate or inspect provider IDs.</summary>
    public bool IsSafeGeneratedTokens(string alphabet, int maxLength, string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(alphabet); ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);
        if (alphabet.Length is < 1 or > 128 || maxLength is < 1 or > 256 || prefix.Length > 256 || suffix.Length > 256)
            throw new ArgumentException("GeneratedTokenLanguageInvalid");
        foreach (var literal in literals)
        {
            if (prefix.Contains(literal, StringComparison.Ordinal) || suffix.Contains(literal, StringComparison.Ordinal)) return false;
            if (literal.Length > prefix.Length + maxLength + suffix.Length) continue;
            for (var split = 0; split <= Math.Min(literal.Length, prefix.Length); split++)
            {
                if (!prefix.AsSpan().EndsWith(literal.AsSpan(0, split), StringComparison.Ordinal)) continue;
                for (var end = 0; end <= Math.Min(literal.Length - split, suffix.Length); end++)
                {
                    if (!suffix.AsSpan().StartsWith(literal.AsSpan(literal.Length - end), StringComparison.Ordinal)) continue;
                    var middle = literal.AsSpan(split, literal.Length - split - end);
                    if (middle.Length > maxLength) continue;
                    var possible = true;
                    foreach (var character in middle)
                        if (!alphabet.Contains(character)) { possible = false; break; }
                    if (possible) return false;
                }
            }
        }
        return true;
    }
    /// <summary>Protect a resolution failure including the single supply that exceeded
    /// the storage ceiling. This returns only a JSON value, never a relaxed storage policy.</summary>
    public static string CreateContentFreeDiagnosticDocument(IReadOnlyList<string> resolvedValues)
    {
        if (resolvedValues.Count > 1024) throw new ArgumentException("RedactionPolicyInvalid");
        long characters = 0;
        foreach (var value in resolvedValues)
        {
            if (characters > MaxSecretCharacters || string.IsNullOrEmpty(value))
                throw new ArgumentException("RedactionPolicyInvalid");
            characters += value.Length;
        }
        // Resolution stops at the first overflow. A safe document from the prefix
        // can have at most prefix trie nodes + 1 characters (65538). A larger last
        // value cannot occur in that document, so omit only that impossible match.
        var relevant = resolvedValues.Where(value => value.Length <= MaxSecretCharacters + 2).ToArray();
        var document = new PersistenceRedactor(relevant, [], 2 * MaxSecretCharacters + 2).CreateContentFreeJsonDocument();
        return resolvedValues.All(value => !document.Contains(value, StringComparison.Ordinal)) ? document : string.Empty;
    }
    public string Redact(string value)
    {
        // Aho-Corasick scans once without per-value allocations. Suppress the whole containing
        // string: partial deletion cannot expose secret tails or re-form secrets at boundaries.
        // Empty output introduces no marker and is idempotent even with overlapping patterns.
        if (nodes.Count == 1) return value;
        var state = 0;
        foreach (var character in value)
        {
            while (state != 0 && !nodes[state].Edges.ContainsKey(character)) state = nodes[state].Failure;
            state = nodes[state].Edges.TryGetValue(character, out var next) ? next : 0;
            if (nodes[state].Terminal) return string.Empty;
        }
        return value;
    }
    /// <summary>One content-free JSON value avoiding all known values, or empty when none can exist.
    /// The search uses only the bounded policy automaton and never exposes its patterns.</summary>
    public string CreateContentFreeJsonDocument()
    {
        foreach (var value in new[] { "{}", "[]", "null", "false", "true", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "\"\"" })
            if (Redact(value) == value) return value;
        if (Redact("\"") == "\"")
        {
            // Each nonempty pattern excluding a bare quotation mark can match at
            // most one of these 1025 distinct one-character JSON strings.
            var tried = 0;
            for (var character = ' '; tried < 1025; character++)
            {
                if (character is '"' or '\\' || char.IsControl(character)) continue;
                tried++;
                var value = "\"" + character + "\"";
                if (Redact(value) == value) return value;
            }
        }
        // Quotes, every digit and the literal candidates are now unavailable.
        // Every possible JSON value must therefore contain a deepest empty
        // object/array, possibly with whitespace. Find the shortest safe one by
        // walking automaton states, rather than an unbounded list of strings.
        char[] alphabet = [' ', '\t', '\r', '\n', '}', ']'];
        var transitions = new int[nodes.Count, alphabet.Length];
        for (var letter = 0; letter < alphabet.Length; letter++)
            transitions[0, letter] = nodes[0].Edges.GetValueOrDefault(alphabet[letter]);
        var breadth = new Queue<int>(nodes[0].Edges.Values);
        while (breadth.TryDequeue(out var state))
        {
            for (var letter = 0; letter < alphabet.Length; letter++)
                transitions[state, letter] = nodes[state].Edges.TryGetValue(alphabet[letter], out var next)
                    ? next : transitions[nodes[state].Failure, letter];
            foreach (var next in nodes[state].Edges.Values) breadth.Enqueue(next);
        }
        var parents = new int[nodes.Count];
        var edges = new char[nodes.Count];
        foreach (var opening in new[] { '{', '[' })
        {
            var start = nodes[0].Edges.GetValueOrDefault(opening);
            if (nodes[start].Terminal) continue;
            Array.Fill(parents, -2); parents[start] = -1;
            var pending = new Queue<int>(); pending.Enqueue(start);
            var closingIndex = opening == '{' ? 4 : 5;
            while (pending.TryDequeue(out var state))
            {
                if (!nodes[transitions[state, closingIndex]].Terminal)
                {
                    var whitespace = new List<char>();
                    for (var current = state; parents[current] != -1; current = parents[current]) whitespace.Add(edges[current]);
                    whitespace.Reverse();
                    return opening + new string(whitespace.ToArray()) + alphabet[closingIndex];
                }
                for (var letter = 0; letter < 4; letter++)
                {
                    var next = transitions[state, letter];
                    if (nodes[next].Terminal || parents[next] != -2) continue;
                    parents[next] = state; edges[next] = alphabet[letter]; pending.Enqueue(next);
                }
            }
        }
        return string.Empty;
    }
    internal bool ContainsAsciiSecret(ReadOnlySpan<byte> token)
    {
        var state = 0;
        foreach (var character in token)
        {
            while (state != 0 && !nodes[state].Edges.ContainsKey((char)character)) state = nodes[state].Failure;
            state = nodes[state].Edges.TryGetValue((char)character, out var next) ? next : 0;
            if (nodes[state].Terminal) return true;
        }
        return false;
    }
    internal bool Sensitive(string name) => fields.Contains(name);
    public bool IsSensitiveField(string name) => Sensitive(name);
    /// <summary>Host-only bounded JSON projection for a foreign writer. Sensitive fields
    /// become null before its buffers/queues/hashes, without changing the original document.</summary>
    public JsonElement RedactDocument(JsonElement document, ArtifactLimits limits)
    {
        if (System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(document).Length > limits.MaxFileBytes)
            throw new ArtifactLimitException();
        var count = 0;
        var safe = Visit(document, 0);
        using var encoding = new BoundedProjection(limits.MaxFileBytes);
        JsonSerializer.Serialize(encoding, safe);
        using var encoded = JsonDocument.Parse(encoding.Bytes);
        var result = encoded.RootElement.Clone();
        if (!IsSafeUtf8(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(result)))
            throw new InvalidDataException("EncodedSecretCollision");
        return result;
        JsonNode? Visit(JsonElement value, int depth)
        {
            if (depth > 32 || ++count > limits.MaxItems) throw new ArtifactLimitException();
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if ((value.TryGetProperty("sensitive", out var marked) && marked.ValueKind == JsonValueKind.True) ||
                        (value.TryGetProperty("mask", out var mask) && mask.ValueKind == JsonValueKind.True))
                    {
                        if (depth == 32 || ++count > limits.MaxItems || "redacted".Length > limits.MaxStringChars)
                            throw new ArtifactLimitException();
                        return new JsonObject { ["redacted"] = Sensitive("redacted") ? null : JsonValue.Create(true) };
                    }
                    var output = new JsonObject();
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Name.Length > limits.MaxStringChars) throw new ArtifactLimitException();
                        var name = Redact(property.Name);
                        if (output.ContainsKey(name)) throw new InvalidDataException("RedactionNameCollision");
                        if (Sensitive(property.Name))
                        {
                            if (depth == 32 || ++count > limits.MaxItems) throw new ArtifactLimitException();
                            output[name] = null;
                        }
                        else output[name] = Visit(property.Value, depth + 1);
                    }
                    return output;
                case JsonValueKind.Array:
                    var array = new JsonArray();
                    foreach (var item in value.EnumerateArray()) array.Add(Visit(item, depth + 1));
                    return array;
                case JsonValueKind.String:
                    var text = value.GetString()!;
                    if (text.Length > limits.MaxStringChars) throw new ArtifactLimitException();
                    return JsonValue.Create(Redact(text));
                case JsonValueKind.True: case JsonValueKind.False: case JsonValueKind.Number: case JsonValueKind.Null:
                    var raw = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value);
                    return IsSafeUtf8(raw) ? JsonNode.Parse(raw) : null;
                default: throw new InvalidDataException("TraceDocumentInvalid");
            }
        }
    }
    private sealed class BoundedProjection(int limit) : Stream
    {
        private readonly MemoryStream memory = new();
        public ReadOnlyMemory<byte> Bytes => memory.GetBuffer().AsMemory(0, checked((int)memory.Length));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => memory.Length;
        public override long Position { get => memory.Length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (buffer.Length > limit - memory.Length) throw new ArtifactLimitException(); memory.Write(buffer); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) memory.Dispose(); base.Dispose(disposing); }
    }
    /// <summary>Recording identity must not be rewritten. Reject a populated sensitive field.</summary>
    public bool HasSensitiveFields(JsonElement document)
    {
        if (document.ValueKind == JsonValueKind.Object)
            foreach (var property in document.EnumerateObject())
                if (Sensitive(property.Name) && property.Value.ValueKind != JsonValueKind.Null || HasSensitiveFields(property.Value)) return true;
        if (document.ValueKind == JsonValueKind.Array)
            foreach (var item in document.EnumerateArray()) if (HasSensitiveFields(item)) return true;
        return false;
    }
    /// <summary>Read-only admission check for an adapter's bounded final UTF-8 encoding.
    /// Reuses the host policy matcher; does not expose secrets or change the supplied bytes.</summary>
    public bool IsSafeUtf8(ReadOnlySpan<byte> bytes) => !ContainsUtf8Secret(bytes);
    internal bool ContainsUtf8Secret(ReadOnlySpan<byte> bytes)
    {
        var state = 0;
        Span<char> characters = stackalloc char[2];
        while (!bytes.IsEmpty)
        {
            int consumed, length;
            if (bytes[0] < 128) { characters[0] = (char)bytes[0]; consumed = length = 1; }
            else
            {
                if (System.Text.Rune.DecodeFromUtf8(bytes, out var rune, out consumed) != System.Buffers.OperationStatus.Done)
                    throw new InvalidDataException("EncodedUtf8Invalid");
                length = rune.EncodeToUtf16(characters);
            }
            for (var index = 0; index < length; index++)
            {
                var character = characters[index];
                while (state != 0 && !nodes[state].Edges.ContainsKey(character)) state = nodes[state].Failure;
                state = nodes[state].Edges.TryGetValue(character, out var next) ? next : 0;
                if (nodes[state].Terminal) return true;
            }
            bytes = bytes[consumed..];
        }
        return false;
    }
}
internal sealed class ArtifactLimitException : Exception;
