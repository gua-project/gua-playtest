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
    private int failed;
    internal GuaTraceSession Session => session;
    internal bool CaptureFailed => Volatile.Read(ref failed) != 0;
    public GuaTraceStatus Status => session.Status;
    public string ArtifactPath => session.ArtifactPath;
    public string TraceId => session.TraceId;
    internal GuaRunTrace(GuaTraceSession session, Func<JsonElement, JsonElement> redact,
        Func<string, bool> sensitiveField, Func<string, string> redactText)
    { this.session = session; this.redact = redact; this.sensitiveField = sensitiveField; this.redactText = redactText; }
    internal static void ValidatePolicy(Func<string, bool> sensitiveField)
    {
        // These Gua-owned envelope/identity fields cannot be removed through 1.1.1's
        // public writer. Reject that policy before starting rather than bypassing it.
        var names = new[] { typeof(GuaTraceEvent), typeof(GuaTraceManifest), typeof(GuaTraceStatus),
            typeof(GuaTraceHost), typeof(GuaTraceRequest) }.SelectMany(type => type.GetProperties())
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));
        if (names.Concat(["request", "schema", "blob", "observationId", "channel", "availability", "host", "continuity", "metadata", "kind", "outcome"])
            .Any(sensitiveField)) throw new ArgumentException("GuaTraceSensitiveEnvelopeUnsupported");
    }
    private string Text(string name, string value) => sensitiveField(name) ? string.Empty : redactText(value);
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
        => Capture(() => session.BeginStep(kind, Text("label", label), request,
            sensitiveField("parentStepId") ? null : parentStepId,
            sourceFile is null || new[] { "source", "file", "sourceFile" }.Any(sensitiveField) ? null : redactText(sourceFile),
            sensitiveField("line") || sensitiveField("sourceLine") ? null : sourceLine), string.Empty);
    public string Mark(string label) => Capture(() => session.Mark(Text("label", label)), string.Empty);
    public bool Record(string step, string type, JsonElement data, bool sensitive = false)
        => Capture(() => session.Record(step, type, sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(data), sensitive));
    public bool RecordRequest(GuaTraceRequest request, string phase, JsonElement data)
        => Capture(() => session.RecordRequest(request, Text("phase", phase),
            sensitiveField("result") ? JsonSerializer.SerializeToElement<object?>(null) : redact(data)));
    public void EndStep(string step, GuaTraceOutcome outcome) => session.EndStep(step, outcome);
    public bool Correlate(string step, GuaTraceRequest request) => Capture(() => session.Correlate(step, request));
    public bool Attach(string step, string schema, JsonElement content, bool sensitive = false)
        => Capture(() => session.Attach(step, schema,
            sensitive ? JsonSerializer.SerializeToElement<object?>(null) : redact(content), sensitive));
    public string Observe(string step, string channel, string reason, string availability, GuaTraceHost host,
        JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Observe(step, channel, Text("reason", reason), availability, host,
            sensitive || snapshot is null ? null : redact(snapshot.Value), continuity, sensitive), string.Empty);
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
            truth, Text("role", role), observations, callerOutcome,
            sensitive || new[] { "target", "operator", "expected", "actual", "truth", "role", "observations", "callerOutcome" }.Any(sensitiveField) ||
            MaskedValue(expected) || MaskedValue(actual)));
    public bool Change(string step, string target, string change, GuaTraceHost host, GuaValue? before,
        GuaValue? after, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Change(step, Text("target", target), change, host, before, after, continuity,
            sensitive || new[] { "target", "change", "before", "after" }.Any(sensitiveField) || MaskedValue(before) || MaskedValue(after)));
    public string CaptureTree(string step, string channel, string sourceId, string reason, Func<string> getter, string? epoch = null)
        => Capture(() => GuaTraceCapture.Tree(session, step, channel, sourceId, Text("reason", reason), () =>
        { using var document = JsonDocument.Parse(getter()); return redact(document.RootElement).GetRawText(); }, epoch), string.Empty);
}
