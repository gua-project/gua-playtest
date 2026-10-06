using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Single trusted execution owner, local private output root. Not a Trace writer or Planner port.
/// Every publication is create-only: temporary bytes are flushed before an atomic same-directory rename.</summary>
public sealed class RunArtifactStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ArtifactLimits limits;
    private readonly PersistenceRedactor redactor;
    private long usedBytes;
    private PrimaryResult? primary;
    private bool completionAttempted;
    private bool persistenceIncomplete;
    private long sanitizedChars;
    public string RunId { get; }
    public string DirectoryPath { get; }
    private RunArtifactStore(string root, ArtifactLimits limits, PersistenceRedactor redactor)
    {
        this.limits = limits; this.redactor = redactor;
        var absoluteRoot = Path.GetFullPath(root);
        var existing = absoluteRoot;
        while (!Directory.Exists(existing) && !File.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("ArtifactRootInvalid");
        CheckPath(existing);
        Directory.CreateDirectory(absoluteRoot);
        CheckPath(absoluteRoot);
        RunId = Guid.NewGuid().ToString("N");
        DirectoryPath = Path.Combine(absoluteRoot, RunId);
        // Random identity and exclusive owner marker prevent any reuse of an existing Run.
        if (Directory.Exists(DirectoryPath)) throw new IOException("RunAlreadyExists");
        Directory.CreateDirectory(DirectoryPath);
        using var owner = new FileStream(Path.Combine(DirectoryPath, ".owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    public static RunArtifactStore Create(string root, ArtifactLimits limits, PersistenceRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(redactor);
        return new(root, limits, redactor);
    }
    /// <summary>Call before launch/connect, at Preparing entry. Input copies are sanitized, never raw files.</summary>
    public PersistenceResult BeginPreparation(RunArtifactMetadata metadata, CancellationToken cancellationToken = default)
        => Attempt(() =>
        {
            if (File.Exists(Path.Combine(DirectoryPath, "run.json"))) throw new InvalidDataException("RunAlreadyStarted");
            var inputs = new List<object>();
            if (metadata.Inputs.Count > limits.MaxItems) throw new ArtifactLimitException();
            long inputBytes = 0;
            var inputNodes = 0;
            foreach (var input in metadata.Inputs)
            {
                var safe = Encode(input.Document, out var nodeCount);
                inputBytes += safe.Length; inputNodes += nodeCount;
                if (inputBytes > limits.MaxFileBytes || inputNodes > limits.MaxItems) throw new ArtifactLimitException();
                // Store as embedded fixed sanitized bytes; do not copy source files or original input hashes.
                inputs.Add(new { name = input.Name, sha256 = Hash(safe), document = JsonSerializer.Deserialize<JsonElement>(safe) });
            }
            Publish("run.json", Encode(new { kind = "playtestRunStorage", storageVersion = 1, runId = RunId,
                executionState = ExecutionState.Preparing, metadata.Profile, metadata.Settings, metadata.Versions, inputs }), cancellationToken);
        });
    /// <summary>Snapshot machine authority at confirmation, before diagnostics or input/resource release.</summary>
    public PersistenceResult ConfirmPrimary(RunSnapshot snapshot, IReadOnlyList<ObservationReference> observations,
        IReadOnlyList<DecisionReference> decisions, CancellationToken cancellationToken = default)
        => ConfirmPrimary(snapshot.Primary, observations, decisions, snapshot.Exceptions, cancellationToken, snapshot.Events);

    public PersistenceResult ConfirmPrimary(PrimaryResult result, IReadOnlyList<ObservationReference> observations,
        IReadOnlyList<DecisionReference> decisions, IReadOnlyList<ExceptionEvidence> exceptions,
        CancellationToken cancellationToken = default, IReadOnlyList<RunEvent>? events = null)
    {
        var saved = Attempt(() =>
        {
            if (primary is not null || completionAttempted) throw new InvalidDataException("PrimaryAlreadyConfirmed");
            ValidateReferences(observations, decisions);
            if (observations.Any(x => x.Boundary == ObservationBoundary.AfterCleanup)) throw new InvalidDataException("ObservationBoundaryInvalid");
            // Freeze in memory even if the disk write fails; persistence never authorizes a replacement result.
            primary = result;
            Publish("primary.json", Encode(new { kind = "playtestPrimaryStorage", storageVersion = 1, runId = RunId,
                result, observations, decisions, exceptions, events = events ?? [] }), cancellationToken);
        });
        return saved;
    }
    /// <summary>Publish once after cleanup. Receipts are independently read before association.
    /// Original RunOutcome and exception evidence are not mutated by redaction or persistence failures.</summary>
    public PersistenceResult Complete(RunOutcome outcome, IReadOnlyList<ArtifactReceipt> artifacts,
        IReadOnlyList<ObservationReference> cleanupObservations, DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        var saved = Attempt(() =>
        {
            if (completionAttempted || primary != outcome.Primary) throw new InvalidDataException("CompletionStateInvalid");
            completionAttempted = true;
            if (cleanupObservations.Any(x => x.Boundary != ObservationBoundary.AfterCleanup)) throw new InvalidDataException("ObservationBoundaryInvalid");
            ValidateReferences(cleanupObservations, []);
            if (artifacts.Count != 2 || artifacts.Select(x => x.Kind).Distinct().Count() != 2) throw new InvalidDataException("ArtifactKindsInvalid");
            var associated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<FileIdentity>();
            foreach (var receipt in artifacts) ValidateReceipt(receipt, outcome.Primary, associated, identities, cancellationToken);
            var issues = outcome.PostProcessing.ToList();
            if (persistenceIncomplete || artifacts.Any(x => x.State is ArtifactState.CaptureFailed or ArtifactState.SaveFailed))
                issues.Add(new(PostProcessingReason.ArtifactFailed));
            Publish("completion.json", Encode(new { kind = "playtestCompletionStorage", storageVersion = 1, runId = RunId,
                artifacts, observations = cleanupObservations, postProcessing = issues }), cancellationToken);
            var document = new ResultDocument(RunId, outcome.Primary.Status, outcome.Primary.Cause.Phase.ToString(),
                outcome.Primary.Cause.Origin.ToString(), outcome.Primary.Cause.Reason.ToString(),
                new(issues.All(x => x.Reason == PostProcessingReason.Cancelled), issues.Select(x => x.Reason.ToString()).Distinct().ToArray()), finishedAt);
            // ResultDocument is the existing public schema. No storage extension changes its wire format.
            Publish("result.json", Encode(document), cancellationToken);
        });
        return saved with { PostProcessingIncomplete = persistenceIncomplete || !outcome.PostProcessingComplete ||
            artifacts.Any(x => x.State is ArtifactState.CaptureFailed or ArtifactState.SaveFailed) };
    }
    private void ValidateReferences(IReadOnlyList<ObservationReference> observations, IReadOnlyList<DecisionReference> decisions)
    {
        if (observations.Count + decisions.Count > limits.MaxItems) throw new ArtifactLimitException();
        foreach (var observation in observations)
            if (!Enum.IsDefined(observation.Boundary) || !Token(observation.ObservationId)) throw new InvalidDataException("ReferenceInvalid");
        foreach (var decision in decisions)
            if (!Token(decision.DecisionId) || !Token(decision.ObservationId) ||
                (decision.ActionId is not null && !Token(decision.ActionId)) || !Token(decision.ReasonCode))
                throw new InvalidDataException("ReferenceInvalid");
    }
    private void ValidateReceipt(ArtifactReceipt receipt, PrimaryResult result, HashSet<string> associated, HashSet<FileIdentity> identities, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(receipt.Kind) || !Enum.IsDefined(receipt.State)) throw new InvalidDataException("ArtifactStateInvalid");
        if (receipt.State != ArtifactState.Saved)
        {
            if (receipt.FileName is not null || receipt.Bytes is not null || receipt.Sha256 is not null || receipt.AdditionalFiles is not null ||
                (receipt.State == ArtifactState.OmittedOnSuccess && result.Status != ResultStatus.Passed))
                throw new InvalidDataException("ArtifactReceiptInvalid");
            return;
        }
        if (receipt.FileName is null || receipt.Bytes is null || receipt.Sha256 is null) throw new InvalidDataException("ArtifactReceiptInvalid");
        ValidateFile(new(receipt.FileName, receipt.Bytes.Value, receipt.Sha256), associated, identities, cancellationToken);
        if (receipt.AdditionalFiles is not null)
            foreach (var file in receipt.AdditionalFiles) ValidateFile(file, associated, identities, cancellationToken);
    }
    private void ValidateFile(ArtifactFileReference reference, HashSet<string> associated, HashSet<FileIdentity> identities, CancellationToken cancellationToken)
    {
        var segments = reference.FileName.Split('/');
        if (segments.Length > 8 || segments.Any(x => !Regex.IsMatch(x, "^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9_-])?$", RegexOptions.CultureInvariant)) ||
            new[] { "run.json", "primary.json", "completion.json", "result.json" }.Contains(segments[0], StringComparer.OrdinalIgnoreCase) ||
            reference.Bytes <= 0 || reference.Bytes > limits.MaxFileBytes || reference.Sha256 is null ||
            !Regex.IsMatch(reference.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
            !associated.Add(reference.FileName)) throw new InvalidDataException("ArtifactReceiptInvalid");
        if (associated.Count > limits.MaxItems) throw new ArtifactLimitException();
        var path = Path.Combine(DirectoryPath, Path.Combine(segments));
        CheckPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        var opened = FileIdentity.OpenRegular(path);
        using var stream = opened.Stream;
        if (stream.Length != reference.Bytes) throw new InvalidDataException("ArtifactLengthMismatch");
        if (usedBytes + stream.Length > limits.MaxRunBytes) throw new ArtifactLimitException();
        if (!identities.Add(opened.Identity)) throw new InvalidDataException("ArtifactFileAlias");
        cancellationToken.ThrowIfCancellationRequested();
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != reference.Sha256) throw new InvalidDataException("ArtifactHashMismatch");
        usedBytes += stream.Length;
    }
    private byte[] Encode<T>(T value) => Encode(value, out _);
    private byte[] Encode<T>(T value, out int nodeCount)
    {
        // Construct a bounded sanitized tree directly from source objects. No unredacted serialized
        // buffer, temporary file or hash is ever created.
        var count = 0;
        sanitizedChars = 0;
        var safe = SafeNode(value, ref count);
        nodeCount = count;
        using var output = new LimitedBuffer(limits.MaxFileBytes);
        using (var writer = new Utf8JsonWriter(output)) { if (safe is null) writer.WriteNullValue(); else safe.WriteTo(writer, Json); }
        return output.ToArray();
    }
    private JsonNode? SafeNode(object? value, ref int count, int depth = 0)
    {
        if (++count > limits.MaxItems || depth > 32) throw new ArtifactLimitException();
        if (value is null) return null;
        if (value is EffectiveSetting setting)
        {
            var obj = new JsonObject();
            Add(obj, "name", setting.Name, ref count, depth, structural: true);
            Add(obj, "value", redactor.Sensitive(setting.Name) ? null : setting.Value, ref count, depth, structural: true);
            Add(obj, "source", setting.Source, ref count, depth, structural: true);
            return obj;
        }
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("InputDocumentUndefined");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject()) Add(obj, property.Name, property.Value, ref count, depth);
                return obj;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray()) array.Add(SafeNode(item, ref count, depth + 1));
                return array;
            }
            if (element.ValueKind == JsonValueKind.String) return SafeNode(element.GetString(), ref count, depth + 1);
            return JsonNode.Parse(element.GetRawText());
        }
        if (value is string text)
        {
            if (text.Length > limits.MaxStringChars) throw new ArtifactLimitException();
            ChargeChars(text.Length);
            return JsonValue.Create(redactor.Redact(text));
        }
        if (value is System.Collections.IDictionary dictionary)
        {
            var obj = new JsonObject();
            foreach (System.Collections.DictionaryEntry entry in dictionary)
                Add(obj, (string)entry.Key, entry.Value, ref count, depth);
            return obj;
        }
        if (value is System.Collections.IEnumerable enumerable)
        {
            var array = new JsonArray();
            foreach (var item in enumerable) array.Add(SafeNode(item, ref count, depth + 1));
            return array;
        }
        if (value.GetType().IsEnum)
        {
            if (redactor.Redact(value.ToString()!) != value.ToString()) throw new InvalidDataException("StructuralRedactionCollision");
            return SafeNode(value.ToString(), ref count, depth + 1);
        }
        if (value is DateTimeOffset || value.GetType().IsPrimitive || value is decimal)
            return JsonSerializer.SerializeToNode(value, value.GetType(), Json);
        var result = new JsonObject();
        foreach (var property in value.GetType().GetProperties())
        {
            var propertyValue = property.GetValue(value);
            if (propertyValue is not null)
                Add(result, JsonNamingPolicy.CamelCase.ConvertName(property.Name), propertyValue, ref count, depth, structural: true);
        }
        return result;
    }
    private void Add(JsonObject obj, string key, object? value, ref int count, int depth, bool structural = false)
    {
        if (++count > limits.MaxItems || key.Length > limits.MaxStringChars) throw new ArtifactLimitException();
        ChargeChars(key.Length);
        var safeKey = redactor.Redact(key);
        if (structural && (safeKey != key || redactor.Sensitive(key))) throw new InvalidDataException("StructuralRedactionCollision");
        if (structural && (key is "runId" or "kind" or "sha256" or "fileName" or "phase" or "origin" or "reason" or
            "observationId" or "decisionId" or "actionId" or "reasonCode") &&
            value is string structuralText && redactor.Redact(structuralText) != structuralText)
            throw new InvalidDataException("StructuralRedactionCollision");
        if (obj.ContainsKey(safeKey)) throw new InvalidDataException("RedactedKeyCollision");
        obj[safeKey] = redactor.Sensitive(key) ? null : SafeNode(value, ref count, depth + 1);
    }
    private void ChargeChars(int count)
    {
        sanitizedChars += count;
        if (sanitizedChars > limits.MaxFileBytes) throw new ArtifactLimitException();
    }
    private void Publish(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        if (usedBytes + bytes.Length > limits.MaxRunBytes) throw new ArtifactLimitException();
        CheckPath(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(DirectoryPath, name), overwrite: false);
            usedBytes += bytes.Length;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private PersistenceResult Attempt(Action action)
    {
        try { action(); return new(PersistenceFailure.None); }
        catch (OperationCanceledException) { return Failure(PersistenceFailure.Cancelled); }
        catch (ArtifactLimitException) { return Failure(PersistenceFailure.LimitExceeded); }
        catch (InvalidDataException) { return Failure(PersistenceFailure.InvalidEvidence); }
        catch (JsonException) { return Failure(PersistenceFailure.InvalidEvidence); }
        catch (IOException) { return Failure(PersistenceFailure.IoFailure); }
        catch (UnauthorizedAccessException) { return Failure(PersistenceFailure.IoFailure); }
    }
    private PersistenceResult Failure(PersistenceFailure failure) { persistenceIncomplete = true; return new(failure); }
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("LinkedArtifactPath");
    }
    private static bool Token(string value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class LimitedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) { if (Length + count > maximum) throw new ArtifactLimitException(); }
    }
}
