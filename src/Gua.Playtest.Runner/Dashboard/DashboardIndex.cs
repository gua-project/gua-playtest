using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;

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
    private readonly Dictionary<string, (string Directory, ArtifactFileReference File, FileIdentity Identity, ArtifactFileReference[] Files)> reports = [];
    private long remaining;
    public DashboardIndex(string registry, IEnumerable<string> artifactRoots, IEnumerable<string> allowedRoots,
        DashboardLimits? limits = null)
    {
        this.registry = Path.GetFullPath(registry);
        roots = artifactRoots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        this.allowedRoots = allowedRoots.Select(Path.GetFullPath).ToArray();
        this.limits = limits ?? new(); this.limits.Validate();
        if (roots.Length is 0 || roots.Length > this.limits.MaxRoots || this.allowedRoots.Length is 0 ||
            this.allowedRoots.Length > this.limits.MaxRoots) throw new ArgumentException("ExplicitRootsRequired");
    }
    public async ValueTask<DashboardSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        reports.Clear(); remaining = limits.MaxIndexBytes;
        var warnings = new List<string>(); var runs = new List<DashboardRun>();
        var scenarios = new List<DashboardScenario>(); var truncated = false;
        try
        {
            CheckAllowed(registry);
            using (var admission = ReadContract(registry))
                AdmitWireReferences(admission.RootElement, Path.GetDirectoryName(registry)!, new HashSet<string>(StringComparer.Ordinal));
            var report = await new StaticContractValidator(allowedRoots).ValidateFileAsync(registry, token);
            token.ThrowIfCancellationRequested();
            if (!report.IsValid || report.Document is not ScenarioRegistryDocument population)
                return new(report.Code == "Valid" ? "RegistryInvalid" : report.Code, [], [], [], false);
            foreach (var entry in population.Scenarios)
            {
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
            try
            {
                CheckAllowed(root);
                if (!visited.Add(FileIdentity.ReadDirectory(root))) continue;
                // Enumerate immediate Run entries only, never recurse or sort an unbounded directory.
                foreach (var directory in Directory.EnumerateFileSystemEntries(root))
                {
                    token.ThrowIfCancellationRequested();
                    if (++examined > limits.MaxEntries) { truncated = true; break; }
                    if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) continue;
                    if (runs.Count == limits.MaxRuns || remaining <= 0) { truncated = true; break; }
                    runs.Add(await ReadRunAsync(directory, runs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), token));
                }
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
            if (Text(run, "runId") != runId) throw new InvalidDataException();
            if (kind == "playtestRunStorage")
            {
                if (run.GetProperty("storageVersion").GetInt32() != 1) throw new InvalidDataException();
                // #9 persistence envelope is deliberately not passed to RunDocument validation.
                if (run.TryGetProperty("identity", out var id)) identity = DecodeIdentity(id);
                if (run.TryGetProperty("startedAt", out var at)) started = at.GetDateTimeOffset();
                if (run.TryGetProperty("inputs", out var inputs))
                    foreach (var input in inputs.EnumerateArray())
                        if (input.TryGetProperty("document", out var copy) && Text(copy, "kind") == "scenario")
                        {
                            scenarioId = Text(copy, "scenarioId");
                            if (identity is not null && (scenarioId != identity.ScenarioId ||
                                Text(copy, "definitionVersion") != identity.DefinitionVersion)) throw new InvalidDataException();
                        }
            }
            else if (kind == "run")
            {
                AdmitWireReferences(run, directory, new HashSet<string>(StringComparer.Ordinal));
                var validation = await new StaticContractValidator(allowedRoots).ValidateFileAsync(Path.Combine(directory, "run.json"), token);
                if (!validation.IsValid || validation.Document is not RunDocument wire) throw new InvalidDataException();
                foreach (var file in validation.Files.Values) { CheckAllowed(file.Path); Charge(file.ByteLength); }
                identity = wire.Identity; started = wire.StartedAt;
            }
            else throw new InvalidDataException();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (ReadFailure(e)) { metadataState = e is FileNotFoundException ? "IdentityUnknown" : "RunUnreadable"; }
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
        var details = ReadDetails(directory, key, result);
        if (metadataState == "RunUnreadable") { identity = null; state = "RunUnreadable"; }
        return new(key, runId, identity?.ScenarioId ?? scenarioId, identity?.DefinitionVersion, identity?.ScenarioSha256,
            identity?.BuildId, identity?.EnvironmentSha256, started, state, result, details);
    }

    private IReadOnlyList<DashboardDetail> ReadDetails(string directory, string key, ResultDocument? result)
    {
        try
        {
            using var doc = ReadJson(Path.Combine(directory, "completion.json"));
            var completion = doc.RootElement;
            if (Text(completion, "kind") != "playtestCompletionStorage" || completion.GetProperty("storageVersion").GetInt32() != 1 ||
                Text(completion, "runId") != Path.GetFileName(directory)) throw new InvalidDataException();
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
                if (!Enum.IsDefined(receipt.State) || !Enum.IsDefined(receipt.Kind)) throw new InvalidDataException();
                if (receipt.State != ArtifactState.Saved)
                {
                    if (receipt.FileName is not null || receipt.Bytes is not null || receipt.Sha256 is not null || receipt.AdditionalFiles is not null ||
                        (receipt.State == ArtifactState.OmittedOnSuccess && result?.Status != ResultStatus.Passed)) throw new InvalidDataException();
                    details.Add(new(receipt.Kind.ToString(), receipt.State.ToString())); continue;
                }
                var entries = new[] { new ArtifactFileReference(receipt.FileName!, receipt.Bytes ?? 0, receipt.Sha256!) }
                    .Concat(receipt.AdditionalFiles ?? []).ToArray();
                if (entries.Any(x => x is null)) throw new InvalidDataException();
                foreach (var entry in entries) ValidateReference(entry);
                string? link = null;
                try
                {
                    foreach (var entry in entries)
                    {
                        using var stream = OpenReceipt(directory, entry); Charge(stream.Length);
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
    public byte[] ReadReport(string route)
    {
        if (!reports.TryGetValue(route, out var reference)) throw new FileNotFoundException();
        using var stream = OpenReceipt(reference.Directory, reference.File);
        var identity = FileIdentity.Read(stream.SafeFileHandle);
        if (identity != reference.Identity || SummaryIdentities(reference.Directory).Contains(identity)) throw new InvalidDataException();
        foreach (var file in reference.Files.Where(f => f != reference.File))
        {
            try { using var other = Open(Path.Combine(reference.Directory, Path.Combine(file.FileName.Split('/'))));
                if (FileIdentity.Read(other.SafeFileHandle) == identity) throw new ReceiptAliasException(); }
            catch (Exception e) when (ReadFailure(e) && e is not ReceiptAliasException) { } // Unreadable Recording is independent of Trace.
        }
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.File.Sha256) throw new InvalidDataException();
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
        using var validation = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        RejectDuplicates(validation.RootElement);
        return JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
    }
    private void AdmitWireReferences(JsonElement document, string directory, HashSet<string> visited)
    {
        if (Text(document, "kind") == "scenarioRegistry")
        {
            foreach (var scenario in document.GetProperty("scenarios").EnumerateArray())
                AdmitReference(scenario.GetProperty("file"), directory, visited);
            return;
        }
        string[] fields = Text(document, "kind") switch
        { "run" => ["scenario", "environment", "plan"], "replayPlan" => ["scenario", "recording"], _ => [] };
        foreach (var field in fields)
        {
            if (!document.TryGetProperty(field, out var reference) || reference.ValueKind == JsonValueKind.Null) continue;
            AdmitReference(reference, directory, visited);
        }
    }
    private void AdmitReference(JsonElement reference, string directory, HashSet<string> visited)
    {
        var path = Path.GetFullPath(reference.GetProperty("path").GetString()!, directory);
        CheckAllowed(path);
        if (!visited.Add(path)) return;
        if (visited.Count > 1000) throw new InvalidDataException();
        using var source = ReadContract(path);
        AdmitWireReferences(source.RootElement, Path.GetDirectoryName(path)!, visited);
    }
    private JsonDocument ReadContract(string path)
    {
        using var stream = Open(path); Charge(stream.Length);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
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
    private void CheckAllowed(string path)
    {
        var full = Path.GetFullPath(path);
        var identities = allowedRoots.Select(FileIdentity.ReadDirectory).ToHashSet();
        for (var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full); directory is not null;
            directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(directory) && identities.Contains(FileIdentity.ReadDirectory(directory)))
            { RunArtifactStore.CheckPath(full); return; }
        throw new InvalidDataException();
    }
    private HashSet<FileIdentity> SummaryIdentities(string directory)
    {
        var identities = new HashSet<FileIdentity>();
        foreach (var name in new[] { ".owner", "run.json", "primary.json", "completion.json", "result.json" })
            try { using var file = Open(Path.Combine(directory, name)); identities.Add(FileIdentity.Read(file.SafeFileHandle)); }
            catch (FileNotFoundException) { }
        return identities;
    }
    private sealed class ReceiptAliasException : IOException { }
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
