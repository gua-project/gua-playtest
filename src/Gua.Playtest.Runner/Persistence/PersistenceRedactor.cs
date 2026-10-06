namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Host-only redaction policy. Apply before storage buffers, input-copy hashes or Trace queues.
/// Callers must enumerate resolved secrets and sensitive field names; absence is not proof of safety.</summary>
public sealed class PersistenceRedactor
{
    private readonly string[] secrets;
    private readonly HashSet<string> fields;
    public PersistenceRedactor(IEnumerable<string> secrets, IEnumerable<string> sensitiveFields)
    {
        this.secrets = secrets.Take(1025).ToArray();
        var names = sensitiveFields.Take(1025).ToArray();
        if (this.secrets.Length > 1024 || names.Length > 1024 ||
            this.secrets.Any(x => string.IsNullOrEmpty(x) || x.Length > 65536) ||
            names.Any(x => string.IsNullOrEmpty(x) || x.Length > 256))
            throw new ArgumentException("RedactionPolicyInvalid");
        // Longest first prevents a shorter secret from exposing the tail of an overlapping value.
        this.secrets = this.secrets.OrderByDescending(x => x.Length).ToArray();
        fields = new(names, StringComparer.OrdinalIgnoreCase);
    }
    public string Redact(string value)
    {
        // Deletion introduces no replacement text containing a secret. Repeat to remove values
        // formed by concatenation and make sanitization idempotent across nested fixed copies.
        bool changed;
        do
        {
            var before = value;
            foreach (var secret in secrets) value = value.Replace(secret, "", StringComparison.Ordinal);
            changed = before != value;
        } while (changed);
        return value;
    }
    internal bool Sensitive(string name) => fields.Contains(name);
}
internal sealed class ArtifactLimitException : Exception;
