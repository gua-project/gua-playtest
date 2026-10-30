using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Artifacts;
using Gua.Core;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class SingleUiRuntimeTests
{
    private static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    [Theory]
    [InlineData("ui-click-host", 0, 1)]
    [InlineData("ui-click-host-fail", 1, 1)]
    [InlineData("ui-click-host-no-goal", 3, 1)]
    [InlineData("ui-click-host-already", 0, 0)]
    [InlineData("zero", 1, 0)]
    [InlineData("forbidden", 2, 0)]
    [InlineData("initial-ok", 0, 1)]
    [InlineData("initial-false", 1, 0)]
    [InlineData("scenario-forbidden", 2, 0)]
    public async Task Fixed_single_click_has_real_goal_budget_artifacts_and_owned_cleanup(string mode, int expectedExit, int expectedClicks)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var unsupported = new StringWriter(); Assert.Equal(2, await CliApplication.ExecuteAsync(["click-run", "--json"], unsupported));
            Assert.Contains("WindowsRuntimeRequired", unsupported.ToString()); return;
        }
        string root = Path.Combine(Repository, "artifacts/tests/single-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/scenario.json")))!;
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/environment.json")))!;
        var read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}}""")!;
        scenario["goal"]!["success"]!["read"] = read.DeepClone(); scenario["goal"]!["success"]!["expected"]!["value"] = true;
        scenario["constraints"]!["maxDurationMilliseconds"] = 2000;
        if (mode.StartsWith("initial-",StringComparison.Ordinal))
        {
            var initial=scenario["goal"]!["success"]!.DeepClone(); initial["expected"]!["value"]=mode=="initial-false";
            scenario["setup"]=new JsonObject { ["kind"]="all", ["conditions"]=new JsonArray(initial,initial.DeepClone()) };
        }
        if (mode=="scenario-forbidden") scenario["constraints"]!["allowedActionIds"]=new JsonArray();
        if (mode == "zero") scenario["constraints"]!["maxActions"] = 0;
        environment["permissions"]!["reads"] = new JsonArray(read.DeepClone());
        environment["permissions"]!["actionIds"] = mode == "forbidden" ? new JsonArray("reset") : new JsonArray("click");
        environment["profile"] = "Debug"; environment["trace"]!["savePolicy"] = "always";
        environment["limits"]!["prepareTimeoutMilliseconds"] = 5000; environment["limits"]!["waitTimeoutMilliseconds"] = 1000;
        environment["limits"]!["cleanupTimeoutMilliseconds"] = 3000;
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        environment["connection"] = new JsonObject { ["endpoint"] = $"ws://127.0.0.1:{number}" };
        string executable = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable))); environment["buildId"] = "sha256-" + hash;
        await File.WriteAllTextAsync(Path.Combine(root, "scenario.json"), scenario.ToJsonString()); await File.WriteAllTextAsync(Path.Combine(root, "environment.json"), environment.ToJsonString());
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string probe = Path.Combine(Repository, "tests/Gua.Playtest.ProcessProbe/bin", configuration, "net10.0/Gua.Playtest.ProcessProbe.dll");
        string pidPath = Path.Combine(root, "pid");
        string[] args = ["click-run", "--json", "--allow-root", Repository, "--allow-root", Path.GetDirectoryName(executable)!,
            "--scenario", Path.Combine(root,"scenario.json"), "--environment", Path.Combine(root,"environment.json"), "--executable", executable,
            "--executable-sha256", hash, "--working-directory", Repository, "--output-root", root, "--target-id", "start",
            "--argument", probe, "--argument", mode.StartsWith("ui-click-",StringComparison.Ordinal) ? mode : "ui-click-host", "--argument", pidPath];
        using var output = new StringWriter(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int exit = await CliApplication.ExecuteAsync(args, output, deadline.Token); await File.WriteAllTextAsync(Path.Combine(root,"command.json"), output.ToString());
        Assert.True(exit == expectedExit, output.ToString());
        if (mode is "forbidden" or "scenario-forbidden") { Assert.False(File.Exists(pidPath)); return; }
        using var result = JsonDocument.Parse(output.ToString()); var details = result.RootElement.GetProperty("details");
        Assert.Equal("single-ui-click", details.GetProperty("driver").GetString()); Assert.True(details.GetProperty("postProcessingComplete").GetBoolean(), output.ToString());
        Assert.Equal(expectedClicks, int.Parse(await File.ReadAllTextAsync(pidPath + ".clicks")));
        Assert.Equal(expectedClicks, details.GetProperty("actions").GetInt32());
        if (expectedClicks == 1)
        {
            Assert.Equal(2, details.GetProperty("click").GetProperty("state").GetInt32());
            Assert.Equal((int)(mode=="ui-click-host-fail" ? GuaActionError.InvalidArgument : GuaActionError.None),details.GetProperty("click").GetProperty("guaErrorCode").GetInt32());
        }
        else Assert.Equal(JsonValueKind.Null, details.GetProperty("click").ValueKind);
        int pid = details.GetProperty("ownedProcessId").GetInt32();
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); } catch (ArgumentException) { }
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(details.GetProperty("artifactDirectory").GetString()!,new(8*1024*1024,8*1024*1024,10000,65536))).State);
        var events=Directory.GetFiles(details.GetProperty("artifactDirectory").GetString()!,"events.jsonl",SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line=>JsonNode.Parse(line)!).ToArray();
        var clickSteps=events.Where(item=>item["type"]!.GetValue<string>()=="runner.ui-click").Select(item=>item["stepId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if(expectedExit==0) Assert.DoesNotContain(events,item=>item["type"]!.GetValue<string>()=="step.end" && clickSteps.Contains(item["stepId"]!.GetValue<string>()) && item["data"]?["outcome"]?.GetValue<string>()=="failed");
        if(mode=="ui-click-host-fail") Assert.Contains(events,item=>item["type"]!.GetValue<string>()=="runner.ui-click" &&
            item["data"]?["GuaErrorCode"]?.GetValue<int>()==(int)GuaActionError.InvalidArgument);
    }
}
