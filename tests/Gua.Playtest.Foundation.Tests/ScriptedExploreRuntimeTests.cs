using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Planning;
using Gua.Testing;
using Gua.Testing.Recording;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ScriptedExploreRuntimeTests
{
    private static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    [Theory]
    [InlineData("ui-click-host", 0, 1)]
    [InlineData("roundtrip", 0, 1)]
    [InlineData("ui-click-host-already", 0, 0)]
    [InlineData("ui-click-host-fail", 1, 1)]
    [InlineData("ui-click-host-private-event", 11, 1)]
    [InlineData("ui-click-host-no-goal", 1, 1)]
    [InlineData("ui-click-host-no-result", 1, 1)]
    [InlineData("zero", 1, 0)]
    [InlineData("debug", 2, 0)]
    [InlineData("private-goal", 2, 0)]
    [InlineData("missing-public-permission", 2, 0)]
    [InlineData("metadata-gap", 1, 0)]
    [InlineData("cancel-consumed", 1, 1)]
    [InlineData("cancel-before-start", 4, 0)]
    [InlineData("click-cancel-before-start", 4, 0)]
    [InlineData("non-exact-goal", 2, 0)]
    public async Task Player_script_uses_existing_gate_monitor_recording_and_cleanup(string mode, int expectedExit, int expectedClicks)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var unsupported = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["scripted-explore-run", "--json"], unsupported));
            Assert.Contains("WindowsRuntimeRequired", unsupported.ToString()); return;
        }
        string root = Path.Combine(Repository, "artifacts/tests/scripted-explore", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/scenario.json")))!;
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/environment.json")))!;
        var read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}}""")!;
        scenario["goal"]!["success"]!["read"] = read.DeepClone(); scenario["goal"]!["success"]!["expected"]!["value"] = true;
        scenario["constraints"]!["maxDurationMilliseconds"] = 3000;
        if (mode == "zero") scenario["constraints"]!["maxActions"] = 0;
        environment["profile"] = mode == "debug" ? "Debug" : "Player";
        environment["permissions"]!["reads"] = new JsonArray(read.DeepClone());
        if (mode != "missing-public-permission")
            foreach (var approved in ScriptedUiExplore.PublicReads("start")) environment["permissions"]!["reads"]!.AsArray().Add(approved);
        environment["permissions"]!["actionIds"] = new JsonArray("click");
        environment["limits"]!["maxDurationMilliseconds"] = 3000;
        environment["limits"]!["prepareTimeoutMilliseconds"] = 5000;
        environment["limits"]!["cleanupTimeoutMilliseconds"] = 3000;
        environment["limits"]!["waitTimeoutMilliseconds"] = 1000;
        environment["limits"]!["maxDecisions"] = 4;
        var nativeProgress = mode is "ui-click-host-no-goal" or "roundtrip";
        if (nativeProgress)
        {
            // Native composition checks real clicks and independent Goal outcomes,
            // without a one-second dispatch SLA. Exact expiry/budget semantics are
            // covered with a controlled clock in ExploreTests.
            scenario["constraints"]!["maxDurationMilliseconds"] = 15000;
            environment["limits"]!["maxDurationMilliseconds"] = 15000;
            environment["limits"]!["waitTimeoutMilliseconds"] = 5000;
            environment["limits"]!["maxSegmentMilliseconds"] = 5000;
            if (mode == "ui-click-host-no-goal")
            {
                environment["limits"]!["maxDecisions"] = 1;
                environment["limits"]!["recoveryDecisionLimit"] = 1;
            }
        }
        environment["trace"]!["savePolicy"] = "always";
        if (mode == "private-goal")
        {
            read["region"] = "observe"; read.AsObject().Remove("field"); read["name"] = "privateFlag";
            scenario["goal"]!["success"]!["read"] = read.DeepClone(); environment["permissions"]!["reads"] = new JsonArray(read.DeepClone());
        }
        if (mode == "non-exact-goal")
        {
            read["target"]!["selector"] = new JsonObject { ["role"] = new JsonObject { ["value"] = "button", ["match"] = "regex" } };
            scenario["goal"]!["success"]!["read"] = read.DeepClone(); environment["permissions"]!["reads"]!.AsArray()[0] = read.DeepClone();
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        environment["connection"] = new JsonObject { ["endpoint"] = $"ws://127.0.0.1:{port}" };
        string executable = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable))); environment["buildId"] = "sha256-" + hash;
        await File.WriteAllTextAsync(Path.Combine(root, "scenario.json"), scenario.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(root, "environment.json"), environment.ToJsonString());
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string probe = Path.Combine(Repository, "tests/Gua.Playtest.ProcessProbe/bin", configuration, "net10.0/Gua.Playtest.ProcessProbe.dll");
        string pid = Path.Combine(root, "pid");
        string[] args = [mode == "click-cancel-before-start" ? "click-run" : "scripted-explore-run", "--json", "--allow-root", Repository, "--allow-root", Path.GetDirectoryName(executable)!,
            "--scenario", Path.Combine(root,"scenario.json"), "--environment", Path.Combine(root,"environment.json"), "--executable", executable,
            "--executable-sha256", hash, "--working-directory", Repository, "--output-root", root, "--target-id", "start",
            "--argument", probe, "--argument", mode == "metadata-gap" ? "ui-click-host" : "scripted-" +
                (mode == "cancel-consumed" ? "ui-click-host-no-result" : mode.StartsWith("ui-click-", StringComparison.Ordinal) ? mode : "ui-click-host"), "--argument", pid];
        using var output = new StringWriter(); // Outer harness includes preparation, Running, cleanup and persistence.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(nativeProgress ? 30 : 18));
        if (mode is "cancel-before-start" or "click-cancel-before-start") deadline.Cancel();
        var execution = CliApplication.ExecuteAsync(args, output, deadline.Token);
        if (mode == "cancel-consumed")
        {
            while (!File.Exists(pid + ".clicks") || (await File.ReadAllTextAsync(pid + ".clicks", deadline.Token)) != "1")
                await Task.Delay(5, deadline.Token);
            deadline.Cancel();
        }
        int exit = await execution;
        await File.WriteAllTextAsync(Path.Combine(root, "command.json"), output.ToString());
        if (mode is "cancel-before-start" or "click-cancel-before-start")
        { Assert.Equal(4, exit); Assert.Contains("Interrupted", output.ToString()); Assert.False(File.Exists(pid)); return; }
        string Summary() { using var parsed = JsonDocument.Parse(output.ToString()); var d = parsed.RootElement.GetProperty("details"); return JsonSerializer.Serialize(new
        { exit, code = parsed.RootElement.GetProperty("code").GetString(), directory = d.GetProperty("artifactDirectory").GetString(),
            actions = d.GetProperty("actions").GetInt32(), requests = d.GetProperty("plannerRequests").GetInt32(), phase = d.GetProperty("phase").GetString() }); }
        Assert.True(exit == expectedExit, exit == expectedExit ? "" : Summary());
        if (mode is "debug" or "private-goal" or "missing-public-permission" or "non-exact-goal")
        { Assert.Contains("ScriptedExplorePublicUiRequired", output.ToString()); Assert.False(File.Exists(pid)); return; }
        using var result = JsonDocument.Parse(output.ToString()); var details = result.RootElement.GetProperty("details");
        Assert.Equal("scripted-ui-explore", details.GetProperty("driver").GetString());
        Assert.Equal(expectedClicks, int.Parse(await File.ReadAllTextAsync(pid + ".clicks")));
        Assert.Equal(expectedClicks, details.GetProperty("actions").GetInt32());
        if (mode == "ui-click-host-no-goal")
        {
            Assert.Equal("DecisionsExhausted", result.RootElement.GetProperty("code").GetString());
            Assert.Equal(1, details.GetProperty("plannerRequests").GetInt32());
            Assert.Equal(1, details.GetProperty("decisions").GetInt32());
            Assert.Equal("Failed", result.RootElement.GetProperty("status").GetString());
            Assert.True(details.GetProperty("click").GetProperty("succeeded").GetBoolean());
            var verified = await RunArtifactReader.ReadResultAsync(details.GetProperty("artifactDirectory").GetString()!,
                new(8 * 1024 * 1024, 8 * 1024 * 1024, 10000, 65536));
            Assert.Equal(ResultReadState.Verified, verified.State);
        }
        if (mode != "cancel-consumed")
            Assert.Equal(mode is not ("ui-click-host-fail" or "ui-click-host-private-event" or "ui-click-host-no-result"), details.GetProperty("postProcessingComplete").GetBoolean());
        if (mode is "ui-click-host" or "roundtrip" or "ui-click-host-no-goal")
        {
            if (mode != "ui-click-host-no-goal") Assert.Equal("GoalSatisfied", result.RootElement.GetProperty("code").GetString());
            Assert.Equal(1, details.GetProperty("plannerRequests").GetInt32());
            Assert.Equal(1, details.GetProperty("decisions").GetInt32());
            var recording = GuaRecordingFile.Load(Assert.Single(Directory.GetFiles(details.GetProperty("artifactDirectory").GetString()!, "recording.json", SearchOption.AllDirectories)));
            var step = Assert.Single(recording.Steps); Assert.Equal(GuaRecordedAction.click, step.Action);
            Assert.Equal(details.GetProperty("click").GetProperty("requestId").GetUInt64(), step.RequestId);
            Assert.Equal("Player", details.GetProperty("click").GetProperty("profile").GetString());
            Assert.Equal(details.GetProperty("runId").GetString(), details.GetProperty("plannerDecision").GetProperty("runId").GetString());
        }
        if (mode is "ui-click-host-no-result" or "cancel-consumed") Assert.Equal("ActionUnconfirmed", result.RootElement.GetProperty("code").GetString());
        if (mode == "metadata-gap") { Assert.Equal(0, details.GetProperty("plannerRequests").GetInt32()); Assert.Equal("Preparation", details.GetProperty("phase").GetString()); }
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(int.Parse(File.ReadAllText(pid))));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, port));
        if (mode == "roundtrip")
        {
            var recording = Assert.Single(Directory.GetFiles(details.GetProperty("artifactDirectory").GetString()!, "recording.json", SearchOption.AllDirectories));
            await VerifyPlayerRoundTripAsync(root, executable, hash, probe, recording, details.GetProperty("runId").GetString()!);
        }
    }


    // Exercise the same public CLI envelope in Linux contract checks and the
    // Windows transport round trip. PlanCommand's inner JSON is not this envelope.
    private static async Task<(int Exit, JsonDocument Json)> RoundTripCommandAsync(string[] arguments,
        CancellationToken token = default)
    {
        using var output = new StringWriter();
        var command = arguments.Contains("--json", StringComparer.Ordinal) ? arguments : [.. arguments, "--json"];
        var exit = await CliApplication.ExecuteAsync(command, output, token);
        return (exit, JsonDocument.Parse(output.ToString()));
    }

    private static string RoundTripCandidatePath(JsonElement response)
        => response.GetProperty("details").GetProperty("plan").GetString()!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Roundtrip_command_uses_public_json_envelope_once(bool jsonAlreadyPresent)
    {
        var root = Path.Combine(Repository, "artifacts/tests/roundtrip-command-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputs = Path.Combine(Repository, "fixtures/contracts");
        var args = new List<string> { "plan", "create", "--managed-root", root, "--allow-root", inputs,
            "--scenario", Path.Combine(inputs, "scenario.json"), "--recording", Path.Combine(inputs, "recording.json"),
            "--timing", "conditionSynchronized" };
        if (jsonAlreadyPresent) args.Add("--json");
        var created = await RoundTripCommandAsync(args.ToArray());
        using var response = created.Json;
        Assert.Equal(0, created.Exit);
        Assert.Equal("PlanCandidateCreated", response.RootElement.GetProperty("code").GetString());
        Assert.False(response.RootElement.TryGetProperty("plan", out _));
        var path = RoundTripCandidatePath(response.RootElement);
        Assert.True(File.Exists(path));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(inputs, "scenario.json")),
            await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, "scenario.json")));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(inputs, "recording.json")),
            await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, "recording.json")));
    }

    private static async Task VerifyPlayerRoundTripAsync(string root, string executable, string executableHash,
        string probe, string recording, string exploreRunId)
    {
        var managed = Path.Combine(root, "plans");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<(int Exit, JsonDocument Json)> Command(params string[] arguments)
            => RoundTripCommandAsync(arguments, deadline.Token);
        var created = await Command("plan", "create", "--managed-root", managed, "--allow-root", root,
            "--scenario", Path.Combine(root, "scenario.json"), "--recording", recording, "--timing", "conditionSynchronized");
        using var candidate = created.Json; Assert.Equal(0, created.Exit);
        var plan = RoundTripCandidatePath(candidate.RootElement);
        var recordingHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(recording)));
        var manager = new Gua.Playtest.Runner.Planning.PlanManager(managed,
            new PersistenceRedactor([], ["password", "token", "authorization", "secretValue"]));
        Assert.False(await manager.IsAcceptedAsync(plan));
        string[] Replay(string phase, bool allowCandidate)
        {
            var output = Path.Combine(root, phase); Directory.CreateDirectory(output);
            var args = new List<string> { "replay-run", "--json", "--allow-root", Repository,
                "--allow-root", Path.GetDirectoryName(executable)!, "--managed-root", managed, "--plan", plan,
                "--environment", Path.Combine(root, "environment.json"), "--executable", executable,
                "--executable-sha256", executableHash, "--working-directory", Repository, "--output-root", output,
                "--argument", probe, "--argument", "scripted-ui-click-host", "--argument", Path.Combine(root, phase + ".pid") };
            if (allowCandidate) args.Add("--allow-candidate");
            return args.ToArray();
        }
        var refused = await Command(Replay("refused", false));
        using (refused.Json) { Assert.Equal(2, refused.Exit); Assert.Equal("ReplayCandidateNotAccepted", refused.Json.RootElement.GetProperty("code").GetString()); }
        Assert.False(File.Exists(Path.Combine(root, "refused.pid")));
        var trial = await Command(Replay("trial", true));
        using var trialJson = trial.Json; Assert.Equal(0, trial.Exit);
        var trialDetails = trialJson.RootElement.GetProperty("details");
        Assert.Equal("Saved", trialDetails.GetProperty("trialState").GetString());
        Assert.False(trialDetails.GetProperty("acceptedAtStart").GetBoolean());
        await VerifyReplayAsync(trialDetails, "trial");
        var trialId = trialDetails.GetProperty("trialId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(trialId)); Assert.False(await manager.IsAcceptedAsync(plan));
        var unapproved = await Command("plan", "accept", "--managed-root", managed, "--plan", plan, "--trial", trialId, "--approve");
        using (unapproved.Json) Assert.Equal(2, unapproved.Exit);
        Assert.False(await manager.IsAcceptedAsync(plan));
        var adoption = await Command("plan", "accept", "--managed-root", managed, "--plan", plan,
            "--trial", trialId, "--approve", "--approve-expectations");
        using (adoption.Json) Assert.Equal(0, adoption.Exit);
        Assert.True(await manager.IsAcceptedAsync(plan));
        var replay = await Command(Replay("adopted", false));
        using var replayJson = replay.Json; Assert.Equal(0, replay.Exit);
        var replayDetails = replayJson.RootElement.GetProperty("details");
        Assert.True(replayDetails.GetProperty("acceptedAtStart").GetBoolean());
        Assert.NotEqual(trialDetails.GetProperty("runId").GetString(), replayDetails.GetProperty("runId").GetString());
        await VerifyReplayAsync(replayDetails, "adopted");
        // Alter only the private copied input. Old trial/adoption cannot authorize
        // a changed Recording, while the original Explore Recording stays fixed.
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(plan)!, "recording.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.IsAcceptedAsync(plan).AsTask());
        var changed = await Command(Replay("changed", false));
        using (changed.Json) Assert.Equal(2, changed.Exit);
        Assert.False(File.Exists(Path.Combine(root, "changed.pid")));
        Assert.Equal(recordingHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(recording))));
        await File.WriteAllTextAsync(Path.Combine(root, "roundtrip.json"), JsonSerializer.Serialize(new
        { recordingSha256 = recordingHash, exploreRunId, trialRunId = trialDetails.GetProperty("runId").GetString(),
            replayRunId = replayDetails.GetProperty("runId").GetString(), candidateRefused = true,
            explicitAdoption = true, changedRecordingRefused = true, clicksPerReplay = 1, profile = "Player" }));

        async Task VerifyReplayAsync(JsonElement details, string phase)
        {
            Assert.True(details.GetProperty("postProcessingComplete").GetBoolean());
            Assert.Equal(recordingHash, details.GetProperty("recordingSha256").GetString());
            Assert.NotEqual(exploreRunId, details.GetProperty("runId").GetString());
            Assert.True(details.GetProperty("progress").GetProperty("planCompleted").GetBoolean());
            Assert.True(details.GetProperty("progress").GetProperty("goalVerified").GetBoolean());
            Assert.Equal(1, details.GetProperty("progress").GetProperty("completedSteps").GetInt32());
            Assert.Equal("Player", details.GetProperty("click").GetProperty("profile").GetString());
            var pid = Path.Combine(root, phase + ".pid"); Assert.Equal("1", await File.ReadAllTextAsync(pid + ".clicks"));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(int.Parse(File.ReadAllText(pid))));
            var verified = await RunArtifactReader.ReadResultAsync(details.GetProperty("artifactDirectory").GetString()!,
                new(8 * 1024 * 1024, 8 * 1024 * 1024, 10000, 65536));
            Assert.Equal(ResultReadState.Verified, verified.State);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("epoch")]
    [InlineData("profile")]
    [InlineData("revision")]
    [InlineData("current-epoch")]
    [InlineData("current-revision")]
    [InlineData("basis-profile")]
    public void Public_basis_and_actual_guard_must_agree_before_dispatch(string mismatch)
    {
        ProjectedPlannerState State() => new("public-1", JsonNode.Parse("""{"profile":"Player","sources":[{"sourceId":"host","sessionEpoch":1,"revision":8}]}""")!.AsObject(), new(), [], []);
        var basis = State(); var current = State();
        var guard = new GuardedUiReceipt(GuaRemoteDispatchState.Rejected, false, null, null, "preflight", "host", 1, "Player", 8);
        Assert.True(ScriptedUiExplore.ContextCurrent(current, basis, guard));
        guard = mismatch switch
        {
            "source" => guard with { SourceId = "other" }, "epoch" => guard with { SessionEpoch = 2 },
            "profile" => guard with { Profile = "Debug" }, "revision" => guard with { Revision = 9 }, _ => guard
        };
        if (mismatch == "current-epoch") current.Observation["sources"]![0]!["sessionEpoch"] = 2;
        if (mismatch == "current-revision") current.Observation["sources"]![0]!["revision"] = 9;
        if (mismatch == "basis-profile") basis.Observation["profile"] = "Debug";
        Assert.False(ScriptedUiExplore.ContextCurrent(current, basis, guard));
    }
    [Fact]
    public void Pending_work_cannot_advertise_cached_values_as_a_new_public_observation()
    {
        var original = new ProjectedPlannerState("actual-id", JsonNode.Parse("""{"observationId":"actual-id","observedAt":"2026-11-09T00:00:00Z","complete":true,"reads":[{"read":{},"status":"available","value":{"type":"bool","value":true}}]}""")!.AsObject(), new(), [], []);
        var stale = ScriptedUiExplore.StaleWhileSettling(original);
        Assert.Equal(original.ObservationId, stale.ObservationId);
        Assert.Equal(original.Observation["observedAt"]!.GetValue<string>(), stale.Observation["observedAt"]!.GetValue<string>());
        Assert.False(stale.Observation["complete"]!.GetValue<bool>());
        Assert.Equal("stale", stale.Observation["reads"]![0]!["status"]!.GetValue<string>());
        Assert.Null(stale.Observation["reads"]![0]!["value"]);
        Assert.True(original.Observation["complete"]!.GetValue<bool>());
        Assert.NotNull(original.Observation["reads"]![0]!["value"]);
    }
}
