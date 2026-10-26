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
    private string Bound(string value)
    { if (value.Length > maxStringChars) throw new InvalidDataException("TraceStringLimit"); return value; }
    private string Identity(string name, string value)
    { Bound(value); if (sensitiveField(name) || redactText(value) != value) throw new InvalidDataException("GuaTraceIdentitySecret"); return value; }
    private string? Optional(string name, string? value) => value is null ? null : Identity(name, value);
    private string Text(string name, string value)
    { Bound(value); return sensitiveField(name) ? string.Empty : redactText(value); }
    private int? SourceLine(int? value)
    {
        if (sensitiveField("line") || sensitiveField("sourceLine")) return null;
        if (value is not null && redactText(JsonSerializer.Serialize(value.Value)) != JsonSerializer.Serialize(value.Value)) return null;
        return value;
    }
    private GuaTraceRequest Request(GuaTraceRequest value)
    { if (sensitiveField("request")) throw new InvalidDataException("TraceRequestSensitive");
        Identity("sourceId", value.SourceId); Identity("sessionEpoch", value.SessionEpoch); Identity("requestId", value.RequestId); return value; }
    private GuaTraceHost Host(GuaTraceHost value)
    {
        if (sensitiveField("host")) throw new InvalidDataException("TraceHostSensitive");
        Identity("sourceId", value.SourceId); Identity("sessionEpoch", value.SessionEpoch); Optional("frame", value.Frame); Optional("revision", value.Revision);
        Optional("timestamp", value.Timestamp); Optional("clockId", value.ClockId); return value;
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
            parentStepId is null ? null : Bound(parentStepId),
            sourceFile is null || new[] { "source", "file", "sourceFile" }.Any(sensitiveField) ? null : Text("sourceFile", sourceFile),
            SourceLine(sourceLine)), string.Empty);
    public string Mark(string label) => Capture(() => session.Mark(Text("label", label)), string.Empty);
    public bool Record(string step, string type, JsonElement data, bool sensitive = false)
        => Capture(() => session.Record(step, Text("type", type), (sensitive || sensitiveField("data")) ? JsonSerializer.SerializeToElement<object?>(null) : redact(data), sensitive || sensitiveField("data")));
    public bool RecordStructural(string step, string type, JsonElement data)
        => Capture(() =>
        {
            // Typed owner facts are trusted structure, not arbitrary host/input data.
            var bytes = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(data);
            if (bytes.Length > maxFileBytes) throw new InvalidDataException("TracePayloadLimit");
            GuaJsonAdmission.Validate(bytes, maxNodes, maxStringChars);
            if (type == "planner.reference")
                foreach (var name in new[] { "decisionId", "actionId" })
                    if (data.TryGetProperty(name, out var identity) && identity.ValueKind == JsonValueKind.String) Identity(name, identity.GetString()!);
            return session.Record(step, Bound(type), data);
        });
    public bool RecordRequest(GuaTraceRequest request, string phase, JsonElement data)
        => Capture(() => session.RecordRequest(Request(request), Text("phase", phase),
            sensitiveField("result") ? JsonSerializer.SerializeToElement<object?>(null) : redact(data)));
    public void EndStep(string step, GuaTraceOutcome outcome) => session.EndStep(step, outcome);
    public bool Correlate(string step, GuaTraceRequest request) => Capture(() => session.Correlate(step, Request(request)));
    public bool Attach(string step, string schema, JsonElement content, bool sensitive = false)
        => Capture(() => session.Attach(step, Bound(schema),
            (sensitive || sensitiveField("content")) ? JsonSerializer.SerializeToElement<object?>(null) : redact(content), sensitive || sensitiveField("content")));
    public string Observe(string step, string channel, string reason, string availability, GuaTraceHost host,
        JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Observe(step, Identity("channel", channel), Text("reason", reason), Bound(availability), Host(host),
            sensitive || sensitiveField("snapshot") || snapshot is null ? null : redact(snapshot.Value), Bound(continuity), sensitive || sensitiveField("snapshot")), string.Empty);
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
            Bound(truth), Text("role", role), References(observations), callerOutcome,
            sensitive || new[] { "target", "operator", "expected", "actual", "truth", "role", "observations", "callerOutcome" }.Any(sensitiveField) ||
            MaskedValue(expected) || MaskedValue(actual)));
    public bool Change(string step, string target, string change, GuaTraceHost host, GuaValue? before,
        GuaValue? after, string continuity = "unverified", bool sensitive = false)
        => Capture(() => session.Change(step, Text("target", target), Bound(change), Host(host), before, after, Bound(continuity),
            sensitive || new[] { "target", "change", "before", "after" }.Any(sensitiveField) || MaskedValue(before) || MaskedValue(after)));
    public string CaptureTree(string step, string channel, string sourceId, string reason, Func<string> getter, string? epoch = null)
        => Capture(() => GuaTraceCapture.Tree(session, step, Identity("channel", channel), Identity("sourceId", sourceId), Text("reason", reason), () =>
        { if (sensitiveField("tree") || sensitiveField("snapshot")) throw new InvalidDataException("TraceTreeSensitive");
            using var document = GuaJsonAdmission.Parse(getter(), maxFileBytes, maxNodes, maxStringChars);
            return redact(document.RootElement).GetRawText(); }, Optional("sessionEpoch", epoch)), string.Empty);
}
