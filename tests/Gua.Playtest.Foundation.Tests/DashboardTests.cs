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
    public async Task Packaged_assets_read_only_http_refresh_and_rejected_requests()
    {
        Save(); var before = Directory.GetFiles(Artifacts, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var cancellation = new CancellationTokenSource(); var ready = new TaskCompletionSource<Uri>();
        var server = DashboardApplication.RunAsync(Index(), port, uri => ready.SetResult(uri), cancellation.Token);
        var origin = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var client = new HttpClient { BaseAddress = origin };
        Assert.Contains("Registered Scenarios", await client.GetStringAsync("/"));
        Assert.Contains("textContent", await client.GetStringAsync("/dashboard.js"));
        Assert.Contains("color-scheme", await client.GetStringAsync("/dashboard.css"));
        for (var i = 0; i < 3; i++) Assert.Contains("Passed", await client.GetStringAsync("/api/refresh"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/result.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/refresh", null)).StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/refresh"); hostile.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostile)).StatusCode);
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        Assert.Equal(before.Count, Directory.GetFiles(Artifacts, "*", SearchOption.AllDirectories).Length);
        cancellation.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
