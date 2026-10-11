using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Dashboard;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Replay;
using Gua.Testing;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class DashboardTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "dashboard-tests-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(root, "registry.json");
    private string Artifacts => Path.Combine(root, "runs");
    private static string EnvironmentHash => new('a', 64);
    private static ArtifactLimits Limits => new(1024 * 1024, 8 * 1024 * 1024, 10000, 65536);
    public DashboardTests() { Directory.CreateDirectory(Artifacts); Register(); }
    private string Register(string objective = "Reach target")
    {
        var scenario = new ScenarioDocument("s1", "v1", "First <script>Scenario</script>", new(objective, null, null), new(1000, 2, null), null);
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(scenario));
        File.WriteAllBytes(Path.Combine(root, "s1.json"), bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var second = new ScenarioDocument("s2", "v1", "Unrun Scenario", new("Second", null, null), new(1000, 2, null), null);
        var secondBytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(second));
        File.WriteAllBytes(Path.Combine(root, "s2.json"), secondBytes);
        File.WriteAllText(Registry, ContractJson.Serialize(new ScenarioRegistryDocument("registry",
            [new("s1", "v1", new("s1.json", hash)), new("s2", "v1", new("s2.json", Convert.ToHexStringLower(SHA256.HashData(secondBytes))))], ["runs"])));
        return hash;
    }
    private RunIdentity Identity(string build = "buildA") => new("s1", "v1",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "s1.json")))), build, EnvironmentHash);
    private DashboardIndex Index(DashboardLimits? limits = null) => new(Registry, [Artifacts], [root], limits);
    private RunArtifactStore Save(ResultStatus status = ResultStatus.Passed, int day = 1, string build = "buildA",
        bool incomplete = false, bool identity = true, bool settled = true, ArtifactState detail = ArtifactState.OmittedOnSuccess)
    {
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "s1.json")));
        Assert.True(store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [new("scenario", scenario.RootElement)],
            identity ? Identity(build) : null, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero))).Saved);
        var reason = status switch
        { ResultStatus.Passed => RunReason.GoalSatisfied, ResultStatus.Failed => RunReason.ActionFailed,
            ResultStatus.TimedOut => RunReason.MaxDuration, ResultStatus.Aborted => RunReason.Cancelled,
            ResultStatus.Invalid => RunReason.InvalidContract, _ => RunReason.ExplorationFinished };
        var primary = new PrimaryResult(status, new(reason, RunPhase.Execution, RunOrigin.Host));
        Assert.True(store.ConfirmPrimary(primary, [], [], []).Saved);
        if (settled)
            Assert.True(store.Complete(new(primary, [], incomplete ? [new(PostProcessingReason.ResourceReleaseUnconfirmed)] : [], []),
                [new(ArtifactKind.Trace, status == ResultStatus.Passed ? detail : ArtifactState.NotExecuted),
                    new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], new DateTimeOffset(2026, 1, day, 1, 0, 0, TimeSpan.Zero)).Saved);
        return store;
    }
    [Fact]
    public async Task Current_content_build_environment_and_latest_failure_are_independent()
    {
        Save(); var failed = Save(ResultStatus.Failed, 2); Save(day: 3, build: "buildB");
        var snapshot = await Index().ReadAsync(); Assert.Equal("Ready", snapshot.RegistryState);
        var selected = DashboardIndex.Select(snapshot, "buildA", EnvironmentHash);
        Assert.Equal("Failed", selected[0].State); Assert.Equal(failed.RunId, selected[0].Latest!.RunId);
        Assert.Equal("Unrun", selected[1].State); Assert.Equal(3, snapshot.Runs.Count);
        Assert.Equal("Passed", DashboardIndex.Select(snapshot, "buildB", EnvironmentHash)[0].State);
        Assert.Equal("Unrun", DashboardIndex.Select(snapshot, "buildA", new string('b', 64))[0].State);
        Register("Changed content with unchanged version");
        Assert.Equal("Unrun", DashboardIndex.Select(await Index().ReadAsync(), "buildA", EnvironmentHash)[0].State);
    }
    [Theory]
    [InlineData(ResultStatus.Passed)] [InlineData(ResultStatus.Failed)] [InlineData(ResultStatus.TimedOut)]
    [InlineData(ResultStatus.Aborted)] [InlineData(ResultStatus.Invalid)] [InlineData(ResultStatus.Unverified)]
    public async Task Saved_status_is_not_regraded(ResultStatus status)
    {
        Save(status); var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal(status.ToString(), run.State); Assert.Equal(status, run.Result!.Status);
    }
    [Fact]
    public async Task Missing_old_identity_stays_unknown_but_result_is_retained()
    {
        Save(identity: false); var snapshot = await Index().ReadAsync();
        Assert.Null(snapshot.Runs[0].BuildId); Assert.Null(snapshot.Runs[0].ScenarioSha256);
        Assert.Equal(ResultStatus.Passed, snapshot.Runs[0].Result!.Status);
        Assert.Equal("Unrun", DashboardIndex.Select(snapshot, "buildA", EnvironmentHash)[0].State);
        Assert.Equal("s1", snapshot.Runs[0].ScenarioId);
    }
    [Fact]
    public async Task Unsettled_corrupt_read_failures_and_exit11_remain_distinct()
    {
        var pending = Save(settled: false); var corrupt = Save(day: 2); var incomplete = Save(day: 3, incomplete: true);
        File.WriteAllText(Path.Combine(corrupt.DirectoryPath, "result.json"), "{broken");
        var snapshot = await Index().ReadAsync();
        Assert.Equal("Unsettled", snapshot.Runs.Single(x => x.RunId == pending.RunId).State);
        Assert.Equal("ResultUnreadable", snapshot.Runs.Single(x => x.RunId == corrupt.RunId).State);
        var run = snapshot.Runs.Single(x => x.RunId == incomplete.RunId);
        Assert.Equal("PassedPostProcessingIncomplete", run.State); Assert.False(run.Result!.PostProcessing.Complete);
        File.WriteAllText(Path.Combine(incomplete.DirectoryPath, "run.json"), "{}");
        Assert.Equal("RunUnreadable", (await Index().ReadAsync()).Runs.Single(x => x.RunId == incomplete.RunId).State);
    }
    [Theory]
    [InlineData(ArtifactState.OmittedOnSuccess)] [InlineData(ArtifactState.NotExecuted)]
    [InlineData(ArtifactState.CaptureFailed)] [InlineData(ArtifactState.SaveFailed)]
    public async Task Detail_states_are_separate(ArtifactState state)
    { Save(detail: state); Assert.Equal(state.ToString(), (await Index().ReadAsync()).Runs[0].Details[0].State); }
    [Fact]
    public async Task Limits_and_root_failure_cannot_claim_unrun_or_latest_pass()
    {
        Save(); Save(day: 2);
        var limited = await Index(new(MaxRuns: 1)).ReadAsync(); Assert.True(limited.Truncated);
        Assert.All(DashboardIndex.Select(limited, "buildA", EnvironmentHash), x => Assert.Equal("IndexIncomplete", x.State));
        var missing = new DashboardIndex(Registry, [Path.Combine(root, "absent")], [root]);
        var unreadable = await missing.ReadAsync(); Assert.Equal(["ArtifactRootUnreadable"], unreadable.Warnings);
        Assert.All(DashboardIndex.Select(unreadable, "buildA", EnvironmentHash), x => Assert.Equal("IndexIncomplete", x.State));
    }
    [Fact]
    public async Task Unknown_start_does_not_hide_ordering_uncertainty()
    {
        var store = Save(); Save(ResultStatus.Failed, 2);
        var file = Path.Combine(store.DirectoryPath, "run.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!.AsObject(); json.Remove("startedAt");
        File.WriteAllText(file, json.ToJsonString());
        Assert.Equal("LatestUnknown", DashboardIndex.Select(await Index().ReadAsync(), "buildA", EnvironmentHash)[0].State);
    }
    [Fact]
    public async Task Explicit_filesystem_root_authorizes_existing_descendants()
    {
        Save(); var drive = Path.GetPathRoot(root)!;
        var snapshot = await new DashboardIndex(Registry, [Artifacts], [drive]).ReadAsync();
        Assert.Equal("Ready", snapshot.RegistryState); Assert.Equal("Passed", DashboardIndex.Select(snapshot, "buildA", EnvironmentHash)[0].State);
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"kind\":\"Trace\"}")] [InlineData("{\"state\":\"NotExecuted\"}")]
    [InlineData("{\"kind\":null,\"state\":\"NotExecuted\"}")] [InlineData("{\"kind\":\"Trace\",\"state\":null}")]
    public async Task Receipt_requires_explicit_string_kind_and_state(string receipt)
    {
        var store = Save(); var file = Path.Combine(store.DirectoryPath, "completion.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!;
        json["artifacts"]![0] = System.Text.Json.Nodes.JsonNode.Parse(receipt); File.WriteAllText(file, json.ToJsonString());
        Assert.All((await Index().ReadAsync()).Runs[0].Details, detail => Assert.Equal("CompletionUnreadable", detail.State));
    }
    [Theory]
    [InlineData("run.json")] [InlineData("primary.json")] [InlineData("completion.json")] [InlineData("result.json")]
    public async Task Case_equivalent_summary_names_are_not_artifact_receipts(string name)
    {
        var store = Save(); var file = Path.Combine(store.DirectoryPath, "completion.json");
        var bytes = File.ReadAllBytes(Path.Combine(store.DirectoryPath, name));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!;
        json["artifacts"]![0] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
        { kind = "Trace", state = "Saved", fileName = name.ToUpperInvariant(), bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
        File.WriteAllText(file, json.ToJsonString());
        Assert.All((await Index().ReadAsync()).Runs[0].Details, detail => Assert.Equal("CompletionUnreadable", detail.State));
    }
    [Theory]
    [InlineData("scenarioId")] [InlineData("definitionVersion")] [InlineData("scenarioSha256")] [InlineData("buildId")] [InlineData("environmentSha256")]
    public void Identity_value_redaction_collisions_reject_without_publication(string field)
    {
        var identity = Identity();
        var secret = field switch { "scenarioId" => identity.ScenarioId, "definitionVersion" => identity.DefinitionVersion,
            "scenarioSha256" => identity.ScenarioSha256, "buildId" => identity.BuildId, _ => identity.EnvironmentSha256 };
        var store = RunArtifactStore.Create(Artifacts, Limits, new([secret], []));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [], identity)).Failure);
        Assert.Equal([".owner"], Directory.GetFiles(store.DirectoryPath).Select(path => Path.GetFileName(path)!).ToArray());
    }
    [Fact]
    public void Valid_identity_is_retained_exactly_in_storage()
    {
        var identity = Identity(); var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        Assert.True(store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [], identity)).Saved);
        using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "run.json")));
        var saved = file.RootElement.GetProperty("identity");
        Assert.Equal(identity.ScenarioId, saved.GetProperty("scenarioId").GetString());
        Assert.Equal(identity.DefinitionVersion, saved.GetProperty("definitionVersion").GetString());
        Assert.Equal(identity.ScenarioSha256, saved.GetProperty("scenarioSha256").GetString());
        Assert.Equal(identity.BuildId, saved.GetProperty("buildId").GetString());
        Assert.Equal(identity.EnvironmentSha256, saved.GetProperty("environmentSha256").GetString());
    }
    [Fact]
    public async Task Registry_yaml_and_references_obey_regular_file_admission()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Registry))!;
        var yaml = Path.Combine(root, "registry.yaml");
        File.WriteAllText(yaml, $"kind: scenarioRegistry\nschemaVersion: 1\nregistryId: registry\nscenarios:\n  - scenarioId: s1\n    definitionVersion: v1\n    file:\n      path: s1.json\n      sha256: {Identity().ScenarioSha256}\nartifactRoots:\n  - runs\n");
        Assert.Equal("Ready", (await new DashboardIndex(yaml, [Artifacts], [root]).ReadAsync()).RegistryState);
        var source = Path.Combine(root, "s1.json"); var link = Path.Combine(root, "linked.json");
        if (OperatingSystem.IsWindows())
        {
            // Junctions do not require developer-mode symlink privilege.
            var linkedDirectory = Path.Combine(root, "linked");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { Arguments = $"/c mklink /J \"{linkedDirectory}\" \"{root}\"", UseShellExecute = false, CreateNoWindow = true });
            Assert.NotNull(process); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            json["scenarios"]![0]!["file"]!["path"] = "linked/s1.json";
            File.WriteAllText(Registry, json.ToJsonString());
            try { Assert.Equal("RegistryUnreadable", (await Index().ReadAsync()).RegistryState); }
            finally { Directory.Delete(linkedDirectory); }
        }
        else
        {
            File.CreateSymbolicLink(link, source); json["scenarios"]![0]!["file"]!["path"] = "linked.json";
            File.WriteAllText(Registry, json.ToJsonString());
            Assert.Equal("RegistryUnreadable", (await Index().ReadAsync()).RegistryState); File.Delete(link);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo")
            { ArgumentList = { link }, UseShellExecute = false });
            Assert.NotNull(process); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            try { Assert.Equal("RegistryUnreadable", (await Index().ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).RegistryState); }
            finally { File.Delete(link); }
        }
    }
    [Fact]
    public async Task Timestamp_order_keeps_100ns_precision_and_normalizes_offsets()
    {
        var first = Save(); var second = Save(ResultStatus.Failed);
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        void SetStart(RunArtifactStore store, DateTimeOffset start)
        {
            var file = Path.Combine(store.DirectoryPath, "run.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!; json["startedAt"] = start;
            File.WriteAllText(file, json.ToJsonString());
        }
        SetStart(first, instant.AddTicks(1)); SetStart(second, instant.AddTicks(2));
        var snapshot = await Index().ReadAsync();
        Assert.Equal("Failed", DashboardIndex.Select(snapshot, "buildA", EnvironmentHash)[0].State);
        Assert.True(string.CompareOrdinal(snapshot.Runs.Single(r => r.RunId == second.RunId).StartedOrder,
            snapshot.Runs.Single(r => r.RunId == first.RunId).StartedOrder) > 0);
        SetStart(first, instant); SetStart(second, instant.ToOffset(TimeSpan.FromHours(1)));
        snapshot = await Index().ReadAsync(); Assert.Equal(snapshot.Runs[0].StartedOrder, snapshot.Runs[1].StartedOrder);
        Assert.Equal("LatestUnknown", DashboardIndex.Select(snapshot, "buildA", EnvironmentHash)[0].State);
    }
    [Fact]
    public async Task Equal_start_times_do_not_claim_which_result_is_latest()
    {
        Save(); Save(ResultStatus.Failed);
        Assert.Equal("LatestUnknown", DashboardIndex.Select(await Index().ReadAsync(), "buildA", EnvironmentHash)[0].State);
    }
    [Fact]
    public async Task Real_executor_and_persistence_results_match_independent_expected_unverified()
    {
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        Assert.True(store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [], Identity(), DateTimeOffset.UtcNow)).Saved);
        var clock = new MonotonicClock(); var duration = TimeSpan.FromSeconds(2);
        var run = new RunSession(new(duration, duration, duration, duration, duration, duration, 2, 2, 2, 100), clock, clock);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true),
            (_, _) => ValueTask.FromResult(true), confirmPrimary: (snapshot, token) =>
                ValueTask.FromResult(store.ConfirmPrimary(snapshot, [], [], token).Saved));
        Assert.Equal(ResultStatus.Unverified, outcome.Primary.Status);
        Assert.True(store.Complete(outcome, [new(ArtifactKind.Trace, ArtifactState.NotExecuted),
            new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        Assert.Equal("Unverified", DashboardIndex.Select(await Index().ReadAsync(), "buildA", EnvironmentHash)[0].State);
    }
    [Fact]
    public async Task Gua_common_report_links_revalidate_and_missing_detail_is_not_omission()
    {
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        Assert.True(store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [], Identity(), DateTimeOffset.UtcNow)).Saved);
        var primary = new PrimaryResult(ResultStatus.Failed, new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host));
        Assert.True(store.ConfirmPrimary(primary, [], [], []).Saved);
        using var trace = new GuaTraceSession(new GuaTraceOptions
        { OutputDirectory = Path.Combine(store.DirectoryPath, "gua"), SavePolicy = GuaTraceSavePolicy.Always, Profile = "player" });
        var step = trace.BeginStep(GuaTraceStepKind.Mark, "dashboard-viewer-fixture"); trace.EndStep(step, GuaTraceOutcome.Failed);
        Assert.True(await trace.CompleteAsync(GuaTraceOutcome.Failed));
        var html = Path.Combine(store.DirectoryPath, "report.html");
        Assert.True(GuaTraceReport.WriteHtml(trace.ArtifactPath, html).Succeeded);
        var files = Directory.GetFiles(store.DirectoryPath, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ".owner" and not "run.json" and not "primary.json")
            .Select(file => { var bytes = File.ReadAllBytes(file); return new ArtifactFileReference(
                Path.GetRelativePath(store.DirectoryPath, file).Replace('\\', '/'), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))); }).ToArray();
        var manifest = files.Single(file => file.FileName.EndsWith("manifest.json", StringComparison.Ordinal));
        Assert.True(store.Complete(new(primary, [], [], []), [new(ArtifactKind.Trace, ArtifactState.Saved,
            manifest.FileName, manifest.Bytes, manifest.Sha256, files.Where(file => file != manifest).ToArray()),
            new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        var index = Index(); var snapshot = await index.ReadAsync(); var detail = snapshot.Runs[0].Details[0];
        Assert.Equal("Saved", detail.State); Assert.NotNull(detail.Report);
        Assert.Contains("dashboard-viewer-fixture", Encoding.UTF8.GetString(index.ReadReport(detail.Report)));
        Assert.Throws<FileNotFoundException>(() => index.ReadReport("/reports/../../result.json"));
        File.WriteAllText(html, "changed"); Assert.Throws<InvalidDataException>(() => index.ReadReport(detail.Report));
        Assert.Equal("Unreadable", (await index.ReadAsync()).Runs[0].Details[0].State);
        File.Delete(html); Assert.Equal("Missing", (await index.ReadAsync()).Runs[0].Details[0].State);
    }
    [Fact]
    public async Task Duplicate_metadata_and_paths_outside_authority_fail_closed()
    {
        var store = Save(); var file = Path.Combine(store.DirectoryPath, "run.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"kind\":", "\"kind\":\"playtestRunStorage\",\"kind\":"));
        var snapshot = await Index().ReadAsync(); Assert.Equal("RunUnreadable", snapshot.Runs[0].State);
        Assert.Null(snapshot.Runs[0].BuildId);
        var outside = new DashboardIndex(Registry, [Path.GetDirectoryName(root)!], [root]);
        Assert.Equal(["ArtifactRootUnreadable"], (await outside.ReadAsync()).Warnings);
    }
    [Fact]
    public async Task Public_run_references_use_caller_authority_and_bounded_admission()
    {
        var store = Save();
        var environment = new EnvironmentDocument("env", "buildA", new("ws://127.0.0.1:9876", null), "Player",
            new(1000, 2, 2, 1000, 1000, 1000, 1000, 1000, 10, 1000, 1000, 10, 64, 2, 2, 2, 1000, 10, 10, 1000),
            new([], false, []), new("recent", "onFailure"), null);
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(environment)); File.WriteAllBytes(Path.Combine(root, "environment.json"), bytes);
        var run = new RunDocument(store.RunId, Identity(), "Explore", ExecutionState.Finished, DateTimeOffset.UtcNow,
            new("../../s1.json", Identity().ScenarioSha256), new("../../environment.json", Convert.ToHexStringLower(SHA256.HashData(bytes))), null);
        var file = Path.Combine(store.DirectoryPath, "run.json"); File.WriteAllText(file, ContractJson.Serialize(run));
        Assert.Equal("Valid", (await new StaticContractValidator([root]).ValidateFileAsync(file)).Code);
        var snapshot = await Index().ReadAsync(); Assert.Equal("Passed", DashboardIndex.Select(snapshot, "buildA", EnvironmentHash)[0].State);
        var outside = run with { Scenario = new("../../../outside.json", run.Scenario.Sha256) };
        File.WriteAllText(file, ContractJson.Serialize(outside));
        Assert.Equal("RunUnreadable", (await Index().ReadAsync()).Runs[0].State);
    }
    [Fact]
    public async Task Corrupt_null_receipts_and_bundle_entries_do_not_crash_snapshot()
    {
        var store = Save(); var file = Path.Combine(store.DirectoryPath, "completion.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!.AsObject();
        json["artifacts"]![0] = null; File.WriteAllText(file, json.ToJsonString());
        Assert.All((await Index().ReadAsync()).Runs.Single(r => r.RunId == store.RunId).Details, detail => Assert.Equal("CompletionUnreadable", detail.State));
        Save(ResultStatus.Failed, day: 2);
        Assert.Equal("Failed", DashboardIndex.Select(await Index().ReadAsync(), "buildA", EnvironmentHash)[0].State);
        json["artifacts"]![0] = System.Text.Json.Nodes.JsonNode.Parse("""{"kind":"Trace","state":"Saved","fileName":"trace.gua","bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","additionalFiles":[null]}""");
        File.WriteAllText(file, json.ToJsonString());
        Assert.All((await Index().ReadAsync()).Runs.Single(r => r.RunId == store.RunId).Details, detail => Assert.Equal("CompletionUnreadable", detail.State));
    }
    [Fact]
    public async Task Packaged_assets_read_only_http_refresh_and_rejected_requests()
    {
        Save(); var before = Directory.GetFiles(Artifacts, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var cancellation = new CancellationTokenSource(); var ready = new TaskCompletionSource<Uri>();
        var server = DashboardApplication.RunAsync(Index(), port, uri => ready.SetResult(uri), cancellation.Token);
        var origin = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var client = new HttpClient { BaseAddress = origin };
        var html = await client.GetStringAsync("/"); Assert.Contains("Registered Scenarios", html);
        Assert.Contains("<option>Unrun</option>", html);
        Assert.Contains("textContent", await client.GetStringAsync("/dashboard.js"));
        Assert.Contains("color-scheme", await client.GetStringAsync("/dashboard.css"));
        for (var i = 0; i < 3; i++) Assert.Contains("Passed", await client.GetStringAsync("/api/refresh"));
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetStringAsync("/api/refresh")));
        foreach (var json in simultaneous)
        {
            using var snapshot = JsonDocument.Parse(json);
            Assert.Equal(2, snapshot.RootElement.GetProperty("scenarios").GetArrayLength());
            Assert.Equal(1, snapshot.RootElement.GetProperty("runs").GetArrayLength());
            Assert.False(snapshot.RootElement.GetProperty("truncated").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/result.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/refresh", null)).StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/refresh"); hostile.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostile)).StatusCode);
        using var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/api/snapshot"); hostileHost.Headers.Host = $"127.0.0.2:{port}";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostileHost)).StatusCode);
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        Assert.Equal(before.Count, Directory.GetFiles(Artifacts, "*", SearchOption.AllDirectories).Length);
        var completion = before.Keys.Single(file => Path.GetFileName(file) == "completion.json");
        var broken = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(completion))!.AsObject(); broken["artifacts"]![0] = null;
        File.WriteAllText(completion, broken.ToJsonString());
        Assert.Contains("CompletionUnreadable", await client.GetStringAsync("/api/refresh"));
        Save(ResultStatus.Failed, day: 2);
        Assert.Contains("Failed", await client.GetStringAsync("/api/refresh"));
        Assert.Contains("CompletionUnreadable", await client.GetStringAsync("/api/snapshot"));
        cancellation.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10));
        using var released = new TcpListener(IPAddress.Loopback, port); released.Start(); released.Stop();
    }
    [Fact]
    public async Task Ordinary_files_count_against_finite_index_work()
    {
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(Artifacts, $"noise-{i}"), "x");
        var snapshot = await Index(new(MaxEntries: 2)).ReadAsync();
        Assert.True(snapshot.Truncated); Assert.Empty(snapshot.Runs);
        Assert.All(DashboardIndex.Select(snapshot, "buildA", EnvironmentHash), item => Assert.Equal("IndexIncomplete", item.State));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index().ReadAsync(canceled.Token).AsTask());
    }
    [Fact]
    public async Task Canceled_start_never_calls_ready_for_a_stopped_listener()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var ready = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DashboardApplication.RunAsync(Index(), 17839, _ => ready = true, canceled.Token));
        Assert.False(ready);
    }
    [Theory]
    [InlineData("scenarioId", "")] [InlineData("definitionVersion", "bad/version")] [InlineData("buildId", ".bad")]
    [InlineData("scenarioSha256", "short")] [InlineData("environmentSha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_identity_never_publishes_self_unreadable_metadata(string field, string value)
    {
        var id = Identity(); id = field switch { "scenarioId" => id with { ScenarioId = value },
            "definitionVersion" => id with { DefinitionVersion = value }, "buildId" => id with { BuildId = value },
            "scenarioSha256" => id with { ScenarioSha256 = value }, _ => id with { EnvironmentSha256 = value } };
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [], id)).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
    }
    private (RunArtifactStore Store, byte[] Html) SavedReportAndRecording()
    {
        var store = Save(ResultStatus.Failed, settled: false); var html = Encoding.UTF8.GetBytes("<html><body>independent Trace report</body></html>");
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "trace.html"), html);
        var recording = Encoding.UTF8.GetBytes("independent recording"); File.WriteAllBytes(Path.Combine(store.DirectoryPath, "recording.gua"), recording);
        var primary = new PrimaryResult(ResultStatus.Failed, new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host));
        Assert.True(store.Complete(new(primary, [], [], []), [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.html", html.Length, Convert.ToHexStringLower(SHA256.HashData(html))),
            new(ArtifactKind.Recording, ArtifactState.Saved, "recording.gua", recording.Length, Convert.ToHexStringLower(SHA256.HashData(recording)))], [], DateTimeOffset.UtcNow).Saved);
        return (store, html);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_failure_preserves_independent_trace_report(bool corrupt)
    {
        var (store, html) = SavedReportAndRecording(); var recording = Path.Combine(store.DirectoryPath, "recording.gua");
        if (corrupt) File.WriteAllText(recording, "corrupt"); else File.Delete(recording);
        var index = Index(); var details = Assert.Single((await index.ReadAsync()).Runs).Details;
        var trace = details.Single(d => d.Kind == "Trace"); Assert.Equal("Saved", trace.State); Assert.NotNull(trace.Report);
        Assert.Equal(corrupt ? "Unreadable" : "Missing", details.Single(d => d.Kind == "Recording").State);
        Assert.Equal(html, index.ReadReport(trace.Report));
    }
    [Theory]
    [InlineData("run.json")] [InlineData("primary.json")] [InlineData("result.json")]
    public async Task Summary_hardlinks_cannot_be_saved_details_or_report_routes(string name)
    {
        var store = Save(); var source = Path.Combine(store.DirectoryPath, name); var bytes = File.ReadAllBytes(source);
        CreateHardLink(Path.Combine(store.DirectoryPath, "alias.html"), source);
        var completion = Path.Combine(store.DirectoryPath, "completion.json"); var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(completion))!;
        json["artifacts"]![0] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new ArtifactReceipt(ArtifactKind.Trace, ArtifactState.Saved,
            "alias.html", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        File.WriteAllText(completion, json.ToJsonString()); var index = Index();
        Assert.All(Assert.Single((await index.ReadAsync()).Runs).Details, d => { Assert.Equal("CompletionUnreadable", d.State); Assert.Null(d.Report); });
        Assert.Throws<FileNotFoundException>(() => index.ReadReport("/reports/0"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cross_receipt_alias_and_late_report_replacement_are_rejected(bool afterSnapshot)
    {
        var (store, html) = SavedReportAndRecording(); var index = Index(); var snapshot = await index.ReadAsync();
        var route = snapshot.Runs[0].Details.Single(d => d.Kind == "Trace").Report!;
        var recording = Path.Combine(store.DirectoryPath, "recording.gua"); File.Delete(recording);
        CreateHardLink(recording, Path.Combine(store.DirectoryPath, "trace.html"));
        var completion = Path.Combine(store.DirectoryPath, "completion.json"); var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(completion))!;
        json["artifacts"]![1]!["bytes"] = html.Length; json["artifacts"]![1]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(html));
        File.WriteAllText(completion, json.ToJsonString());
        if (afterSnapshot) Assert.ThrowsAny<IOException>(() => index.ReadReport(route));
        else Assert.All((await index.ReadAsync()).Runs[0].Details, d => Assert.Equal("CompletionUnreadable", d.State));
    }
    [Fact]
    public async Task Allowed_directory_uses_actual_filesystem_case_lookup()
    {
        Save(); var alternate = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root).ToUpperInvariant());
        if (Directory.Exists(alternate))
            Assert.Equal("Ready", (await new DashboardIndex(Path.Combine(alternate, "registry.json"), [Path.Combine(alternate, "runs")], [root]).ReadAsync()).RegistryState);
        else
        {
            Directory.CreateDirectory(alternate); Directory.CreateDirectory(Path.Combine(alternate, "runs"));
            foreach (var file in new[] { "registry.json", "s1.json", "s2.json" }) File.Copy(Path.Combine(root, file), Path.Combine(alternate, file));
            try { Assert.Equal("RegistryUnreadable", (await new DashboardIndex(Path.Combine(alternate, "registry.json"), [Path.Combine(alternate, "runs")], [root]).ReadAsync()).RegistryState); }
            finally { Directory.Delete(alternate, true); }
        }
    }
    private static void CreateHardLink(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/ln")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in OperatingSystem.IsWindows() ? new[] { "/c", "mklink", "/H", link, target } : new[] { target, link }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
    }
    [Fact]
    public async Task Case_distinct_sibling_never_inherits_allowed_root_authority()
    {
        var parent = Path.Combine(root, "case-parent"); Directory.CreateDirectory(parent);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("fsutil.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "file", "setCaseSensitiveInfo", parent, "enable" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        var safe = Path.Combine(parent, "safe"); var sibling = Path.Combine(parent, "SAFE");
        Directory.CreateDirectory(safe);
        var directories = Directory.Exists(sibling) ? new[] { safe } : new[] { safe, sibling };
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory, "runs"));
            foreach (var file in new[] { "registry.json", "s1.json", "s2.json" }) File.Copy(Path.Combine(root, file), Path.Combine(directory, file));
        }
        if (directories.Length == 1)
        {
            Assert.Equal(FileIdentity.ReadDirectory(safe), FileIdentity.ReadDirectory(sibling));
            Assert.Equal("Ready", (await new DashboardIndex(Path.Combine(sibling, "registry.json"), [Path.Combine(sibling, "runs")], [safe]).ReadAsync()).RegistryState);
            return; // Case-insensitive volumes resolve both spellings to the same authorized directory.
        }
        Assert.NotEqual(FileIdentity.ReadDirectory(safe), FileIdentity.ReadDirectory(sibling));
        Assert.Equal("RegistryUnreadable", (await new DashboardIndex(Path.Combine(sibling, "registry.json"), [Path.Combine(sibling, "runs")], [safe]).ReadAsync()).RegistryState);
        Assert.Equal("Ready", (await new DashboardIndex(Path.Combine(sibling, "registry.json"), [Path.Combine(sibling, "runs")], [parent]).ReadAsync()).RegistryState);
    }
    [Fact]
    public async Task Missing_run_document_is_unreadable_and_independent_result_is_retained()
    {
        var store = Save(); File.Delete(Path.Combine(store.DirectoryPath, "run.json"));
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal("RunUnreadable", run.State); Assert.Null(run.BuildId);
        Assert.Equal(ResultStatus.Passed, run.Result!.Status);
    }
    [Fact]
    public async Task Registry_budget_reserves_static_validation_second_read()
    {
        var files = new[] { Registry, Path.Combine(root, "s1.json"), Path.Combine(root, "s2.json") };
        var total = files.Sum(path => new FileInfo(path).Length);
        var largest = checked((int)Math.Max(256, files.Max(path => new FileInfo(path).Length)));
        Assert.Equal("RegistryUnreadable", (await Index(new(MaxFileBytes: largest, MaxIndexBytes: total)).ReadAsync()).RegistryState);
        Assert.Equal("Ready", (await Index(new(MaxFileBytes: largest, MaxIndexBytes: 2 * total)).ReadAsync()).RegistryState);
    }
    [Fact]
    public async Task Public_run_budget_is_reserved_before_even_invalid_static_validation()
    {
        var store = Save();
        var environment = new EnvironmentDocument("env", "buildA", new("ws://127.0.0.1:9876", null), "Player",
            new(1000, 2, 2, 1000, 1000, 1000, 1000, 1000, 10, 1000, 1000, 10, 64, 2, 2, 2, 1000, 10, 10, 1000),
            new([], false, []), new("recent", "onFailure"), null);
        var environmentFile = Path.Combine(root, "environment.json");
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(environment)); File.WriteAllBytes(environmentFile, bytes);
        var run = new RunDocument(store.RunId, Identity(), "InvalidProfile", ExecutionState.Finished, DateTimeOffset.UtcNow,
            new("../../s1.json", Identity().ScenarioSha256), new("../../environment.json", Convert.ToHexStringLower(SHA256.HashData(bytes))), null);
        var file = Path.Combine(store.DirectoryPath, "run.json"); File.WriteAllText(file, ContractJson.Serialize(run));
        File.Delete(Path.Combine(store.DirectoryPath, "result.json")); File.Delete(Path.Combine(store.DirectoryPath, "completion.json"));
        Assert.False((await new StaticContractValidator([root]).ValidateFileAsync(file)).IsValid);
        var registryFiles = new[] { Registry, Path.Combine(root, "s1.json"), Path.Combine(root, "s2.json") };
        var runFiles = new[] { file, Path.Combine(root, "s1.json"), environmentFile };
        var firstPassBudget = 2 * registryFiles.Sum(p => new FileInfo(p).Length) + runFiles.Sum(p => new FileInfo(p).Length);
        var largest = checked((int)Math.Max(256, registryFiles.Concat(runFiles).Max(p => new FileInfo(p).Length)));
        var snapshot = await Index(new(MaxFileBytes: largest, MaxIndexBytes: firstPassBudget)).ReadAsync();
        Assert.Equal("Ready", snapshot.RegistryState); Assert.Equal("RunUnreadable", Assert.Single(snapshot.Runs).State);
        // Invalid static validation cannot skip budget accounting: admission must reject before calling it.
        Assert.True(snapshot.Truncated);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Fixed_input_hash_corruption_never_reassigns_saved_history(bool identity)
    {
        var store = Save(identity: identity); var file = Path.Combine(store.DirectoryPath, "run.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!;
        json["inputs"]![0]!["document"]!["scenarioId"] = "s2"; File.WriteAllText(file, json.ToJsonString());
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal("RunUnreadable", run.State); Assert.Null(run.ScenarioId); Assert.Null(run.BuildId);
        Assert.Equal(ResultStatus.Passed, run.Result!.Status);
    }
    [Theory]
    [InlineData("name")] [InlineData("sha256")] [InlineData("document")]
    public async Task Fixed_input_wrapper_missing_fields_are_unreadable(string field)
    {
        var store = Save(identity: false); var file = Path.Combine(store.DirectoryPath, "run.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!;
        json["inputs"]![0]!.AsObject().Remove(field); File.WriteAllText(file, json.ToJsonString());
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal("RunUnreadable", run.State); Assert.Null(run.ScenarioId); Assert.Equal(ResultStatus.Passed, run.Result!.Status);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Plan_preparation_preserves_dashboard_metadata_without_granting_ordinary_trial_authority(bool replayPreparation)
    {
        var recording = Path.Combine(root, "recording.json");
        File.WriteAllText(recording, """{"schemaVersion":1,"steps":[{"action":"click","target":{"id":"buy"},"relativeMilliseconds":0,"preRevision":0,"postRevision":1,"sensitive":false}]}""");
        var plan = Path.Combine(root, "plan.json");
        File.WriteAllText(plan, ContractJson.Serialize(new ReplayPlanDocument("candidate", new("s1.json", Identity().ScenarioSha256),
            new("recording.json", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(recording)))), "conditionSynchronized", "afterPlan", [], null)));
        var replay = await ResolvedReplay.LoadAsync(plan, [root], new(10, 1000));
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "s1.json")));
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var metadata = new RunArtifactMetadata("player", [], new Dictionary<string, string>(), [new("scenario", scenario.RootElement)], Identity(), started);
        Assert.True((replayPreparation ? store.BeginReplayPreparation(metadata, Identity("buildB")) : store.BeginPreparation(metadata)).Saved);
        if (replayPreparation) store.BindReplay(replay);
        else Assert.Throws<InvalidDataException>(() => store.BindReplay(replay));
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal("Unsettled", run.State); Assert.Equal(replayPreparation ? "buildB" : "buildA", run.BuildId);
        Assert.Equal(started, run.StartedAt); Assert.Equal(Identity().ScenarioSha256, run.ScenarioSha256);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Preparation_never_publishes_identity_that_disagrees_with_fixed_scenario(bool replayPreparation, bool versionMismatch)
    {
        var store = RunArtifactStore.Create(Artifacts, Limits, new([], []));
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "s1.json")));
        var identity = versionMismatch ? Identity() with { DefinitionVersion = "v2" } : Identity() with { ScenarioId = "s2" };
        var metadata = new RunArtifactMetadata("player", [], new Dictionary<string, string>(), [new("scenario", scenario.RootElement)], identity);
        var result = replayPreparation ? store.BeginReplayPreparation(metadata, identity) : store.BeginPreparation(metadata);
        Assert.Equal(PersistenceFailure.InvalidEvidence, result.Failure); Assert.False(result.Saved);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
    }
    [Fact]
    public async Task Cancellation_returned_by_public_run_validator_never_becomes_a_normal_snapshot()
    {
        var store = Save(); var runFile = Path.Combine(store.DirectoryPath, "run.json");
        File.WriteAllText(runFile, ContractJson.Serialize(new RunDocument(store.RunId, Identity(), "Explore", ExecutionState.Finished,
            DateTimeOffset.UtcNow, new("../../s1.json", Identity().ScenarioSha256), new("../../s2.json", new string('b', 64)), null)));
        File.Delete(Path.Combine(store.DirectoryPath, "result.json")); File.Delete(Path.Combine(store.DirectoryPath, "completion.json"));
        using var canceled = new CancellationTokenSource(); var interrupted = false;
        var index = new DashboardIndex(Registry, [Artifacts], [root])
        {
            ValidateContract = async (path, roots, token) =>
            {
                if (path == runFile) canceled.Cancel();
                var report = await new StaticContractValidator(roots).ValidateFileAsync(path, token);
                if (path == runFile) interrupted = report.Code == "Interrupted";
                return report;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.ReadAsync(canceled.Token).AsTask());
        Assert.True(interrupted); // Real static validator returned its Interrupted report on the final run.
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Redacted_legacy_scenario_grouping_stays_unknown_without_corrupting_saved_result(bool sensitiveName)
    {
        var store = RunArtifactStore.Create(Artifacts, Limits, sensitiveName ? new([], ["scenarioId"]) : new(["s1"], []));
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "s1.json")));
        Assert.True(store.BeginPreparation(new("player", [], new Dictionary<string, string>(), [new("scenario", scenario.RootElement)])).Saved);
        var primary = new PrimaryResult(ResultStatus.Passed, new(RunReason.GoalSatisfied, RunPhase.Execution, RunOrigin.Host));
        Assert.True(store.ConfirmPrimary(primary, [], [], []).Saved);
        Assert.True(store.Complete(new(primary, [], [], []), [new(ArtifactKind.Trace, ArtifactState.NotExecuted), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.Equal("Passed", run.State); Assert.Null(run.ScenarioId); Assert.Null(run.BuildId); Assert.Null(run.ScenarioSha256);
        Assert.Equal(ResultStatus.Passed, run.Result!.Status);
    }
    [Fact]
    public async Task Entry_limit_stops_all_later_roots_without_advancing_or_admitting_them()
    {
        File.WriteAllText(Path.Combine(Artifacts, "noise"), "x");
        File.WriteAllText(Path.Combine(Artifacts, "second-noise"), "x");
        var index = new DashboardIndex(Registry, [Artifacts, Path.Combine(root, "unreadable-unused-root")], [root], new(MaxEntries: 1));
        var snapshot = await index.ReadAsync();
        Assert.Empty(snapshot.Warnings); Assert.True(snapshot.Truncated); Assert.Empty(snapshot.Runs);
        Assert.All(DashboardIndex.Select(snapshot, "buildA", EnvironmentHash), item => Assert.Equal("IndexIncomplete", item.State));
    }
    [Fact]
    public async Task Issued_report_route_never_rebinds_to_another_run_after_refresh()
    {
        var (first, firstHtml) = SavedReportAndRecording(); var index = Index();
        var firstRun = Assert.Single((await index.ReadAsync()).Runs); var oldRoute = firstRun.Details.Single(d => d.Kind == "Trace").Report!;
        Assert.Equal(firstHtml, index.ReadReport(oldRoute));
        Directory.Delete(first.DirectoryPath, true); var (second, secondHtml) = SavedReportAndRecording();
        var secondRun = Assert.Single((await index.ReadAsync()).Runs); var newRoute = secondRun.Details.Single(d => d.Kind == "Trace").Report!;
        Assert.NotEqual(first.RunId, second.RunId); Assert.NotEqual(firstRun.Key, secondRun.Key); Assert.NotEqual(oldRoute, newRoute);
        Assert.Throws<FileNotFoundException>(() => index.ReadReport(oldRoute)); Assert.Equal(secondHtml, index.ReadReport(newRoute));
        Assert.Equal(newRoute, Assert.Single((await index.ReadAsync()).Runs).Details.Single(d => d.Kind == "Trace").Report);
    }
    [Fact]
    public async Task Summary_to_summary_hardlink_is_rejected_independently_of_receipt_aliases()
    {
        var store = Save(); var primary = Path.Combine(store.DirectoryPath, "primary.json"); File.Delete(primary);
        CreateHardLink(primary, Path.Combine(store.DirectoryPath, "result.json"));
        var run = Assert.Single((await Index().ReadAsync()).Runs);
        Assert.All(run.Details, d => Assert.Equal("CompletionUnreadable", d.State));
        Assert.Equal(ResultStatus.Passed, run.Result!.Status); // Preserve the independent saved result, do not regrade it.
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
