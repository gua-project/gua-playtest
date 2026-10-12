using System.Text.Json;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Host-policy port to the public Gua writer. Every operation delegates to Gua;
/// no alternate Trace format or raw session bypass is exposed to the composition owner.</summary>
public sealed class GuaRunTrace
{
    private readonly GuaTraceSession session;
    private readonly Func<JsonElement, JsonElement> redact;
    private readonly Func<string, bool> sensitiveField;
    private readonly Func<string, string> redactText;
    private readonly int maxStringChars;
    private int failed;
    internal GuaTraceSession Session => session;
    internal bool CaptureFailed => Volatile.Read(ref failed) != 0;
    public GuaTraceStatus Status => session.Status;
    public string ArtifactPath => session.ArtifactPath;
    public string TraceId => session.TraceId;
    internal GuaRunTrace(GuaTraceSession session, Func<JsonElement, JsonElement> redact,
        Func<string, bool> sensitiveField, Func<string, string> redactText, int maxStringChars)
    { this.session = session; this.redact = redact; this.sensitiveField = sensitiveField; this.redactText = redactText; this.maxStringChars = maxStringChars; }
    internal static void ValidatePolicy(Func<string, bool> sensitiveField, Func<string, bool> safeLiteral)
    {
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
        var fixedTokens = envelopeNames.Concat(["recent", "streaming", "onFailure", "always", "debug", "player",
            "unknown", "passed", "failed", "interrupted", "collector-monotonic:", "true", "false", "0", "1",
            "index", "recording", "steps", "gua.trace.recording.v1", "gua.recording.references.v1"])
            .Concat(Enum.GetNames<GuaTraceStepKind>().Select(JsonNamingPolicy.CamelCase.ConvertName));
        if (fixedTokens.Any(token => !safeLiteral(token) || !safeLiteral(JsonSerializer.Serialize(token))) ||
            envelopeNames.Any(name => !safeLiteral(JsonSerializer.Serialize(name) + ":\"")))
            throw new ArgumentException("GuaTraceLiteralEnvelopeUnsupported");
    }
    private string Bound(string value)
    { if (value.Length > maxStringChars) throw new InvalidDataException("TraceStringLimit"); return value; }
    private string? Optional(string? value) => value is null ? null : Bound(value);
    private string Text(string name, string value)
    { Bound(value); return sensitiveField(name) ? string.Empty : redactText(value); }
    private GuaTraceRequest Request(GuaTraceRequest value)
    { Bound(value.SourceId); Bound(value.SessionEpoch); Bound(value.RequestId); return value; }
    private GuaTraceHost Host(GuaTraceHost value)
    {
        Bound(value.SourceId); Bound(value.SessionEpoch); Optional(value.Frame); Optional(value.Revision);
        Optional(value.Timestamp); Optional(value.ClockId); return value;
    }
    private IReadOnlyList<string> References(IReadOnlyList<string> values)
    { foreach (var value in values) Bound(value); return values; }
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
            sourceFile is null || new[] { "source", "file", "sourceFile" }.Any(sensitiveField) ? null : redactText(Bound(sourceFile)),
            sensitiveField("line") || sensitiveField("sourceLine") ? null : sourceLine), string.Empty);
    public string Mark(string label) => Capture(() => session.Mark(Text("label", label)), string.Empty);
    public bool Record(string step, string type, JsonElement data, bool sensitive = false)
        => Capture(() => session.Record(step, Text("type", type), sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(data), sensitive));
    public bool RecordRequest(GuaTraceRequest request, string phase, JsonElement data)
        => Capture(() => session.RecordRequest(Request(request), Text("phase", phase),
            sensitiveField("result") ? JsonSerializer.SerializeToElement<object?>(null) : redact(data)));
    public void EndStep(string step, GuaTraceOutcome outcome) => session.EndStep(step, outcome);
    public bool Correlate(string step, GuaTraceRequest request) => Capture(() => session.Correlate(step, Request(request)));
    public bool Attach(string step, string schema, JsonElement content, bool sensitive = false)
        => Capture(() => session.Attach(step, Bound(schema),
            sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(content), sensitive));
    public string Observe(string step, string channel, string reason, string availability, GuaTraceHost host,
        JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Observe(step, Bound(channel), Text("reason", reason), Bound(availability), Host(host),
            sensitive || snapshot is null ? null : redact(snapshot.Value), Bound(continuity), sensitive), string.Empty);
    private bool MaskedValue(GuaValue? value)
    {
        if (value is null) return false;
        using var document = JsonDocument.Parse(value.ToJson());
        return redact(document.RootElement).GetRawText() != document.RootElement.GetRawText();
    }
    public bool Annotate(string step, string name, GuaValue value, bool sensitive = false)
        => Capture(() => session.Annotate(step, Text("name", name), value,
            sensitive || sensitiveField(name) || sensitiveField("name") || sensitiveField("value") || MaskedValue(value)));
    public bool Evaluate(string step, string target, string @operator, GuaValue? expected, GuaValue? actual,
        string truth, string role, IReadOnlyList<string> observations, GuaTraceOutcome callerOutcome, bool sensitive = false)
        => Capture(() => session.Evaluate(step, Text("target", target), Text("operator", @operator), expected, actual,
            Bound(truth), Text("role", role), References(observations), callerOutcome,
            sensitive || new[] { "target", "operator", "expected", "actual", "truth", "role", "observations", "callerOutcome" }.Any(sensitiveField) ||
            MaskedValue(expected) || MaskedValue(actual)));
    public bool Change(string step, string target, string change, GuaTraceHost host, GuaValue? before,
        GuaValue? after, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Change(step, Text("target", target), Bound(change), Host(host), before, after, Bound(continuity),
            sensitive || new[] { "target", "change", "before", "after" }.Any(sensitiveField) || MaskedValue(before) || MaskedValue(after)));
    public string CaptureTree(string step, string channel, string sourceId, string reason, Func<string> getter, string? epoch = null)
        => Capture(() => GuaTraceCapture.Tree(session, step, Bound(channel), Bound(sourceId), Text("reason", reason), () =>
        { using var document = JsonDocument.Parse(getter()); return redact(document.RootElement).GetRawText(); }, Optional(epoch)), string.Empty);
}
