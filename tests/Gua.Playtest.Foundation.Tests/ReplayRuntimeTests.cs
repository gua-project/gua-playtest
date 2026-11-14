using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Planning;
using Gua.Testing;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ReplayRuntimeTests
{
    private static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    [Theory]
    [InlineData("success", 0, 1)]
    [InlineData("player", 0, 1)]
    [InlineData("failure", 1, 1)]
    [InlineData("no-goal", 1, 1)]
    [InlineData("no-budget", 1, 0)]
    [InlineData("candidate-refused", 2, 0)]
    [InlineData("recorded", 2, 0)]
    [InlineData("multiple", 2, 0)]
    [InlineData("changed", 2, 0)]
    [InlineData("cancel-before", 4, 0)]
    [InlineData("cancel-after-send", 1, 1)]
    [InlineData("no-result", 1, 1)]
    [InlineData("already", 0, 1)]
    [InlineData("on-goal", 2, 0)]
    [InlineData("save-failure", 11, 1)]
    [InlineData("offset-window", 2, 0)]
    public async Task Packaged_composition_requires_candidate_consent_and_genuine_full_trial(string mode, int expectedExit, int expectedClicks)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var unsupported = new StringWriter(); Assert.Equal(2, await CliApplication.ExecuteAsync(["replay-run", "--json"], unsupported));
            Assert.Contains("WindowsRuntimeRequired", unsupported.ToString()); return;
        }
        var root = Path.Combine(Repository, "artifacts/tests/replay-runtime", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/scenario.json")))!;
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/environment.json")))!;
        var read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}}""")!;
        scenario["goal"]!["success"]!["read"] = read.DeepClone(); scenario["goal"]!["success"]!["expected"]!["value"] = true;
        scenario["constraints"]!["maxDurationMilliseconds"] = 2000;
        scenario["constraints"]!["maxActions"] = mode == "no-budget" ? 0 : mode is "no-goal" or "cancel-after-send" ? 2 : 1;
        environment["permissions"]!["reads"] = new JsonArray(read.DeepClone());
        environment["permissions"]!["actionIds"] = new JsonArray("click");
        environment["profile"] = mode == "player" ? "Player" : "Debug"; environment["trace"]!["savePolicy"] = "always";
        environment["limits"]!["prepareTimeoutMilliseconds"] = 5000; environment["limits"]!["waitTimeoutMilliseconds"] = 1000;
        environment["limits"]!["cleanupTimeoutMilliseconds"] = 3000;
        // These rows verify real composition, not a one-second host SLA.
        // Exact deadline/fresh-capture boundaries are checked with virtual clocks.
        var nativeProgress = mode is "success" or "player" or "already" or "failure" or "save-failure" or "no-goal" or "cancel-after-send";
        if (nativeProgress)
        {
            scenario["constraints"]!["maxDurationMilliseconds"] = 15000;
            environment["limits"]!["maxDurationMilliseconds"] = 15000;
            environment["limits"]!["waitTimeoutMilliseconds"] = 5000;
            environment["limits"]!["maxSegmentMilliseconds"] = 5000;
        }
        if (mode == "offset-window") environment["limits"]!["waitTimeoutMilliseconds"] = 2000;
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        environment["connection"] = new JsonObject { ["endpoint"] = $"ws://127.0.0.1:{number}" };
        var executable = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable))); environment["buildId"] = "sha256-" + hash;
        await File.WriteAllTextAsync(Path.Combine(root, "scenario.json"), scenario.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(root, "environment.json"), environment.ToJsonString());
        var recording = JsonNode.Parse("""{"schemaVersion":1,"steps":[{"action":"click","target":{"id":"start"},"relativeMilliseconds":0,"preRevision":1,"postRevision":1,"sensitive":false}]}""")!;
        if (mode == "multiple") recording["steps"]!.AsArray().Add(recording["steps"]![0]!.DeepClone());
        if (mode == "offset-window") recording["steps"]![0]!["relativeMilliseconds"] = 1500;
        await File.WriteAllTextAsync(Path.Combine(root, "recording.json"), recording.ToJsonString());
        var managed = Path.Combine(root, "plans");
        var manager = new PlanManager(managed, new PersistenceRedactor([], ["password", "token", "authorization", "secretValue"]));
        var plan = await manager.CreateAsync(Path.Combine(root, "scenario.json"), Path.Combine(root, "recording.json"), [root], mode == "recorded" ? "recorded" : "conditionSynchronized");
        if (mode == "on-goal")
        {
            var model = JsonNode.Parse(await File.ReadAllTextAsync(plan))!; model["completion"] = "onGoal";
            await File.WriteAllTextAsync(plan, model.ToJsonString());
        }
        if (mode == "changed") await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(plan)!, "recording.json"), " ");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(Repository, "tests/Gua.Playtest.ProcessProbe/bin", configuration, "net10.0/Gua.Playtest.ProcessProbe.dll");
        var pid = Path.Combine(root, "pid"); using var timing = new ReplayFailureDiagnostics(pid);
        var args = new List<string> { "replay-run", "--json", "--allow-root", Repository, "--allow-root", Path.GetDirectoryName(executable)!,
            "--plan", plan, "--managed-root", managed, "--environment", Path.Combine(root, "environment.json"),
            "--executable", executable, "--executable-sha256", hash, "--working-directory", Repository, "--output-root", root,
            "--argument", probe, "--argument", mode == "failure" ? "ui-click-host-fail" : mode is "cancel-after-send" or "no-result" ? "ui-click-host-no-result" : mode == "no-goal" ? "ui-click-host-no-goal" : mode == "already" ? "ui-click-host-already" : "ui-click-host", "--argument", pid };
        if (mode != "candidate-refused") args.Add("--allow-candidate");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(nativeProgress ? 30 : 15));
        if (mode == "cancel-before") cancel.Cancel();
        using var output = new StringWriter();
        var execution = CliApplication.ExecuteAsync(args.ToArray(), output, cancel.Token);
        if (mode == "save-failure")
        {
            while (!File.Exists(pid))
            {
                Assert.False(execution.IsCompleted, "Run ended before the controlled write failure");
                await Task.Delay(10, cancel.Token);
            }
            var runRoot = Directory.GetDirectories(root).Single(path => File.Exists(Path.Combine(path, ".owner")));
            await File.WriteAllTextAsync(Path.Combine(runRoot, "trace"), "controlled destination collision");
        }
        if (mode == "cancel-after-send")
        {
            while (!File.Exists(pid + ".clicks") || (await File.ReadAllTextAsync(pid + ".clicks")).Trim() != "1")
            {
                Assert.False(execution.IsCompleted, "Run ended before the controlled post-send cancellation");
                await Task.Delay(10, cancel.Token);
            }
            cancel.Cancel();
        }
        var exit = await execution;
        await File.WriteAllTextAsync(Path.Combine(root, "command.json"), output.ToString());
        if (mode == "no-result")
        {
            // Existing lost-completion control exercises failure-only export without
            // changing any runtime deadline or injecting a new product delay.
            Assert.True(timing.SaveFailure(root, true));
            var journal = JsonSerializer.SerializeToElement(timing.Snapshot()).GetProperty("journal");
            var rows = journal.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Contains(rows, row => row.GetProperty("phase").GetInt32() == 50);
            Assert.DoesNotContain(rows, row => row.GetProperty("phase").GetInt32() == 53);
            Assert.Contains(rows, row => row.GetProperty("phase").GetInt32() == 32);
            Assert.Contains(rows, row => row.GetProperty("phase").GetInt32() == 10);
            Assert.Contains(rows, row => row.GetProperty("phase").GetInt32() == 11);
        }
        using var command = JsonDocument.Parse(output.ToString());
        var summary = command.RootElement.GetProperty("code").GetString() + " (" + Path.Combine(root, "command.json") + ")";
        if (mode == "no-goal")
        {
            // This case tests independent
            // Goal/trial eligibility, not which of two legitimate expiry facts wins.
            Assert.True(exit is 1 or 3, summary);
            Assert.Contains(command.RootElement.GetProperty("code").GetString(), new[] { "WaitExpired", "MaxDuration" });
        }
        else { timing.SaveFailure(root, exit != expectedExit); Assert.True(exit == expectedExit, summary); }
        if (expectedExit == 2 || mode == "cancel-before") { Assert.False(File.Exists(pid)); return; }
        var details = command.RootElement.GetProperty("details");
        Assert.Equal(expectedClicks, int.Parse(await File.ReadAllTextAsync(pid + ".clicks")));
        if (mode is not ("cancel-after-send" or "save-failure")) Assert.True(details.GetProperty("postProcessingComplete").GetBoolean(), summary);
        var processId = details.GetProperty("ownedProcessId").GetInt32();
        bool gone; try { using var process = Process.GetProcessById(processId); gone = process.HasExited; } catch (ArgumentException) { gone = true; }
        Assert.True(gone);
        var runDirectory = details.GetProperty("artifactDirectory").GetString()!;
        var verified = await RunArtifactReader.ReadResultAsync(runDirectory, new(8 * 1024 * 1024, 8 * 1024 * 1024, 10000, 65536));
        Assert.Equal(ResultReadState.Verified, verified.State);
        if (mode == "save-failure")
        {
            Assert.Equal("Passed", command.RootElement.GetProperty("status").GetString());
            Assert.Equal("GoalSatisfied", command.RootElement.GetProperty("code").GetString());
            Assert.False(details.GetProperty("postProcessingComplete").GetBoolean());
            Assert.Equal("NotEligible", details.GetProperty("trialState").GetString());
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(plan)!, "trial-*"));
            using var failed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "completion.json")));
            Assert.Contains(failed.RootElement.GetProperty("artifacts").EnumerateArray(), artifact =>
                artifact.GetProperty("kind").GetString() == "Trace" && artifact.GetProperty("state").GetString() == "SaveFailed");
            return;
        }
        var traceDirectory = Directory.GetParent(Directory.GetFiles(runDirectory, "manifest.json", SearchOption.AllDirectories).Single())!.FullName;
        Assert.Empty(GuaTraceReader.Read(traceDirectory).Issues);
        if (mode == "failure")
        {
            Assert.Equal(1, details.GetProperty("progress").GetProperty("completedSteps").GetInt32());
            using var confirmedFailure = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "primary.json")));
            Assert.DoesNotContain(confirmedFailure.RootElement.GetProperty("events").EnumerateArray(),
                e => e.GetProperty("reason").GetString() == "ActionUnconfirmed");
        }
        if (mode is "cancel-after-send" or "no-result")
        {
            Assert.Equal("ActionUnconfirmed", command.RootElement.GetProperty("code").GetString());
            Assert.Equal(1, details.GetProperty("progress").GetProperty("dispatchedSteps").GetInt32());
            Assert.Equal(0, details.GetProperty("progress").GetProperty("completedSteps").GetInt32());
            using var completion = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "completion.json")));
            Assert.DoesNotContain(completion.RootElement.GetProperty("postProcessing").EnumerateArray(),
                issue => issue.GetProperty("reason").GetString() is "InputReleaseUnconfirmed" or "ResourceReleaseUnconfirmed" or "CleanupTimeout");
            if (mode == "cancel-after-send")
            {
                using var primary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "primary.json")));
                Assert.Contains(primary.RootElement.GetProperty("events").EnumerateArray(), e => e.GetProperty("reason").GetString() == "Cancelled");
            }
        }
        if (mode is not ("success" or "player" or "already"))
        {
            if (mode == "no-goal")
            {
                Assert.Equal(1, details.GetProperty("progress").GetProperty("completedSteps").GetInt32());
                Assert.True(details.GetProperty("progress").GetProperty("planCompleted").GetBoolean());
                Assert.False(details.GetProperty("progress").GetProperty("goalVerified").GetBoolean());
            }
            Assert.Equal("NotEligible", details.GetProperty("trialState").GetString());
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(plan)!, "trial-*")); return;
        }
        Assert.Equal("Saved", details.GetProperty("trialState").GetString());
        var progress = details.GetProperty("progress");
        Assert.Equal(1, progress.GetProperty("dispatchedSteps").GetInt32());
        Assert.Equal(1, progress.GetProperty("completedSteps").GetInt32());
        Assert.True(progress.GetProperty("planCompleted").GetBoolean());
        Assert.True(progress.GetProperty("goalVerified").GetBoolean());
        if (mode == "already") return;
        var trialId = details.GetProperty("trialId").GetString()!;
        Assert.False(await manager.IsAcceptedAsync(plan));
        await manager.AcceptAsync(plan, trialId, true, true);
        Assert.True(await manager.IsAcceptedAsync(plan));
        // A successful real trial can be adopted explicitly, then executed without
        // candidate override. It gets a fresh process, Run and guarded owner.
        args.Remove("--allow-candidate"); timing.NextHost();
        using var acceptedOutput = new StringWriter();
        using var acceptedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptedExit = await CliApplication.ExecuteAsync(args.ToArray(), acceptedOutput, acceptedDeadline.Token);
        await File.WriteAllTextAsync(Path.Combine(root, "accepted-command.json"), acceptedOutput.ToString());
        timing.SaveFailure(root, acceptedExit != 0); Assert.Equal(0, acceptedExit);
        using var accepted = JsonDocument.Parse(acceptedOutput.ToString());
        Assert.True(accepted.RootElement.GetProperty("details").GetProperty("acceptedAtStart").GetBoolean());
        Assert.NotEqual(details.GetProperty("runId").GetString(), accepted.RootElement.GetProperty("details").GetProperty("runId").GetString());
        Assert.False(File.Exists(Path.Combine(root, "replay-timing.json")));
        var correlated = JsonSerializer.SerializeToElement(timing.Snapshot()).GetProperty("journal").GetProperty("rows").EnumerateArray().ToArray();
        Assert.Contains(correlated, row => row.GetProperty("phase").GetInt32() == 10 && row.GetProperty("c").GetInt32() == 2);
        Assert.Contains(correlated, row => row.GetProperty("phase").GetInt32() == 50 && row.GetProperty("segment").GetInt32() == 2);
        Assert.Contains(correlated, row => row.GetProperty("phase").GetInt32() == 33 && row.GetProperty("c").GetInt32() == 2);
    }
}
