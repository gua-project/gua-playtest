using System.Text.Json;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;
using Gua.Playtest.Runner.Preparation;
using Gua.Testing;
using Gua.Testing.Recording;

namespace Gua.Playtest.Cli;

public sealed record GuaArtifactCompletion(PersistenceResult Persistence, IReadOnlyList<ArtifactReceipt> Receipts)
{
    public int ExitCode(RunOutcome original) => Persistence.ExitCode(original);
}

/// <summary>Trusted composition across Runner and GuaIntegration, serialized by the execution
/// owner except the worker-safe Preparation trace port. No production host capability is granted.</summary>
public sealed class GuaRunArtifacts : IPreparationTrace, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly RunArtifactStore store;
    private readonly int maxReferences;
    private readonly List<ObservationReference> observations = [];
    private readonly List<DecisionReference> decisions = [];
    private readonly Dictionary<string, string> observationIds = new(StringComparer.Ordinal);
    private readonly GuaRunArtifactWriter writer;
    private PrimaryResult? primary;
    private bool completionAttempted, captureFailed;
    public PersistenceResult PreparationPersistence { get; }
    public string RunId => store.RunId;
    public GuaRunTrace Trace => writer.Trace;

    /// <summary>Construct at Preparing entry before launch/connect. The existing store owns
    /// metadata sanitization and immutable summaries; Gua owns its Trace/Recording bytes.</summary>
    public GuaRunArtifacts(RunArtifactStore store, RunArtifactMetadata metadata, ArtifactLimits limits,
        GuaTraceOptions options, PersistenceRedactor recordingRedactor, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(recordingRedactor);
        if (metadata.Profile != options.Profile) throw new ArgumentException("ArtifactProfileMismatch");
        this.store = store; maxReferences = limits.MaxItems;
        writer = new(store.DirectoryPath, options, limits.MaxFileBytes, limits.MaxRunBytes,
            limits.MaxItems, text => recordingRedactor.Redact(text) == text,
            bytes => SafeRecordingEncoding(recordingRedactor, bytes),
            document => recordingRedactor.RedactDocument(document, limits),
            recordingRedactor.IsSensitiveField, recordingRedactor.Redact, limits.MaxStringChars);
        PreparationPersistence = store.BeginPreparation(metadata, token);
    }
    private static bool SafeRecordingEncoding(PersistenceRedactor redactor, ReadOnlyMemory<byte> bytes)
    {
        if (!redactor.IsSafeUtf8(bytes.Span)) return false;
        using var document = JsonDocument.Parse(bytes);
        return !redactor.HasSensitiveFields(document.RootElement) &&
            !redactor.IsSensitiveField("recording") && !redactor.IsSensitiveField("index");
    }

    public void Record(PreparationEvent evidence)
    {
        lock (gate)
        {
            if (completionAttempted) { captureFailed = true; return; }
            var step = Trace.BeginStep(GuaTraceStepKind.Lifecycle, "Preparing");
            if (string.IsNullOrEmpty(step) || !Trace.Record(step, "runner.preparation",
                JsonSerializer.SerializeToElement(new { stage = evidence.Stage.ToString(), code = evidence.Code.ToString() })))
                captureFailed = true;
            Trace.EndStep(step, evidence.Code is PreparationCode.Started or PreparationCode.Completed
                ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed);
        }
    }

    /// <summary>Associate a genuine Gua observation ID with the owner's observation identity.
    /// Snapshot/profile authorization and capture completion belong to the caller.</summary>
    public string Observe(string ownerObservationId, ObservationBoundary boundary, string step,
        string channel, string reason, string availability, GuaTraceHost host,
        JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
    {
        lock (gate)
        {
            if (completionAttempted || (primary is null) == (boundary == ObservationBoundary.AfterCleanup) ||
                observationIds.ContainsKey(ownerObservationId) || observations.Count + decisions.Count >= maxReferences)
                throw new InvalidOperationException("TraceObservationBoundaryInvalid");
            var id = Trace.Observe(step, channel, reason, availability, host, snapshot, continuity, sensitive);
            if (id.Length == 0) { captureFailed = true; return id; }
            observationIds.Add(ownerObservationId, id); observations.Add(new(boundary, id)); return id;
        }
    }

    public bool RecordPlannerDecision(string step, PlannerDecisionReference reference, string? approvedActionId = null)
    {
        lock (gate)
        {
            if (primary is not null || completionAttempted || reference.RunId != RunId ||
                !Enum.IsDefined(reference.Code) || !observationIds.TryGetValue(reference.BasedOnObservationId, out var observation) ||
                observations.Count + decisions.Count >= maxReferences)
                throw new InvalidOperationException("TraceDecisionReferenceInvalid");
            var value = new DecisionReference(reference.DecisionRequestId, observation, approvedActionId, reference.Code.ToString());
            decisions.Add(value);
            var accepted = Trace.Record(step, "planner.reference", JsonSerializer.SerializeToElement(new
                { decisionId = value.DecisionId, observationId = observation, actionId = approvedActionId, code = value.ReasonCode }));
            captureFailed |= !accepted; return accepted;
        }
    }

    /// <summary>Use directly as RunExecutor's pre-cleanup confirmation hook with its fresh token.</summary>
    public ValueTask<bool> ConfirmPrimaryAsync(RunSnapshot snapshot, CancellationToken token)
    {
        lock (gate)
        {
            if (primary is not null || completionAttempted) throw new InvalidOperationException("TracePrimaryAlreadyConfirmed");
            primary = snapshot.Primary;
            captureFailed |= !writer.ConfirmPrimary(Map(snapshot.Primary.Status));
            var step = Trace.BeginStep(GuaTraceStepKind.Lifecycle, "Primary confirmation");
            captureFailed |= !Trace.Record(step, "runner.primary", JsonSerializer.SerializeToElement(new
                { status = primary.Status.ToString(), reason = primary.Cause.Reason.ToString(),
                    phase = primary.Cause.Phase.ToString(), origin = primary.Cause.Origin.ToString() }));
            Trace.EndStep(step, Map(primary.Status));
            return ValueTask.FromResult(store.ConfirmPrimary(snapshot,
                observations.Where(x => x.Boundary != ObservationBoundary.AfterCleanup).ToArray(), decisions.ToArray(), token).Saved);
        }
    }

    public async Task<GuaArtifactCompletion> CompleteAsync(RunOutcome original, DateTimeOffset finishedAt,
        GuaRecording? recording = null, bool recordingCaptureFailed = false, CancellationToken token = default)
    {
        ObservationReference[] cleanup;
        lock (gate)
        {
            if (completionAttempted || primary != original.Primary) throw new InvalidOperationException("TraceCompletionStateInvalid");
            completionAttempted = true;
            cleanup = observations.Where(x => x.Boundary == ObservationBoundary.AfterCleanup).ToArray();
            var step = Trace.BeginStep(GuaTraceStepKind.Lifecycle, "Postprocessing");
            captureFailed |= !Trace.Record(step, "runner.postprocessing", JsonSerializer.SerializeToElement(new
                { complete = original.PostProcessingComplete, reasons = original.PostProcessing.Select(x => x.Reason.ToString()).ToArray() }));
            Trace.EndStep(step, original.PostProcessingComplete ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed);
        }
        var bundles = await writer.CompleteAsync(recording, recordingCaptureFailed).ConfigureAwait(false);
        var trace = captureFailed && bundles.Trace.State is GuaArtifactState.Saved or GuaArtifactState.OmittedOnSuccess
            ? new ArtifactReceipt(ArtifactKind.Trace, ArtifactState.CaptureFailed) : Receipt(ArtifactKind.Trace, bundles.Trace);
        ArtifactReceipt[] receipts = [trace, Receipt(ArtifactKind.Recording, bundles.Recording)];
        return new(store.Complete(original, receipts, cleanup, finishedAt, token), receipts);
    }
    private static GuaTraceOutcome Map(ResultStatus status) => status switch
    { ResultStatus.Passed => GuaTraceOutcome.Passed, ResultStatus.Aborted => GuaTraceOutcome.Interrupted, _ => GuaTraceOutcome.Failed };
    private static ArtifactReceipt Receipt(ArtifactKind kind, GuaArtifactBundle bundle) => new(kind,
        Enum.Parse<ArtifactState>(bundle.State.ToString()), bundle.Entry?.Name, bundle.Entry?.Bytes, bundle.Entry?.Sha256,
        bundle.AdditionalFiles?.Select(x => new ArtifactFileReference(x.Name, x.Bytes, x.Sha256)).ToArray());
    public ValueTask DisposeAsync() => writer.DisposeAsync();
}
