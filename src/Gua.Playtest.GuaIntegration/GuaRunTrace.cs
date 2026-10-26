using System.Text.Json;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Host-policy port to the public Gua writer. Every operation delegates to Gua;
/// no alternate Trace format or raw session bypass is exposed to the composition owner.</summary>
public sealed class GuaRunTrace
{
    // Pinned 1.1.1 native event/quality vocabulary; admission only, never encoding.
    private static readonly string[] NativeVocabulary =
    [
        "step.begin", "step.end", "request.correlated", "request.phase", "request.sending", "request.enqueue", "request.completion",
        "action.preparing", "selector.resolved", "caller.failure", "caller.result", "assertion.result", "assertion.evaluation",
        "observation", "observation.change", "annotation", "attachment", "capture.failure", "spatial.received", "spatial.storage",
        "invalid-request", "correlation-limit", "request-outside-correlation-window", "step-outside-retention", "invalid-event-type",
        "invalid-step-outcome", "uncertain-step-outcome", "snapshot-limit", "attachment-limit", "event-limit", "artifact-limit",
        "queue-byte-limit", "queue-limit", "retained-byte-limit", "capture-failed", "flush-timeout", "flush-pending", "flush-failed",
        "unfinished-steps", "write-failed", "tree-failed", "tree-stale", "profile-mismatch", "attachment-invalid", "attachment-unavailable",
        "attachment-failed", "native-capture-failed", "native-correlation-unavailable", "native-lifecycle-gap", "native-lifecycle-not-provided",
        "native-request-outside-correlation-window", "native-source-changed", "native-source-limit", "native-stop-timeout",
        "observe-storage-gap", "observe-unsubscribe-failed", "screenshot-unavailable", "spatial-storage-omitted",
        "owner-disconnected", "release-requested", "late-completion", "unavailable-or-not-authorized", "subscription-start",
        "received-live-result", "caller-authorized", "available", "stale", "unverified", "[redacted]", "redacted",
        "observation-partial", "observation-gap", "observation-stale", "observation-failed", "observation-outsideRetention"
    ];
    private readonly GuaTraceSession session;
    private readonly Func<JsonElement, JsonElement> redact;
    private readonly Func<string, bool> sensitiveField;
    private readonly Func<string, string> redactText;
    private readonly int maxStringChars, maxFileBytes, maxNodes;
    private int failed;
    private string? publishedPath;
    internal GuaTraceSession Session => session;
    internal bool CaptureFailed => Volatile.Read(ref failed) != 0;
    public GuaTraceStatus Status => session.Status;
    public string ArtifactPath => publishedPath ?? session.ArtifactPath;
    internal void Published(string path) => publishedPath = path;
    public string TraceId => session.TraceId;
    internal GuaRunTrace(GuaTraceSession session, Func<JsonElement, JsonElement> redact,
        Func<string, bool> sensitiveField, Func<string, string> redactText, int maxStringChars, int maxFileBytes, int maxNodes)
    { this.session = session; this.redact = redact; this.sensitiveField = sensitiveField; this.redactText = redactText;
        this.maxStringChars = maxStringChars; this.maxFileBytes = maxFileBytes; this.maxNodes = maxNodes; }
    internal static void ValidatePolicy(Func<string, bool> sensitiveField, Func<string, bool> safeLiteral,
        Func<string, int, string, string, bool> safeGeneratedTokens, Func<string, bool> safeCompositionBoundary)
    {
        // 1.1.1 creates these envelope values internally, beyond public payload gates.
        // Never sample random IDs/current clocks as proof for a future event. Refuse
        // intersecting conservative token languages before its session/worker exists.
        if (!safeGeneratedTokens("0123456789abcdef", 64, "", "") ||
            !safeGeneratedTokens("0123456789abcdef", 32, "collector-monotonic:", "") ||
            !safeGeneratedTokens("0123456789-+.eETZ:", 64, "", "") ||
            !safeGeneratedTokens("0123456789abcdef", 64, "snapshots/", ".json") ||
            !safeGeneratedTokens("0123456789abcdef", 64, "attachments/", ".json"))
            throw new ArgumentException("GuaTraceGeneratedEnvelopeUnsupported");
        // These Gua-owned envelope/identity fields cannot be removed through 1.1.1's
        // public writer. Reject that policy before starting rather than bypassing it.
        var names = new[] { typeof(GuaTraceEvent), typeof(GuaTraceManifest), typeof(GuaTraceStatus),
            typeof(GuaTraceHost), typeof(GuaTraceRequest) }.SelectMany(type => type.GetProperties())
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));
        var envelopeNames = names.Concat(["request", "schema", "blob", "observationId", "channel", "availability", "host", "continuity", "metadata", "kind", "outcome"])
            .Distinct(StringComparer.Ordinal).ToArray();
        if (envelopeNames.Any(sensitiveField)) throw new ArgumentException("GuaTraceSensitiveEnvelopeUnsupported");
        // Unlike payload JSON, the pinned native envelope is not redacted by Gua.
        // Check both decoded names and fixed encoded key/value/schema tokens before
        // constructing its session/worker; late report rejection cannot undo a write.
        // Public native methods construct these payload keys internally. Literal
        // key collisions cannot be corrected by sanitizing only their input values.
        var nativePayloadNames = new[] { "label", "parentStepId", "source", "file", "line", "name", "value",
            "target", "operator", "expected", "actual", "truth", "role", "observations", "callerOutcome",
            "before", "after", "change", "reason", "phase", "result", "content", "references" };
        // Inspect overlaps, not just each syntax token and caller value separately.
        // Gua assembles these bindings after its public methods receive the values;
        // there is no complete-event admission hook in the pinned public API.
        foreach (var name in envelopeNames.Concat(nativePayloadNames).Distinct(StringComparer.Ordinal))
        {
            var key = JsonSerializer.Serialize(name);
            if (new[] { key + ":", key + ": ", key + ":\"", key + ": \"" }.Any(boundary => !safeCompositionBoundary(boundary)))
                throw new ArgumentException("GuaTraceCompositionBoundaryUnsupported");
        }
        var fixedTokens = envelopeNames.Concat(nativePayloadNames).Concat(["recent", "streaming", "onFailure", "always", "debug", "player",
            "unknown", "passed", "failed", "interrupted", "collector-monotonic:", "true", "false", "0", "1",
            "index", "recording", "steps", "gua.trace.recording.v1", "gua.recording.references.v1"])
            .Concat(Enum.GetNames<GuaTraceStepKind>().Select(JsonNamingPolicy.CamelCase.ConvertName)).Concat(NativeVocabulary);
        // Native JSON syntax is unavoidable even when no caller payload is retained.
        var delimiters = new[] { "{", "}", "[", "]", ",", ":", "\"", "\r", "\n", "\t", " " };
        if (delimiters.Any(boundary => !safeCompositionBoundary(boundary)))
            throw new ArgumentException("GuaTraceCompositionBoundaryUnsupported");
        if (delimiters.Any(token => !safeLiteral(token)) ||
            fixedTokens.Any(token => !safeLiteral(token) || !safeLiteral(JsonSerializer.Serialize(token))) ||
            envelopeNames.Concat(nativePayloadNames).Any(name => !safeLiteral(JsonSerializer.Serialize(name) + ":\"")))
            throw new ArgumentException("GuaTraceLiteralEnvelopeUnsupported");
    }
    private string Bound(string value)
    { if (value.Length > maxStringChars) throw new InvalidDataException("TraceStringLimit"); return value; }
    private string Identity(string value)
    { Bound(value); if (redactText(value) != value || UnsafeEncoding(value)) throw new InvalidDataException("GuaTraceIdentitySecret"); return value; }
    private bool UnsafeEncoding(string value)
    { var encoding = JsonSerializer.Serialize(value); return redactText(encoding) != encoding; }
    private string? Optional(string? value) => value is null ? null : Identity(value);
    private string Text(string name, string value)
    { Bound(value); var safe = sensitiveField(name) ? string.Empty : redactText(value); return UnsafeEncoding(safe) ? string.Empty : safe; }
    private int? SourceLine(int? value)
    {
        if (sensitiveField("line") || sensitiveField("sourceLine")) return null;
        if (value is not null) Identity(JsonSerializer.Serialize(value.Value));
        return value;
    }
    private GuaTraceRequest Request(GuaTraceRequest value)
    { Identity(value.SourceId); Identity(value.SessionEpoch); Identity(value.RequestId); return value; }
    private GuaTraceHost Host(GuaTraceHost value)
    {
        Identity(value.SourceId); Identity(value.SessionEpoch); Optional(value.Frame); Optional(value.Revision);
        Optional(value.Timestamp); Optional(value.ClockId); return value;
    }
    private IReadOnlyList<string> References(IReadOnlyList<string> values)
    { foreach (var value in values) Identity(value); return values; }
    private T Capture<T>(Func<T> operation, T missing)
    {
        try { return operation(); }
        catch { Interlocked.Exchange(ref failed, 1); return missing; }
    }
    private bool Capture(Func<bool> operation)
    {
        var accepted = Capture(operation, false);
        if (!accepted) Interlocked.Exchange(ref failed, 1);
        return accepted;
    }
    public string BeginStep(GuaTraceStepKind kind, string label, GuaTraceRequest? request = null,
        string? parentStepId = null, string? sourceFile = null, int? sourceLine = null)
        => Capture(() => session.BeginStep(kind, Text("label", label), request is null ? null : Request(request),
            sensitiveField("parentStepId") ? null : Optional(parentStepId),
            sourceFile is null || new[] { "source", "file", "sourceFile" }.Any(sensitiveField) ? null : Text("sourceFile", sourceFile),
            SourceLine(sourceLine)), string.Empty);
    public string Mark(string label) => Capture(() => session.Mark(Text("label", label)), string.Empty);
    public bool Record(string step, string type, JsonElement data, bool sensitive = false)
        => Capture(() => session.Record(step, Text("type", type), sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(data), sensitive));
    public bool RecordStructural(string step, string type, JsonElement data)
        => Capture(() =>
        {
            var safe = redact(data);
            if (!JsonElement.DeepEquals(safe, data)) return false;
            return session.Record(step, Identity(type), safe);
        });
    public bool RecordRequest(GuaTraceRequest request, string phase, JsonElement data)
        => Capture(() => session.RecordRequest(Request(request), Text("phase", phase),
            sensitiveField("result") ? JsonSerializer.SerializeToElement<object?>(null) : redact(data)));
    public void EndStep(string step, GuaTraceOutcome outcome) => session.EndStep(step, outcome);
    public bool Correlate(string step, GuaTraceRequest request) => Capture(() => session.Correlate(step, Request(request)));
    public bool Attach(string step, string schema, JsonElement content, bool sensitive = false)
        => Capture(() => session.Attach(step, Identity(schema),
            sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(content), sensitive));
    public string Observe(string step, string channel, string reason, string availability, GuaTraceHost host,
        JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Observe(step, Identity(channel), Text("reason", reason), Identity(availability), Host(host),
            sensitive || snapshot is null ? null : redact(snapshot.Value), Identity(continuity), sensitive), string.Empty);
    private bool MaskedValue(GuaValue? value)
    {
        if (value is null) return false;
        using var document = GuaJsonAdmission.Parse(value.ToJson(), maxFileBytes, maxNodes, maxStringChars);
        return !JsonElement.DeepEquals(redact(document.RootElement), document.RootElement);
    }
    public bool Annotate(string step, string name, GuaValue value, bool sensitive = false)
        => Capture(() => session.Annotate(step, Text("name", name), value,
            sensitive || sensitiveField(name) || sensitiveField("name") || sensitiveField("value") || MaskedValue(value)));
    public bool Evaluate(string step, string target, string @operator, GuaValue? expected, GuaValue? actual,
        string truth, string role, IReadOnlyList<string> observations, GuaTraceOutcome callerOutcome, bool sensitive = false)
        => Capture(() => session.Evaluate(step, Text("target", target), Text("operator", @operator), expected, actual,
            Identity(truth), Text("role", role), References(observations), callerOutcome,
            sensitive || new[] { "target", "operator", "expected", "actual", "truth", "role", "observations", "callerOutcome" }.Any(sensitiveField) ||
            MaskedValue(expected) || MaskedValue(actual)));
    public bool Change(string step, string target, string change, GuaTraceHost host, GuaValue? before,
        GuaValue? after, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Change(step, Text("target", target), Identity(change), Host(host), before, after, Identity(continuity),
            sensitive || new[] { "target", "change", "before", "after" }.Any(sensitiveField) || MaskedValue(before) || MaskedValue(after)));
    public string CaptureTree(string step, string channel, string sourceId, string reason, Func<string> getter, string? epoch = null)
        => Capture(() => GuaTraceCapture.Tree(session, step, Identity(channel), Identity(sourceId), Text("reason", reason), () =>
        { using var document = GuaJsonAdmission.Parse(getter(), maxFileBytes, maxNodes, maxStringChars);
            return redact(document.RootElement).GetRawText(); }, Optional(epoch)), string.Empty);
}
