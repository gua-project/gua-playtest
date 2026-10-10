using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;

namespace Gua.Playtest.Runner.Dashboard;

public sealed record DashboardLimits(int MaxRuns = 1000, int MaxFileBytes = 1024 * 1024,
    long MaxIndexBytes = 32 * 1024 * 1024, int MaxRoots = 16)
{
    internal void Validate()
    {
        if (MaxRuns is < 1 or > 10000 || MaxFileBytes is < 256 or > 16 * 1024 * 1024 ||
            MaxIndexBytes < MaxFileBytes || MaxIndexBytes > 256L * 1024 * 1024 || MaxRoots is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxRuns));
    }
}
public sealed record DashboardScenario(string ScenarioId, string DefinitionVersion, string Sha256, string Name);
public sealed record DashboardDetail(string Kind, string State, string? Report = null);
public sealed record DashboardRun(string Key, string RunId, string? ScenarioId, string? DefinitionVersion,
    string? ScenarioSha256, string? BuildId, string? EnvironmentSha256, DateTimeOffset? StartedAt,
    string State, ResultDocument? Result, IReadOnlyList<DashboardDetail> Details);
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
    private readonly Dictionary<string, (string Directory, ArtifactFileReference File)> reports = [];
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
        reports.Clear(); remaining = limits.MaxIndexBytes;
        var warnings = new List<string>(); var runs = new List<DashboardRun>();
        var scenarios = new List<DashboardScenario>(); var truncated = false;
        try
        {
            CheckAllowed(registry);
            var report = await new StaticContractValidator(allowedRoots).ValidateFileAsync(registry, token);
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
        var visited = new HashSet<FileIdentity>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                CheckAllowed(root);
                if (!visited.Add(FileIdentity.ReadDirectory(root))) continue;
                // Enumerate immediate Run entries only, never recurse or sort an unbounded directory.
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested();
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
            var receipts = JsonSerializer.Deserialize<ArtifactReceipt[]>(completion.GetProperty("artifacts"), Json);
            if (receipts is null || receipts.Length != 2 || receipts.Any(x => x is null) ||
                receipts.Select(x => x.Kind).Distinct().Count() != 2) throw new InvalidDataException();
            var details = new List<DashboardDetail>();
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
                string? link = null;
                try
                {
                    foreach (var entry in entries)
                    {
                        using var stream = OpenReceipt(directory, entry); Charge(stream.Length);
                        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != entry.Sha256) throw new InvalidDataException();
                        if (receipt.Kind == ArtifactKind.Trace && entry.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        {
                            link = $"/reports/{key}";
                            reports[link] = (directory, entry);
                        }
                    }
                    details.Add(new(receipt.Kind.ToString(), "Saved", link));
                }
                catch (Exception e) when (ReadFailure(e))
                {
                    reports.Remove($"/reports/{key}");
                    details.Add(new(receipt.Kind.ToString(), e is FileNotFoundException ? "Missing" : "Unreadable"));
                }
            }
            return details;
        }
        catch (Exception e) when (ReadFailure(e)) { return [new("Trace", "CompletionUnreadable"), new("Recording", "CompletionUnreadable")]; }
    }
    public byte[] ReadReport(string route)
    {
        if (!reports.TryGetValue(route, out var reference)) throw new FileNotFoundException();
        using var stream = OpenReceipt(reference.Directory, reference.File);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.File.Sha256) throw new InvalidDataException();
        return bytes;
    }
    private FileStream OpenReceipt(string directory, ArtifactFileReference file)
    {
        if (file.FileName is null || file.FileName.Length > 1031 || file.Bytes <= 0 || file.Bytes > limits.MaxFileBytes ||
            !Hash(file.Sha256)) throw new InvalidDataException();
        var parts = file.FileName.Split('/');
        if (parts.Length > 8 || parts.Any(p => !Token(p) || p[^1] == '.') ||
            parts[0] is "run.json" or "primary.json" or "completion.json" or "result.json") throw new InvalidDataException();
        var stream = Open(Path.Combine(directory, Path.Combine(parts)));
        if (stream.Length == file.Bytes) return stream;
        stream.Dispose(); throw new InvalidDataException();
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
        string[] fields = Text(document, "kind") switch
        { "run" => ["scenario", "environment", "plan"], "replayPlan" => ["scenario", "recording"], _ => [] };
        foreach (var field in fields)
        {
            if (!document.TryGetProperty(field, out var reference) || reference.ValueKind == JsonValueKind.Null) continue;
            var path = Path.GetFullPath(reference.GetProperty("path").GetString()!, directory);
            CheckAllowed(path);
            if (!visited.Add(path)) continue;
            if (visited.Count > 16) throw new InvalidDataException();
            using var source = ReadJson(path);
            AdmitWireReferences(source.RootElement, Path.GetDirectoryName(path)!, visited);
        }
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
        if (!allowedRoots.Any(root => full == Path.TrimEndingDirectorySeparator(root) ||
            full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) throw new InvalidDataException();
        RunArtifactStore.CheckPath(full);
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
