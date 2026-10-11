using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Dashboard;

public sealed record DashboardLimits(int MaxRuns = 1000, int MaxFileBytes = 1024 * 1024,
    long MaxIndexBytes = 32 * 1024 * 1024, int MaxRoots = 16, int MaxEntries = 10000)
{
    internal void Validate()
    {
        if (MaxRuns is < 1 or > 10000 || MaxFileBytes is < 256 or > 16 * 1024 * 1024 ||
            MaxIndexBytes < MaxFileBytes || MaxIndexBytes > 256L * 1024 * 1024 || MaxRoots is < 1 or > 64 || MaxEntries is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(MaxRuns));
    }
}
public sealed record DashboardScenario(string ScenarioId, string DefinitionVersion, string Sha256, string Name);
public sealed record DashboardDetail(string Kind, string State, string? Report = null);
public sealed record DashboardRun(string Key, string RunId, string? ScenarioId, string? DefinitionVersion,
    string? ScenarioSha256, string? BuildId, string? EnvironmentSha256, DateTimeOffset? StartedAt,
    string State, ResultDocument? Result, IReadOnlyList<DashboardDetail> Details)
{
    // Fixed-width decimal string preserves .NET 100 ns precision through JavaScript JSON.
    public string? StartedOrder => StartedAt?.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
}
public sealed record DashboardSnapshot(string RegistryState, IReadOnlyList<DashboardScenario> Scenarios,
    IReadOnlyList<DashboardRun> Runs, IReadOnlyList<string> Warnings, bool Truncated);
public sealed record DashboardSelection(DashboardScenario Scenario, DashboardRun? Latest, string State);

/// <summary>Explicit, bounded, read-only snapshot. Paths and raw metadata never enter browser JSON.</summary>
public sealed class DashboardIndex
{
    private readonly string registry;
    private readonly string[] roots;
    private readonly string[] allowedRoots;
    private readonly DashboardLimits limits;
    private readonly HashSet<string> validationRoots = new(StringComparer.Ordinal);
    private Dictionary<string, (string Directory, ArtifactFileReference File, FileIdentity Identity, ArtifactFileReference[] Files)> reports = [];
    private long remaining;
    private readonly Dictionary<FileIdentity, string> admittedHashes = [];
    internal Func<string, IEnumerable<string>, CancellationToken, ValueTask<StaticValidationReport>> ValidateContract { get; init; }
        = static (path, roots, token) => new StaticContractValidator(roots).ValidateFileAsync(path, token);
    internal Action<string>? ReceiptFileOpened { get; init; }
    internal Action<string>? ReferenceAdmitted { get; init; }
    internal Action<string>? PathProbed { get; init; }
    internal Action<string, CancellationToken>? ReportFileOpened { get; init; }
    public DashboardIndex(string registry, IEnumerable<string> artifactRoots, IEnumerable<string> allowedRoots,
        DashboardLimits? limits = null)
    {
        this.registry = LocalPath(registry);
        roots = artifactRoots.Select(path => LocalPath(path)).Distinct(StringComparer.Ordinal).ToArray();
        this.allowedRoots = allowedRoots.Select(path => LocalPath(path)).ToArray();
        this.limits = limits ?? new(); this.limits.Validate();
        if (roots.Length is 0 || roots.Length > this.limits.MaxRoots || this.allowedRoots.Length is 0 ||
            this.allowedRoots.Length > this.limits.MaxRoots) throw new ArgumentException("ExplicitRootsRequired");
    }
    public async ValueTask<DashboardSnapshot> ReadAsync(CancellationToken token = default)
    {
        // Build a separate bounded index so canceled or failed refreshes cannot invalidate
        // the report routes belonging to the last successfully returned snapshot.
        var pending = new DashboardIndex(registry, roots, allowedRoots, limits)
        { ValidateContract = ValidateContract, ReceiptFileOpened = ReceiptFileOpened,
            ReferenceAdmitted = ReferenceAdmitted, PathProbed = PathProbed, ReportFileOpened = ReportFileOpened };
        var snapshot = await pending.ReadCoreAsync(token);
        token.ThrowIfCancellationRequested();
        reports = pending.reports;
        return snapshot;
    }
    private async ValueTask<DashboardSnapshot> ReadCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        reports.Clear(); validationRoots.Clear(); remaining = limits.MaxIndexBytes;
        var warnings = new List<string>(); var runs = new List<DashboardRun>();
        var scenarios = new List<DashboardScenario>(); var truncated = false;
        try
        {
            CheckAllowed(registry, token);
            using (var admission = ReadContract(registry, reserveValidationRead: true))
                AdmitWireReferences(admission.RootElement, Path.GetDirectoryName(registry)!, new HashSet<string>(StringComparer.Ordinal), token, reserveValidationRead: true);
            var report = await ValidateContract(registry, allowedRoots.Concat(validationRoots).ToArray(), token);
            token.ThrowIfCancellationRequested();
            if (report.Code == "Interrupted") throw new OperationCanceledException(token);
            if (!report.IsValid || report.Document is not ScenarioRegistryDocument population)
                return new(report.Code == "Valid" ? "RegistryInvalid" : report.Code, [], [], [], false);
            CheckValidationAuthority(report, token);
            if (population.ArtifactRoots.Length > limits.MaxRoots) throw new InvalidDataException();
            foreach (var root in population.ArtifactRoots) CheckAllowed(LocalPath(root, Path.GetDirectoryName(registry)!), token);
            foreach (var entry in population.Scenarios)
            {
                token.ThrowIfCancellationRequested();
                var file = report.Files.Values.Single(x => x.Sha256 == entry.File.Sha256 &&
                    x.CopyJson()["scenarioId"]?.GetValue<string>() == entry.ScenarioId);
                scenarios.Add(new(entry.ScenarioId, entry.DefinitionVersion, entry.File.Sha256,
                    file.CopyJson()["name"]!.GetValue<string>()));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (ReadFailure(e)) { return new("RegistryUnreadable", [], [], [], false); }
        var visited = new HashSet<FileIdentity>(); var examined = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (truncated) break;
            try
            {
                CheckAllowed(root, token);
                if (!visited.Add(FileIdentity.ReadDirectory(root))) continue;
                // Enumerate immediate Run entries only, never recurse or sort an unbounded directory.
                using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
                while (examined < limits.MaxEntries && entries.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    examined++;
                    var directory = entries.Current;
                    if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) continue;
                    if (runs.Count == limits.MaxRuns || remaining <= 0) { truncated = true; break; }
                    runs.Add(await ReadRunAsync(directory, RunKey(directory), token));
                }
                // Do not advance any enumerator again just to discover whether more work exists.
                if (examined == limits.MaxEntries) truncated = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (ReadFailure(e)) { warnings.Add("ArtifactRootUnreadable"); }
        }
        return new("Ready", scenarios, runs, warnings, truncated || remaining < 0);
    }
    public static IReadOnlyList<DashboardSelection> Select(DashboardSnapshot snapshot, string buildId, string environmentSha256)
        => snapshot.Scenarios.Select(s =>
        {
            var candidates = snapshot.Runs.Where(r => r.ScenarioId == s.ScenarioId && r.DefinitionVersion == s.DefinitionVersion &&
                r.ScenarioSha256 == s.Sha256 && r.BuildId == buildId && r.EnvironmentSha256 == environmentSha256).ToArray();
            // Unknown ordering cannot be silently outranked by a dated success.
            var latest = candidates.OrderByDescending(r => r.StartedAt is null).ThenByDescending(r => r.StartedAt)
                .ThenBy(r => r.Key, StringComparer.Ordinal).FirstOrDefault();
            var state = latest?.State ?? (snapshot.Truncated || snapshot.Warnings.Count != 0 ? "IndexIncomplete" : "Unrun");
            if (candidates.Length > 1 && (latest?.StartedAt is null ||
                candidates.Count(r => r.StartedAt == latest?.StartedAt) > 1)) state = "LatestUnknown";
            if (snapshot.Truncated || snapshot.Warnings.Count != 0) state = "IndexIncomplete";
            return new DashboardSelection(s, latest, state);
        }).ToArray();

    private async ValueTask<DashboardRun> ReadRunAsync(string directory, string key, CancellationToken token)
    {
        var runId = Path.GetFileName(directory);
        RunIdentity? identity = null; string? scenarioId = null; DateTimeOffset? started = null;
        var metadataState = "Ready";
        try
        {
            CheckAllowed(directory);
            using var document = ReadJson(Path.Combine(directory, "run.json"));
            var run = document.RootElement;
            var kind = Text(run, "kind");
            if (!Token(runId) || Text(run, "runId") != runId) throw new InvalidDataException();
            if (kind == "playtestRunStorage")
            {
                if (run.GetProperty("storageVersion").GetInt32() != 1) throw new InvalidDataException();
                // #9 persistence envelope is deliberately not passed to RunDocument validation.
                if (Text(run, "executionState") != "Preparing" || Text(run, "profile") is null)
                    throw new InvalidDataException();
                var settings = run.GetProperty("settings"); var versions = run.GetProperty("versions");
                if (settings.ValueKind != JsonValueKind.Array || versions.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException();
                foreach (var setting in settings.EnumerateArray())
                    if (Text(setting, "name") is null || Text(setting, "source") is null ||
                        !setting.TryGetProperty("value", out var value) ||
                        value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException();
                // Sensitive setting/version values are explicitly null in legitimate sanitized storage.
                foreach (var version in versions.EnumerateObject())
                    if (version.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException();
                if (run.TryGetProperty("identity", out var id)) identity = DecodeIdentity(id);
                if (run.TryGetProperty("startedAt", out var at)) started = at.GetDateTimeOffset();
                if (!run.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException();
                string? scenarioCopyHash = null;
                foreach (var input in inputs.EnumerateArray())
                {
                    if (Text(input, "name") is null || !Hash(Text(input, "sha256")) ||
                        !input.TryGetProperty("document", out var copy) ||
                        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy, Json))) != Text(input, "sha256"))
                        throw new InvalidDataException();
                    if (Text(copy, "kind") == "scenario")
                    {
                        if (scenarioCopyHash is not null && scenarioCopyHash != Text(input, "sha256")) throw new InvalidDataException();
                        scenarioCopyHash = Text(input, "sha256");
                        if (identity is not null && (!Hash(Text(input, "sourceSha256")) ||
                            Text(input, "sourceSha256") != identity.ScenarioSha256)) throw new InvalidDataException();
                        if (!copy.TryGetProperty("scenarioId", out var storedId) ||
                            storedId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException();
                        scenarioId = storedId.ValueKind == JsonValueKind.String ? storedId.GetString() : null;
                        if (!Token(scenarioId))
                        {
                            if (identity is not null || scenarioId is { Length: > 0 }) throw new InvalidDataException();
                            scenarioId = null; // Legacy grouping may be unavailable after legitimate redaction.
                        }
                        if (identity is not null && (scenarioId != identity.ScenarioId ||
                            Text(copy, "definitionVersion") != identity.DefinitionVersion)) throw new InvalidDataException();
                    }
                }
                if (identity is not null && scenarioCopyHash is null) throw new InvalidDataException();
            }
            else if (kind == "run")
            {
                using (var reservation = Open(Path.Combine(directory, "run.json"))) Charge(reservation.Length);
                AdmitWireReferences(run, directory, new HashSet<string>(StringComparer.Ordinal), token, reserveValidationRead: true);
                var validation = await ValidateContract(Path.Combine(directory, "run.json"), allowedRoots.Concat(validationRoots).ToArray(), token);
                token.ThrowIfCancellationRequested();
                if (validation.Code == "Interrupted") throw new OperationCanceledException(token);
                if (!validation.IsValid || validation.Document is not RunDocument wire) throw new InvalidDataException();
                CheckValidationAuthority(validation, token);
                identity = wire.Identity; started = wire.StartedAt;
            }
            else throw new InvalidDataException();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (ReadFailure(e)) { metadataState = "RunUnreadable"; }
        ResultDocument? result = null; var state = "Unsettled";
        try
        {
            // Charge bytes before the independent schema reader allocates its retained copy.
            CheckAllowed(Path.Combine(directory, "result.json"));
            using (var stream = Open(Path.Combine(directory, "result.json"))) { Charge(stream.Length); }
            var read = await RunArtifactReader.ReadResultAsync(directory,
                new(limits.MaxFileBytes, Math.Max(limits.MaxFileBytes, limits.MaxIndexBytes), 10000, limits.MaxFileBytes), token);
            result = read.Result;
            state = read.State switch
            {
                ResultReadState.Verified => result!.Status == ResultStatus.Passed && !result.PostProcessing.Complete
                    ? "PassedPostProcessingIncomplete" : result.Status.ToString(),
                ResultReadState.Missing => "Unsettled", ResultReadState.Interrupted => throw new OperationCanceledException(token),
                _ => "ResultUnreadable"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (ReadFailure(e)) { state = e is FileNotFoundException ? "Unsettled" : "ResultUnreadable"; }
        var details = ReadDetails(directory, key, result, token);
        if (metadataState == "RunUnreadable") { identity = null; scenarioId = null; state = "RunUnreadable"; }
        var displayedRunId = result?.RunId ?? (metadataState == "Ready" ? runId : "Unknown-" + key);
        return new(key, displayedRunId, identity?.ScenarioId ?? scenarioId, identity?.DefinitionVersion, identity?.ScenarioSha256,
            identity?.BuildId, identity?.EnvironmentSha256, started, state, result, details);
    }

    private IReadOnlyList<DashboardDetail> ReadDetails(string directory, string key, ResultDocument? result, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var doc = ReadJson(Path.Combine(directory, "completion.json"));
            var completion = doc.RootElement;
            if (Text(completion, "kind") != "playtestCompletionStorage" || completion.GetProperty("storageVersion").GetInt32() != 1 ||
                Text(completion, "runId") != Path.GetFileName(directory)) throw new InvalidDataException();
            var observations = completion.GetProperty("observations");
            if (observations.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var observation in observations.EnumerateArray())
                if (Text(observation, "boundary") != nameof(ObservationBoundary.AfterCleanup) ||
                    !Token(Text(observation, "observationId"))) throw new InvalidDataException();
            var postProcessing = completion.GetProperty("postProcessing");
            if (postProcessing.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var issue in postProcessing.EnumerateArray())
            {
                if (!Enum.GetNames<PostProcessingReason>().Contains(Text(issue, "reason"), StringComparer.Ordinal))
                    throw new InvalidDataException();
                if (issue.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null)
                {
                    if (exception.ValueKind != JsonValueKind.Object ||
                        exception.GetProperty("type").ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                        (exception.TryGetProperty("stackTrace", out var stack) && stack.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        throw new InvalidDataException();
                }
            }
            var rawReceipts = completion.GetProperty("artifacts");
            if (rawReceipts.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var raw in rawReceipts.EnumerateArray())
                if (!Enum.GetNames<ArtifactKind>().Contains(Text(raw, "kind"), StringComparer.Ordinal) ||
                    !Enum.GetNames<ArtifactState>().Contains(Text(raw, "state"), StringComparer.Ordinal)) throw new InvalidDataException();
            var receipts = JsonSerializer.Deserialize<ArtifactReceipt[]>(rawReceipts, Json);
            if (receipts is null || receipts.Length != 2 || receipts.Any(x => x is null) ||
                receipts.Select(x => x.Kind).Distinct().Count() != 2) throw new InvalidDataException();
            var details = new List<DashboardDetail>();
            var identities = SummaryIdentities(directory);
            var allFiles = receipts.Where(r => r.State == ArtifactState.Saved).SelectMany(r =>
                new[] { new ArtifactFileReference(r.FileName!, r.Bytes ?? 0, r.Sha256!) }.Concat(r.AdditionalFiles ?? [])).ToArray();
            foreach (var receipt in receipts)
            {
                token.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(receipt.State) || !Enum.IsDefined(receipt.Kind)) throw new InvalidDataException();
                if (receipt.State != ArtifactState.Saved)
                {
                    if (receipt.FileName is not null || receipt.Bytes is not null || receipt.Sha256 is not null || receipt.AdditionalFiles is not null ||
                        (receipt.State == ArtifactState.OmittedOnSuccess && result is not null && result.Status != ResultStatus.Passed)) throw new InvalidDataException();
                    details.Add(new(receipt.Kind.ToString(), receipt.State.ToString())); continue;
                }
                var entries = new[] { new ArtifactFileReference(receipt.FileName!, receipt.Bytes ?? 0, receipt.Sha256!) }
                    .Concat(receipt.AdditionalFiles ?? []).ToArray();
                if (entries.Any(x => x is null)) throw new InvalidDataException();
                foreach (var entry in entries) { token.ThrowIfCancellationRequested(); ValidateReference(entry); }
                string? link = null;
                try
                {
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = OpenReceipt(directory, entry); Charge(stream.Length);
                        ReceiptFileOpened?.Invoke(entry.FileName);
                        var identity = FileIdentity.Read(stream.SafeFileHandle);
                        if (!identities.Add(identity)) throw new ReceiptAliasException();
                        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != entry.Sha256) throw new InvalidDataException();
                        if (receipt.Kind == ArtifactKind.Trace && entry.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        {
                            link = $"/reports/{key}";
                            reports[link] = (directory, entry, identity, allFiles);
                        }
                    }
                    details.Add(new(receipt.Kind.ToString(), "Saved", link));
                }
                catch (Exception e) when (ReadFailure(e) && e is not ReceiptAliasException)
                {
                    if (receipt.Kind == ArtifactKind.Trace) reports.Remove($"/reports/{key}");
                    details.Add(new(receipt.Kind.ToString(), e is FileNotFoundException ? "Missing" : "Unreadable"));
                }
            }
            return details;
        }
        catch (Exception e) when (ReadFailure(e))
        { reports.Remove($"/reports/{key}"); return [new("Trace", "CompletionUnreadable"), new("Recording", "CompletionUnreadable")]; }
    }
    public byte[] ReadReport(string route, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!reports.TryGetValue(route, out var reference)) throw new FileNotFoundException();
        using var stream = OpenReceipt(reference.Directory, reference.File);
        ReportFileOpened?.Invoke(reference.File.FileName, token);
        token.ThrowIfCancellationRequested();
        var identity = FileIdentity.Read(stream.SafeFileHandle);
        if (identity != reference.Identity || SummaryIdentities(reference.Directory, token).Contains(identity)) throw new InvalidDataException();
        foreach (var file in reference.Files.Where(f => f != reference.File))
        {
            token.ThrowIfCancellationRequested();
            try { using var other = Open(Path.Combine(reference.Directory, Path.Combine(file.FileName.Split('/'))));
                ReportFileOpened?.Invoke(file.FileName, token); token.ThrowIfCancellationRequested();
                if (FileIdentity.Read(other.SafeFileHandle) == identity) throw new ReceiptAliasException(); }
            catch (Exception e) when (ReadFailure(e) && e is not ReceiptAliasException) { } // Unreadable Recording is independent of Trace.
        }
        token.ThrowIfCancellationRequested();
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.File.Sha256) throw new InvalidDataException();
        token.ThrowIfCancellationRequested();
        return bytes;
    }
    private FileStream OpenReceipt(string directory, ArtifactFileReference file)
    {
        ValidateReference(file);
        var stream = Open(Path.Combine(directory, Path.Combine(file.FileName.Split('/'))));
        if (stream.Length == file.Bytes) return stream;
        stream.Dispose(); throw new InvalidDataException();
    }
    private void ValidateReference(ArtifactFileReference file)
    {
        if (file.FileName is null || file.FileName.Length > 1031 || file.Bytes <= 0 || file.Bytes > limits.MaxFileBytes ||
            !Hash(file.Sha256)) throw new InvalidDataException();
        var parts = file.FileName.Split('/');
        if (parts.Length > 8 || parts.Any(p => !Token(p) || p[^1] == '.') ||
            new[] { "run.json", "primary.json", "completion.json", "result.json" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException();
    }
    private JsonDocument ReadJson(string path)
    {
        using var stream = Open(path); Charge(stream.Length);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        admittedHashes[FileIdentity.Read(stream.SafeFileHandle)] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var validation = JsonDocument.Parse(bytes, new() { MaxDepth = 33 });
        RejectDuplicates(validation.RootElement);
        return JsonDocument.Parse(bytes, new() { MaxDepth = 33 });
    }
    private void AdmitWireReferences(JsonElement document, string directory, HashSet<string> visited, CancellationToken token, bool reserveValidationRead = false)
    {
        token.ThrowIfCancellationRequested();
        if (Text(document, "kind") == "scenarioRegistry")
        {
            var artifactRoots = document.GetProperty("artifactRoots");
            var scenarios = document.GetProperty("scenarios");
            if (artifactRoots.ValueKind != JsonValueKind.Array || artifactRoots.GetArrayLength() > limits.MaxRoots)
                throw new InvalidDataException();
            if (scenarios.ValueKind != JsonValueKind.Array || scenarios.GetArrayLength() > 1000) throw new InvalidDataException();
            foreach (var scenario in scenarios.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                AdmitReference(scenario.GetProperty("file"), directory, visited, token, reserveValidationRead);
            }
            foreach (var root in artifactRoots.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                CheckAllowed(LocalPath(root.GetString()!, directory), token);
            }
            return;
        }
        string[] fields = Text(document, "kind") switch
        { "run" => ["scenario", "environment", "plan"], "replayPlan" => ["scenario", "recording"], _ => [] };
        foreach (var field in fields)
        {
            token.ThrowIfCancellationRequested();
            if (!document.TryGetProperty(field, out var reference) || reference.ValueKind == JsonValueKind.Null) continue;
            AdmitReference(reference, directory, visited, token, reserveValidationRead);
        }
    }
    private void AdmitReference(JsonElement reference, string directory, HashSet<string> visited, CancellationToken token, bool reserveValidationRead)
    {
        token.ThrowIfCancellationRequested();
        var path = LocalPath(reference.GetProperty("path").GetString()!, directory);
        if (!visited.Add(path)) return;
        CheckAllowed(path, token);
        if (visited.Count > 1000) throw new InvalidDataException();
        using var source = ReadContract(path, reserveValidationRead);
        ReferenceAdmitted?.Invoke(path);
        token.ThrowIfCancellationRequested();
        AdmitWireReferences(source.RootElement, Path.GetDirectoryName(path)!, visited, token, reserveValidationRead);
    }
    private JsonDocument ReadContract(string path, bool reserveValidationRead = false)
    {
        using var stream = Open(path); Charge(stream.Length);
        if (reserveValidationRead) Charge(stream.Length); // Reserve the static validator's duplicate read before it allocates.
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        admittedHashes[FileIdentity.Read(stream.SafeFileHandle)] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var inspected = ContractJson.Inspect(bytes, Path.GetExtension(path));
        if (inspected.Document is null) throw new InvalidDataException();
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(inspected.Document), new() { MaxDepth = 64 });
    }
    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); RejectDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectDuplicates(item);
    }
    private FileStream Open(string path)
    {
        CheckAllowed(path); var stream = FileIdentity.OpenRegular(path).Stream;
        if (stream.Length <= limits.MaxFileBytes) return stream;
        stream.Dispose(); throw new InvalidDataException();
    }
    private static string LocalPath(string path, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        RejectNamespace(path);
        var full = directory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, directory);
        RejectNamespace(full);
        if (OperatingSystem.IsWindows() && full[(Path.GetPathRoot(full)?.Length ?? 0)..].Contains(':'))
            throw new InvalidDataException("LocalPathRequired");
        return full;
    }
    private static void RejectNamespace(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith("\\??\\", StringComparison.Ordinal)) throw new InvalidDataException("LocalPathRequired");
    }
    private void CheckAllowed(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var full = LocalPath(path);
        CheckNoLinks(full, token);
        PathProbed?.Invoke(full);
        var identities = new HashSet<FileIdentity>();
        foreach (var root in allowedRoots) { token.ThrowIfCancellationRequested(); CheckNoLinks(root, token); PathProbed?.Invoke(root); identities.Add(FileIdentity.ReadDirectory(root)); }
        for (var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full); directory is not null;
            directory = Path.GetDirectoryName(directory))
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory) && identities.Contains(FileIdentity.ReadDirectory(directory)))
            {
                RunArtifactStore.CheckPath(full);
                // Preserve this spelling of the very same authorized directory for the static
                // validator's lexical check (e.g. case-insensitive APFS). No new identity is granted.
                validationRoots.Add(Path.TrimEndingDirectorySeparator(directory)); return;
            }
        }
        throw new InvalidDataException();
    }
    private static void CheckNoLinks(string path, CancellationToken token)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("LocalRegularPathRequired"); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }
    private void CheckValidationAuthority(StaticValidationReport report, CancellationToken token)
    {
        foreach (var file in report.Files.Values)
        {
            token.ThrowIfCancellationRequested(); CheckAllowed(file.Path, token);
            using var stream = Open(file.Path);
            if (!admittedHashes.TryGetValue(FileIdentity.Read(stream.SafeFileHandle), out var hash) || hash != file.Sha256)
                throw new InvalidDataException("AdmissionChanged");
        }
    }
    private HashSet<FileIdentity> SummaryIdentities(string directory, CancellationToken token = default)
    {
        var identities = new HashSet<FileIdentity>();
        foreach (var name in new[] { ".owner", "run.json", "primary.json", "completion.json", "result.json" })
            try { token.ThrowIfCancellationRequested(); using var file = Open(Path.Combine(directory, name));
                if (!identities.Add(FileIdentity.Read(file.SafeFileHandle))) throw new ReceiptAliasException(); }
            catch (FileNotFoundException) { }
        return identities;
    }
    private sealed class ReceiptAliasException : IOException { }
    private string RunKey(string directory)
    {
        string input;
        try { CheckAllowed(directory); var id = FileIdentity.ReadDirectory(directory);
            input = FormattableString.Invariant($"{id.Device:x16}:{id.Low:x16}:{id.High:x16}:{Path.GetFileName(directory)}"); }
        catch (Exception e) when (ReadFailure(e)) { input = "unreadable:" + Path.GetFullPath(directory); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
    private void Charge(long length)
    { remaining -= length; if (remaining < 0) throw new InvalidDataException("IndexLimit"); }
    private static RunIdentity DecodeIdentity(JsonElement element)
    {
        var id = JsonSerializer.Deserialize<RunIdentity>(element, Json) ?? throw new InvalidDataException();
        if (!Token(id.ScenarioId) || !Token(id.DefinitionVersion) || !Token(id.BuildId) ||
            !Hash(id.ScenarioSha256) || !Hash(id.EnvironmentSha256)) throw new InvalidDataException();
        return id;
    }
    private static bool Token(string? text) => text is { Length: >= 1 and <= 128 } && char.IsAsciiLetterOrDigit(text[0]) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool Hash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? Text(JsonElement node, string property) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException;
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
}
