using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Artifacts;
using Gua.Testing;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ObservationRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "playtest-runtime-" + Guid.NewGuid().ToString("N"));
    private static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    public ObservationRuntimeTests() => Directory.CreateDirectory(root);
    public void Dispose() { Directory.Delete(root, true); }
    private async Task<string[]> Inputs(string mutation, int port, bool wrongHash = false)
    {
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/scenario.json")))!;
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository, "fixtures/contracts/environment.json")))!;
        var executable = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable)));
        var read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"ready"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}}""")!;
        scenario["goal"]!["success"]!["read"] = read.DeepClone();
        if (mutation.StartsWith("ambiguous-", StringComparison.Ordinal))
        {
            read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"role":{"value":"button"}}},"region":"observe","name":"flag","valueType":{"type":"bool"}}""")!;
            scenario["goal"]!["success"]!["read"] = read.DeepClone();
            scenario["goal"]!["success"]!["quantifier"] = mutation[10..];
        }
        scenario["goal"]!["success"]!["expected"]!["value"] = mutation is "timeout" or "cancel" or "delayed" or "zero-false";
        if (mutation.StartsWith("zero-", StringComparison.Ordinal)) scenario["constraints"]!["maxActions"] = 0;
        scenario["constraints"]!["maxDurationMilliseconds"] = mutation == "timeout" ? 300 : 3000;
        environment["buildId"] = "sha256-" + (wrongHash ? new string('0', 64) : hash);
        environment["connection"] = new JsonObject { ["endpoint"] = "ws://127.0.0.1:" + port };
        environment["profile"] = "Debug";
        environment["trace"]!["savePolicy"] = "always";
        environment["limits"]!["prepareTimeoutMilliseconds"] = 5000;
        environment["limits"]!["cleanupTimeoutMilliseconds"] = 3000;
        environment["limits"]!["waitTimeoutMilliseconds"] = 500;
        environment["permissions"]!["actionIds"] = new JsonArray();
        environment["permissions"]!["reads"] = new JsonArray(read.DeepClone());
        if (mutation == "actions") environment["permissions"]!["actionIds"] = new JsonArray("buy");
        await File.WriteAllTextAsync(Path.Combine(root, "scenario.json"), scenario.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(root, "environment.json"), environment.ToJsonString());
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(Repository, "tests/Gua.Playtest.ProcessProbe/bin", configuration, "net10.0/Gua.Playtest.ProcessProbe.dll");
        return ["observe-run", "--json", "--allow-root", root, "--allow-root", Path.GetDirectoryName(executable)!, "--allow-root", Repository,
            "--scenario", Path.Combine(root, "scenario.json"), "--environment", Path.Combine(root, "environment.json"),
            "--executable", executable, "--executable-sha256", wrongHash ? new string('0', 64) : hash,
            "--working-directory", Repository, "--output-root", root, "--argument", probe,
            "--argument", mutation == "delayed" ? "observation-host-delayed" : mutation.StartsWith("ambiguous-", StringComparison.Ordinal) ? "observation-host-ambiguous" : "observation-host",
            "--argument", Path.Combine(root, "pid")];
    }
    private static int Port()
    { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static bool IsExited(int id)
    { try { using var process = Process.GetProcessById(id); return process.HasExited; } catch (ArgumentException) { return true; } }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("timeout", 3)]
    [InlineData("cancel", 4)]
    [InlineData("delayed", 0)]
    [InlineData("ambiguous-one", 2)]
    [InlineData("ambiguous-any", 0)]
    [InlineData("ambiguous-all", 3)]
    [InlineData("ambiguous-none", 3)]
    [InlineData("zero-true", 0)]
    [InlineData("zero-false", 1)]
    public async Task Actual_owned_native_bridge_uses_runner_result_trace_and_cleanup(string mode, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var unavailable = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["observe-run", "--json"], unavailable));
            Assert.Contains("WindowsRuntimeRequired", unavailable.ToString()); return;
        }
        var args = await Inputs(mode, Port());
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = CliApplication.ExecuteAsync(args, output, cancellation.Token);
        if (mode == "cancel")
        {
            var deadline = Stopwatch.StartNew();
            while (!Directory.GetFiles(root, "primary.json", SearchOption.AllDirectories).Any() && !task.IsCompleted && deadline.Elapsed < TimeSpan.FromMilliseconds(1000))
                await Task.Delay(10);
            cancellation.Cancel();
        }
        var exit = await task;
        Assert.True(expected == exit, $"Expected {expected}; got {exit}: {output}");
        using var result = JsonDocument.Parse(output.ToString());
        var details = result.RootElement.GetProperty("details");
        if (mode == "ambiguous-one") Assert.Equal("ObservationContractViolation", result.RootElement.GetProperty("code").GetString());
        if (mode == "zero-false") Assert.Equal("ActionsExhausted", result.RootElement.GetProperty("code").GetString());
        if (mode == "cancel")
        {
            // Existing Runner cancellation skips diagnostics, records that debt,
            // and still owes exact owned resource/process release.
            Assert.False(details.GetProperty("postProcessingComplete").GetBoolean());
            Assert.Contains("DiagnosticsFailed", details.GetProperty("postProcessing").EnumerateArray().Select(x => x.GetString()));
            Assert.DoesNotContain("ResourceReleaseUnconfirmed", details.GetProperty("postProcessing").EnumerateArray().Select(x => x.GetString()));
        }
        else Assert.True(details.GetProperty("postProcessingComplete").GetBoolean(), output.ToString());
        Assert.Equal("None", details.GetProperty("persistence").GetString());
        int pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "pid")));
        Assert.Equal(pid, details.GetProperty("ownedProcessId").GetInt32());
        Assert.True(IsExited(pid), "The exact owned process must be gone before returning the Run result.");
        var directory = details.GetProperty("artifactDirectory").GetString()!;
        var limits = new ArtifactLimits(8 * 1024 * 1024, 8 * 1024 * 1024, 10000, 65536);
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(directory, limits)).State);
        var trace = Directory.GetFiles(directory, "manifest.json", SearchOption.AllDirectories).Single();
        Assert.True(GuaTraceReader.Read(Path.GetDirectoryName(trace)!).Manifest.Finalized);
        if (mode == "delayed")
        {
            var values = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(trace)!, "snapshots"))
                .Select(file => JsonNode.Parse(File.ReadAllText(file))!["Reads"]!.AsArray())
                .SelectMany(reads => reads).Where(read => read?["Value"] is not null)
                .Select(read => read!["Value"]!["value"]!.GetValue<bool>()).ToArray();
            Assert.Contains(false, values); Assert.Contains(true, values);
        }
        Assert.NotEmpty(Directory.GetFiles(directory, "primary.json"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unsafe_actions_and_wrong_image_hash_refuse_before_launch(bool wrongHash)
    {
        if (!OperatingSystem.IsWindows()) return;
        var args = await Inputs(wrongHash ? "success" : "actions", Port(), wrongHash);
        using var output = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(args, output));
        Assert.Contains(wrongHash ? "RuntimeBuildHashMismatch" : "ObservationOnlyCapabilityUnavailable", output.ToString());
        Assert.False(File.Exists(Path.Combine(root, "pid")));
        Assert.Empty(Directory.GetFiles(root, "run.json", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task An_unrelated_listener_cannot_attest_the_launched_image_or_be_terminated()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var args = await Inputs("success", port);
        using var output = new StringWriter();
        Assert.Equal(1, await CliApplication.ExecuteAsync(args, output));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("ExecutionError", result.RootElement.GetProperty("code").GetString());
        Assert.Equal("Preparation", result.RootElement.GetProperty("details").GetProperty("phase").GetString());
        // The foreign listener remains owned by this test. A reachable port is never a build attestation.
        using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(listener.Pending());
        // A foreign owner can be rejected before the managed child reaches its marker write.
        var pid = result.RootElement.GetProperty("details").GetProperty("ownedProcessId").GetInt32();
        Assert.True(IsExited(pid));
    }
}
