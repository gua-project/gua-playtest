using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Dashboard;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

// Test consumer of existing public contracts, never a Cloud service or a grader.
internal static class RunReportConsumptionAssertions
{
    internal static async Task VerifyAsync(JsonElement command, string scenarioPath, string outputRoot, string allowedRoot)
    {
        var details = command.GetProperty("details");
        var directory = details.GetProperty("artifactDirectory").GetString()!;
        var saved = await RunArtifactReader.ReadResultAsync(directory, new(8 * 1024 * 1024, 8 * 1024 * 1024, 10000, 65536));
        Assert.Equal(ResultReadState.Verified, saved.State);
        Assert.Equal(details.GetProperty("runId").GetString(), saved.Result!.RunId);
        Assert.Equal(command.GetProperty("status").GetString(), saved.Result.Status.ToString());
        Assert.Equal(command.GetProperty("code").GetString(), saved.Result.Reason);
        Assert.Equal(details.GetProperty("postProcessingComplete").GetBoolean(), saved.Result.PostProcessing.Complete);
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => SHA256.HashData(File.ReadAllBytes(file)));
        var registryRoot = Path.Combine(allowedRoot, "consumer-" + saved.Result.RunId);
        Directory.CreateDirectory(registryRoot);
        using var scenario = JsonDocument.Parse(await File.ReadAllBytesAsync(scenarioPath));
        var definition = scenario.RootElement;
        var registry = Path.Combine(registryRoot, "registry.json");
        await File.WriteAllTextAsync(registry, ContractJson.Serialize(new ScenarioRegistryDocument("consumer-contract",
            [new(definition.GetProperty("scenarioId").GetString()!, definition.GetProperty("definitionVersion").GetString()!,
                new(Path.GetRelativePath(registryRoot, scenarioPath).Replace('\\', '/'),
                    Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenarioPath)))))],
            [Path.GetRelativePath(registryRoot, outputRoot).Replace('\\', '/')])));
        var index = new DashboardIndex(registry, [outputRoot], [allowedRoot]);
        var indexed = await index.ReadAsync(); Assert.Equal("Ready", indexed.RegistryState);
        var run = Assert.Single(indexed.Runs, run => run.RunId == saved.Result.RunId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenarioPath))), run.ScenarioSha256);
        Assert.Equal(saved.Result.RunId, Assert.Single(DashboardIndex.Select(indexed, run.BuildId!, run.EnvironmentSha256!),
            selection => selection.Scenario.ScenarioId == definition.GetProperty("scenarioId").GetString()).Latest!.RunId);
        Assert.Equal(saved.Result.Status, run.Result!.Status);
        Assert.Equal(saved.Result.Reason, run.Result.Reason);
        Assert.Equal(saved.Result.PostProcessing.Complete, run.Result.PostProcessing.Complete);
        var report = Assert.Single(run.Details, detail => detail.Report is not null);
        Assert.Equal("Saved", report.State); Assert.Equal("Trace", report.Kind);
        var expectedHtml = index.ReadReport(report.Report!); Assert.NotEmpty(expectedHtml);
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = DashboardApplication.RunAsync(index, port, uri => ready.TrySetResult(uri), stop.Token);
        try
        {
            var origin = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var client = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
            using var snapshot = JsonDocument.Parse(await client.GetStringAsync("/api/snapshot"));
            var viaHttp = Assert.Single(snapshot.RootElement.GetProperty("runs").EnumerateArray(),
                item => item.GetProperty("runId").GetString() == saved.Result.RunId);
            Assert.Equal(saved.Result.Status.ToString(), viaHttp.GetProperty("result").GetProperty("status").GetString());
            var link = Assert.Single(viaHttp.GetProperty("details").EnumerateArray(),
                item => item.TryGetProperty("report", out var value) && value.ValueKind == JsonValueKind.String)
                .GetProperty("report").GetString()!;
            Assert.Equal(report.Report, link);
            using var response = await client.GetAsync(link);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            var wrapper = await response.Content.ReadAsStringAsync();
            Assert.Contains("sandbox=\"allow-scripts\"", wrapper);
            var embedded = Regex.Match(wrapper, "srcdoc=\"([^\"]*)\"");
            Assert.True(embedded.Success);
            Assert.Equal(Encoding.UTF8.GetString(expectedHtml), WebUtility.HtmlDecode(embedded.Groups[1].Value));
        }
        finally
        {
            stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(before.Count, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        foreach (var file in before) Assert.Equal(file.Value, SHA256.HashData(await File.ReadAllBytesAsync(file.Key)));
    }
}
