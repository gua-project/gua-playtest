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
    public PersistenceRedactor(IEnumerable<string> secrets, IEnumerable<string> sensitiveFields)
    {
        var patterns = secrets.Take(1025).ToArray();
        var names = sensitiveFields.Take(1025).ToArray();
        if (patterns.Length > 1024 || names.Length > 1024 ||
            patterns.Any(x => string.IsNullOrEmpty(x) || x.Length > MaxSecretCharacters) ||
            names.Any(x => string.IsNullOrEmpty(x) || x.Length > 256))
            throw new ArgumentException("RedactionPolicyInvalid");
        if (patterns.Sum(x => (long)x.Length) > MaxSecretCharacters) throw new ArgumentException("RedactionPolicyInvalid");
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
    internal bool Sensitive(string name) => fields.Contains(name);
}
internal sealed class ArtifactLimitException : Exception;
