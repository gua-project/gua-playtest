using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Replay;

namespace Gua.Playtest.Runner.Planning;

/// <summary>Host-owned private storage. The authority key and this service must never be
/// exposed to a Planner. Signatures authenticate local provenance, not a publisher or safety.</summary>
public sealed class PlanManager
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root;
    private readonly FileIdentity rootIdentity;
    private readonly byte[] key;
    private readonly PersistenceRedactor redactor;
    public PlanManager(string managedRoot, PersistenceRedactor redactor)
    {
        this.redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        var existing = root;
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("PlanRootInvalid");
        RunArtifactStore.CheckPath(existing);
        Directory.CreateDirectory(root); RunArtifactStore.CheckPath(root);
        rootIdentity = FileIdentity.ReadDirectory(root);
        var keyPath = Path.Combine(root, ".plan-authority");
        if (!File.Exists(keyPath))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            try { Publish(keyPath, fresh, default); }
            catch (IOException) when (File.Exists(keyPath)) { }
        }
        key = Read(keyPath);
        if (key.Length != 32) throw new InvalidDataException("PlanAuthorityInvalid");
    }

    /// <summary>Copy supplied expectations only; do not infer a Goal from observed bugs.
    /// The generated candidate always requests full afterPlan coverage.</summary>
    public async ValueTask<string> CreateAsync(string scenarioPath, string recordingPath,
        IEnumerable<string> allowedRoots, string timing, CancellationToken token = default,
        string? expectedScenarioSha256 = null, string? expectedRecordingSha256 = null,
        Func<string, bool>? candidateResponseAllowed = null)
    {
        token.ThrowIfCancellationRequested();
        var roots = allowedRoots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToArray();
        var recordingFullPath = AuthorizeInput(recordingPath, roots, "RecordingOutsideAllowedRoots");
        var scenarioFullPath = AuthorizeInput(scenarioPath, roots, "ScenarioOutsideAllowedRoots");
        var scenarioReport = await new StaticContractValidator(roots).ValidateFileAsync(scenarioFullPath, token);
        token.ThrowIfCancellationRequested();
        if (!scenarioReport.IsValid || scenarioReport.Document is not ScenarioDocument) throw new InvalidDataException("ScenarioInvalid");
        var source = scenarioReport.Files.Values.Single();
        if (expectedScenarioSha256 is not null && source.Sha256 != expectedScenarioSha256)
            throw new InvalidDataException("PlanInputChanged");
        RejectSecrets(source.CopyBytes());
        var recordingBytes = Read(recordingFullPath); RejectSecrets(recordingBytes);
        if (expectedRecordingSha256 is not null && Hash(recordingBytes) != expectedRecordingSha256)
            throw new InvalidDataException("PlanInputChanged");
        // Use static reference validation to authorize and retain Recording bytes too.
        var sourcePlan = new ReplayPlanDocument("candidate", new(source.Path, source.Sha256),
            new(recordingFullPath, Hash(recordingBytes)), timing, "afterPlan", [], null);
        var sourcePlanBytes = CheckedPlanBytes(sourcePlan);
        var staging = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var entry = Path.Combine(staging, "source.json");
            Publish(entry, sourcePlanBytes, token);
            var report = await new StaticContractValidator(roots.Append(root)).ValidateFileAsync(entry, token);
            token.ThrowIfCancellationRequested();
            if (!report.IsValid) throw new InvalidDataException(report.Code);
            var scenario = report.Files.Values.Single(f => f.Sha256 == source.Sha256);
            var recording = report.Files.Values.Single(f => f.Sha256 == sourcePlan.Recording.Sha256);
            RejectStructuredSecrets(scenario.CopyJson()); RejectStructuredSecrets(recording.CopyJson());
            var scenarioName = "scenario" + Path.GetExtension(source.Path).ToLowerInvariant();
            var recordingName = "recording" + Path.GetExtension(recording.Path).ToLowerInvariant();
            Publish(Path.Combine(staging, scenarioName), scenario.CopyBytes(), token);
            Publish(Path.Combine(staging, recordingName), recording.CopyBytes(), token);
            var id = Guid.NewGuid().ToString("N");
            var plan = sourcePlan with { PlanId = id, Scenario = new(scenarioName, scenario.Sha256), Recording = new(recordingName, recording.Sha256) };
            Publish(Path.Combine(staging, "plan.json"), CheckedPlanBytes(plan), token);
            File.Delete(entry);
            var validated = await new StaticContractValidator([root]).ValidateFileAsync(Path.Combine(staging, "plan.json"), token);
            token.ThrowIfCancellationRequested();
            if (!validated.IsValid) throw new InvalidDataException(validated.Code);
            token.ThrowIfCancellationRequested(); RunArtifactStore.CheckPath(root);
            var destination = Path.Combine(root, id);
            if (candidateResponseAllowed is not null && !candidateResponseAllowed(Path.Combine(destination, "plan.json")))
                throw new InvalidDataException("PlanResponseRejected");
            Directory.Move(staging, destination);
            return Path.Combine(destination, "plan.json");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>Called by the trusted Replay composition after CompleteReplay, never by a file
    /// import. Copies immutable result/completion bytes so temporary Runs may be removed.</summary>
    public async ValueTask<string> RecordTrialAsync(string planPath, RunArtifactStore store,
        ReplayOutcome replay, RunIdentity identity, CancellationToken token = default)
    {
        var (directory, plan, scenario, recording) = await Load(planPath, token);
        RequireFullReplay(replay);
        if (!store.CanCertifyReplay(replay) || identity != store.ReplayIdentity || replay.PlanSha256 != plan.Sha256 ||
            replay.ScenarioSha256 != scenario.Sha256 || replay.RecordingSha256 != recording.Sha256)
            throw new InvalidDataException("TrialIdentityMismatch");
        var scenarioModel = JsonSerializer.Deserialize<ScenarioDocument>(scenario.CopyJson(), Json)!;
        if (identity.ScenarioSha256 != scenario.Sha256 || identity.ScenarioId != scenarioModel.ScenarioId ||
            identity.DefinitionVersion != scenarioModel.DefinitionVersion)
            throw new InvalidDataException("TrialIdentityMismatch");
        var limit = store.SummaryMaxBytes;
        var result = Read(Path.Combine(store.DirectoryPath, "result.json"), limit);
        var completion = Read(Path.Combine(store.DirectoryPath, "completion.json"), limit);
        var run = Read(Path.Combine(store.DirectoryPath, "run.json"), limit);
        if (!store.MatchesPublishedSummary("run.json", run) || !store.MatchesPublishedSummary("result.json", result) ||
            !store.MatchesPublishedSummary("completion.json", completion)) throw new InvalidDataException("TrialSummaryChanged");
        foreach (var bytes in new[] { run, result, completion })
        { RejectSecrets(bytes); RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(bytes)!); }
        CheckRun(run, store.RunId, identity);
        CheckResult(result, store.RunId);
        CheckCompletion(completion, store.RunId);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, ".trial-" + id);
        Directory.CreateDirectory(temporary);
        try
        {
            Publish(Path.Combine(temporary, "result.json"), result, token, limit);
            Publish(Path.Combine(temporary, "completion.json"), completion, token, limit);
            Publish(Path.Combine(temporary, "run.json"), run, token, limit);
            var proof = new TrialProof(store.RunId, identity, replay.PlanSha256, replay.ScenarioSha256,
                replay.RecordingSha256, replay.Progress, replay.TotalCheckpoints, Hash(result), Hash(completion), Hash(run), limit);
            Publish(Path.Combine(temporary, "proof.json"), Seal(proof), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(temporary, Path.Combine(directory, "trial-" + id));
            return id;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    /// <summary>Explicit approval applies to these exact expectation and timing bytes.
    /// A changed Plan needs a new full trial and approval. Never overwrites an adoption.</summary>
    public async ValueTask AcceptAsync(string planPath, string trialId, bool explicitlyApprove,
        bool expectationsApproved, CancellationToken token = default, string? expectedPlanSha256 = null)
    {
        if (!explicitlyApprove || !expectationsApproved) throw new InvalidDataException("ExplicitApprovalRequired");
        var (directory, plan, scenario, recording) = await Load(planPath, token);
        if (expectedPlanSha256 is not null && plan.Sha256 != expectedPlanSha256)
            throw new InvalidDataException("PlanInputChanged");
        var proof = VerifyTrial(directory, trialId, plan, scenario, recording);
        var evidence = new AdoptionEvidenceDocument(new("plan.json", plan.Sha256),
            new(Relative(directory, scenario.Path), scenario.Sha256), new(Relative(directory, recording.Path), recording.Sha256),
            proof.RunId, proof.Identity, "afterPlan", true, true, true, false);
        // Signed marker is authoritative; the wire evidence inside remains independently exportable.
        Publish(Path.Combine(directory, "accepted.json"), Seal(new Acceptance(trialId, evidence)), token);
    }

    public async ValueTask<bool> IsAcceptedAsync(string planPath, CancellationToken token = default)
    {
        var (directory, plan, scenario, recording) = await Load(planPath, token);
        var marker = Path.Combine(directory, "accepted.json");
        if (!File.Exists(marker)) return false;
        var acceptance = Unseal<Acceptance>(Read(marker));
        var proof = VerifyTrial(directory, acceptance.TrialId, plan, scenario, recording);
        if (acceptance.Evidence.Plan.Path != "plan.json" ||
            acceptance.Evidence.Scenario.Path != Relative(directory, scenario.Path) ||
            acceptance.Evidence.Recording.Path != Relative(directory, recording.Path) ||
            acceptance.Evidence.Plan.Sha256 != plan.Sha256 || acceptance.Evidence.TestRunId != proof.RunId ||
            acceptance.Evidence.Scenario.Sha256 != scenario.Sha256 || acceptance.Evidence.Recording.Sha256 != recording.Sha256)
            throw new InvalidDataException("AdoptionIdentityMismatch");
        return true;
    }

    private async ValueTask<(string, ValidatedFile, ValidatedFile, ValidatedFile)> Load(string planPath, CancellationToken token)
    {
        var path = Path.GetFullPath(planPath);
        var directory = Path.GetDirectoryName(path)!;
        if (!string.Equals(Path.GetFileName(path), "plan.json", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new InvalidDataException("ManagedPlanRequired");
        if (FileIdentity.ReadDirectory(Path.GetDirectoryName(directory)!) != rootIdentity)
            throw new InvalidDataException("ManagedPlanRequired");
        RunArtifactStore.CheckPath(path);
        var canonical = Path.Combine(directory, "plan.json");
        // Case sensitivity is a filesystem property, not an OS property. Keep the
        // managed entry name, and accept alternate casing only for the same object.
        var requested = FileIdentity.OpenRegular(path);
        using (requested.Stream)
        {
            var expected = FileIdentity.OpenRegular(canonical);
            using (expected.Stream)
                if (requested.Identity != expected.Identity) throw new InvalidDataException("ManagedPlanRequired");
        }
        var report = await new StaticContractValidator([directory]).ValidateFileAsync(canonical, token);
        token.ThrowIfCancellationRequested();
        if (!report.IsValid || report.Document is not ReplayPlanDocument model || model.Completion != "afterPlan")
            throw new InvalidDataException("CandidateInvalid");
        foreach (var file in report.Files.Values)
        { RejectSecrets(file.CopyBytes()); RejectStructuredSecrets(file.CopyJson()); }
        return (directory, report.Files.Values.Single(f => f.Path == canonical),
            report.Files.Values.Single(f => f.Sha256 == model.Scenario.Sha256),
            report.Files.Values.Single(f => f.Sha256 == model.Recording.Sha256));
    }
    private TrialProof VerifyTrial(string directory, string id, ValidatedFile plan, ValidatedFile scenario, ValidatedFile recording)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("TrialInvalid");
        var trial = Path.Combine(directory, "trial-" + id);
        var proof = Unseal<TrialProof>(Read(Path.Combine(trial, "proof.json")));
        if (proof.SummaryMaxBytes < 256 || proof.SummaryMaxBytes > 16 * 1024 * 1024) throw new InvalidDataException("TrialLimitInvalid");
        var result = Read(Path.Combine(trial, "result.json"), proof.SummaryMaxBytes);
        var completion = Read(Path.Combine(trial, "completion.json"), proof.SummaryMaxBytes);
        var run = Read(Path.Combine(trial, "run.json"), proof.SummaryMaxBytes);
        if (proof.PlanSha256 != plan.Sha256 || proof.ScenarioSha256 != scenario.Sha256 ||
            proof.RecordingSha256 != recording.Sha256 || proof.ResultSha256 != Hash(result) || proof.CompletionSha256 != Hash(completion) || proof.RunSha256 != Hash(run) ||
            !Full(proof.Progress, proof.TotalCheckpoints)) throw new InvalidDataException("TrialIdentityMismatch");
        foreach (var bytes in new[] { run, result, completion })
        { RejectSecrets(bytes); RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(bytes)!); }
        CheckResult(result, proof.RunId); CheckCompletion(completion, proof.RunId);
        CheckRun(run, proof.RunId, proof.Identity);
        return proof;
    }
    private static void RequireFullReplay(ReplayOutcome replay)
    {
        if (replay.Completion != CompletionPolicy.AfterPlan || replay.Run.Primary.Status != ResultStatus.Passed ||
            replay.Run.Primary.Cause.Reason != RunReason.GoalSatisfied || !replay.Run.PostProcessingComplete ||
            !Full(replay.Progress, replay.TotalCheckpoints)) throw new InvalidDataException("FullReplayRequired");
    }
    private static bool Full(ReplayProgress progress, int checkpoints) => progress.PlanCompleted && progress.GoalVerified &&
        progress.OmittedFromStep is null && progress.DispatchedSteps == progress.TotalSteps &&
        progress.CompletedSteps == progress.TotalSteps && progress.CompletedCheckpoints == checkpoints;
    private static void CheckResult(byte[] bytes, string runId)
    {
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement;
        if (result.GetProperty("kind").GetString() != "result" || result.GetProperty("schemaVersion").GetInt32() != 1 ||
            result.GetProperty("runId").GetString() != runId || result.GetProperty("status").GetString() != "Passed" ||
            result.GetProperty("reason").GetString() != "GoalSatisfied" || !result.GetProperty("postProcessing").GetProperty("complete").GetBoolean() ||
            result.GetProperty("postProcessing").GetProperty("reasons").EnumerateArray().Any(x => x.GetString() != "Cancelled"))
            throw new InvalidDataException("TrialResultInvalid");
    }
    private static void CheckCompletion(byte[] bytes, string runId)
    {
        using var json = JsonDocument.Parse(bytes);
        var completion = json.RootElement;
        if (completion.GetProperty("kind").GetString() != "playtestCompletionStorage" ||
            completion.GetProperty("storageVersion").GetInt32() != 1 || completion.GetProperty("runId").GetString() != runId ||
            completion.GetProperty("postProcessing").EnumerateArray().Any(x => x.GetProperty("reason").GetString() != "Cancelled")) throw new InvalidDataException("TrialPostProcessingInvalid");
    }
    private static void CheckRun(byte[] bytes, string runId, RunIdentity identity)
    {
        using var json = JsonDocument.Parse(bytes);
        var run = json.RootElement;
        if (run.GetProperty("kind").GetString() != "playtestRunStorage" || run.GetProperty("storageVersion").GetInt32() != 1 ||
            run.GetProperty("runId").GetString() != runId || run.GetProperty("identity").Deserialize<RunIdentity>(Json) != identity)
            throw new InvalidDataException("TrialRunInvalid");
    }
    private byte[] Seal<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        RejectSecrets(bytes);
        RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(bytes)!);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Convert.ToBase64String(bytes),
            Convert.ToHexStringLower(HMACSHA256.HashData(key, bytes))), Json);
        RejectSecrets(envelope);
        RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(envelope)!);
        return envelope;
    }
    private byte[] CheckedPlanBytes(ReplayPlanDocument plan)
    {
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(plan));
        RejectSecrets(bytes);
        RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(bytes)!);
        return bytes;
    }
    private void RejectSecrets(byte[] bytes)
    {
        if (redactor.ContainsUtf8Secret(bytes)) throw new InvalidDataException("PlanSecretCollision");
    }
    private void RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (redactor.Redact(property.Key) != property.Key || redactor.Sensitive(property.Key) && property.Value is not null)
                    throw new InvalidDataException("PlanSecretCollision");
                if (property.Value is not null) RejectStructuredSecrets(property.Value);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array) if (item is not null) RejectStructuredSecrets(item);
        }
        else if (node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && redactor.Redact(text) != text)
            throw new InvalidDataException("PlanSecretCollision");
    }
    private T Unseal<T>(byte[] bytes)
    {
        RejectSecrets(bytes);
        RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(bytes)!);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new InvalidDataException("ProofInvalid");
        var payload = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromHexString(envelope.Signature);
        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, payload)))
            throw new InvalidDataException("ProofUntrusted");
        RejectSecrets(payload);
        RejectStructuredSecrets(System.Text.Json.Nodes.JsonNode.Parse(payload)!);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("ProofInvalid");
    }
    private static byte[] Read(string path, int maximum = MaxBytes)
    {
        RunArtifactStore.CheckPath(path);
        var opened = FileIdentity.OpenRegular(path);
        using var stream = opened.Stream;
        if (stream.Length > maximum) throw new InvalidDataException("PlanFileTooLarge");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("PlanFileChanged");
        return bytes;
    }
    private static void Publish(string path, byte[] bytes, CancellationToken token, int maximum = MaxBytes)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("PlanFileTooLarge");
        RunArtifactStore.CheckPath(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Relative(string directory, string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    private static string AuthorizeInput(string input, IEnumerable<string> roots, string refusal)
    {
        var path = Path.GetFullPath(input);
        RunArtifactStore.CheckPath(path);
        foreach (var allowed in roots)
        {
            if (!Directory.Exists(allowed)) continue;
            var identity = FileIdentity.ReadDirectory(allowed);
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (FileIdentity.ReadDirectory(ancestor) != identity) continue;
                // This relative suffix uses the input's own ancestor spelling. Rebase
                // it onto the verified allowed root for the static lexical validator.
                var canonical = Path.GetFullPath(Path.Combine(allowed, Path.GetRelativePath(ancestor, path)));
                var requested = FileIdentity.OpenRegular(path);
                using (requested.Stream)
                {
                    var expected = FileIdentity.OpenRegular(canonical);
                    using (expected.Stream)
                        if (requested.Identity != expected.Identity) throw new InvalidDataException(refusal);
                }
                return canonical;
            }
        }
        throw new InvalidDataException(refusal);
    }
    private sealed record Envelope(string Payload, string Signature);
    private sealed record TrialProof(string RunId, RunIdentity Identity, string PlanSha256, string ScenarioSha256,
        string RecordingSha256, ReplayProgress Progress, int TotalCheckpoints, string ResultSha256, string CompletionSha256, string RunSha256, int SummaryMaxBytes);
    private sealed record Acceptance(string TrialId, AdoptionEvidenceDocument Evidence);
}
