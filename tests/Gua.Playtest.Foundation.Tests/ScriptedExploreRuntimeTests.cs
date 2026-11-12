using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
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
        if (mode == "ui-click-host-no-goal")
        {
            // Isolate final-decision exhaustion after the first approved click.
            // Four RPC-heavy turns could instead correctly reach the unchanged
            // global deadline before their final observation window closes.
            environment["limits"]!["maxDecisions"] = 1;
            environment["limits"]!["recoveryDecisionLimit"] = 1;
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
        using var output = new StringWriter(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
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
        }
        if (mode != "cancel-consumed")
            Assert.Equal(mode is not ("ui-click-host-fail" or "ui-click-host-private-event" or "ui-click-host-no-result"), details.GetProperty("postProcessingComplete").GetBoolean());
        if (mode == "ui-click-host")
        {
            Assert.Equal("GoalSatisfied", result.RootElement.GetProperty("code").GetString());
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
