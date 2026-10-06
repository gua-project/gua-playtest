using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Planners.Codex;
using Gua.Playtest.Runner;
using Gua.Testing;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class FoundationTests
{
    [Fact]
    public async Task ClockRejectsInfiniteDelayAndHonorsCancellation()
    {
        var clock = new MonotonicClock();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await clock.DelayAsync(Timeout.InfiniteTimeSpan, CancellationToken.None));
        var fake = new FakeClock();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fake.DelayAsync(Timeout.InfiniteTimeSpan, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await clock.DelayAsync(TimeSpan.FromSeconds(1), cancelled.Token));
    }

    [Fact]
    public void PublicNativePackagesMatchPinnedCompatibility()
    {
        PackageCompatibility.CheckNative();
        using var core = new Gua.Core.GuaContext();
        Assert.Throws<Gua.Core.GuaCompatibilityException>(() => core.GetVersion().EnsureCompatible(
            requiredCapabilities: ["nonexistent-playtest-fixture-capability"]));
    }

    [Theory]
    [InlineData("{\"id\":{\"value\":\"ready\"}}", ValidationStatus.Valid, 0)]
    [InlineData("{\"id\":42}", ValidationStatus.Invalid, 2)]
    [InlineData("{", ValidationStatus.Invalid, 2)]
    public async Task CliAndLibraryShareRealPackagedValidator(string json, ValidationStatus expected, int exit)
    {
        var result = await new ValidationRunner(new PackagedSchemaValidator("selector.schema.json")).ValidateAsync(json);
        Assert.Equal(expected, result.Status);
        Assert.Equal(exit, result.ExitCode);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, json);
            using var output = new StringWriter();
            Assert.Equal(exit, await CliApplication.ExecuteAsync(["validate", "--gua-schema", "selector.schema.json", file], output));
            using var doc = JsonDocument.Parse(output.ToString());
            Assert.Equal(result.Code, doc.RootElement.GetProperty("code").GetString());
            Assert.Equal(expected.ToString(), doc.RootElement.GetProperty("status").GetString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ScenarioValidationRequiresExplicitAllowedRoot()
    {
        using var output = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(["validate", "scenario.json"], output));
        Assert.Contains("explicit-validation-arguments-required", output.ToString());
    }

    [Fact]
    public async Task MissingAndBrokenAllowedRootsReturnStructuredInvalid()
    {
        var scope = Directory.CreateTempSubdirectory("gua-root-cli-");
        var missing = Path.Combine(scope.FullName,"missing");
        var target = Directory.CreateDirectory(Path.Combine(scope.FullName,"target"));
        var broken = Path.Combine(scope.FullName,"broken");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(broken); start.ArgumentList.Add(target.FullName);
                using var process = System.Diagnostics.Process.Start(start)!;
                await process.WaitForExitAsync(); Assert.Equal(0,process.ExitCode);
            }
            else Directory.CreateSymbolicLink(broken,target.FullName);
            target.Delete();
            foreach (var root in new[] { missing, broken })
            {
                using var output = new StringWriter();
                Assert.Equal(2,await CliApplication.ExecuteAsync(["validate","--allow-root",root,"input.json"],output));
                using var document = JsonDocument.Parse(output.ToString());
                Assert.Equal("Invalid",document.RootElement.GetProperty("status").GetString());
                Assert.Equal("InvalidAllowedRoots",document.RootElement.GetProperty("code").GetString());
                Assert.DoesNotContain(scope.FullName,output.ToString());
            }
        }
        finally
        {
            if (new DirectoryInfo(broken).LinkTarget is not null)
            {
                if (OperatingSystem.IsWindows()) Directory.Delete(broken);
                else File.Delete(broken);
            }
            scope.Delete(true);
        }
    }

    [Fact]
    public async Task UnknownSchemaDoesNotFetchAnything()
    {
        var result = await new ValidationRunner(new PackagedSchemaValidator("https://example.invalid/schema")).ValidateAsync("{}");
        Assert.Equal(new(ValidationStatus.Invalid, "unknown-gua-schema"), result);
    }

    [Fact]
    public async Task PackageLocalReferencesRejectInvalidNestedValue()
    {
        const string valid = """{"schemaVersion":2,"sessionEpoch":1,"revision":0,"context":"fixture","count":1,"truncated":false,"actions":[{"id":"move","description":"Move","valueType":"axis1d","holdable":false,"active":true,"bindings":[],"risk":"safe","requiresConfirmation":false,"valueSchema":{"type":"number"}}]}""";
        var runner = new ValidationRunner(new PackagedSchemaValidator("game-input-action-search-v2.schema.json"));
        Assert.Equal(ValidationStatus.Valid, (await runner.ValidateAsync(valid)).Status);
        Assert.Equal(ValidationStatus.Invalid, (await runner.ValidateAsync(valid.Replace("\"number\"", "\"invalid\""))).Status);
    }

    [Fact]
    public async Task CancellationNeverInvokesProviderOrReturnsValid()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new FakeValidator((_, _) => new(ValidationStatus.Valid, "valid"));
        var result = await new ValidationRunner(fake).ValidateAsync("{}", cancellation.Token);
        Assert.Equal(ValidationStatus.Interrupted, result.Status);
        Assert.Equal(130, result.ExitCode);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task CancellationDuringProviderWinsOverReturnedValid()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeValidator((_, _) => { cancellation.Cancel(); return new(ValidationStatus.Valid, "valid"); });
        Assert.Equal(ValidationStatus.Interrupted, (await new ValidationRunner(fake).ValidateAsync("{}", cancellation.Token)).Status);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task ProviderFaultActuallyFiresAndDoesNotLeakSecret()
    {
        var fake = new FakeValidator((_, _) => throw new InvalidOperationException("secret-token=do-not-print"));
        var result = await new ValidationRunner(fake).ValidateAsync("{}");
        Assert.Equal(1, fake.Calls);
        Assert.Equal(new(ValidationStatus.Unavailable, "validation-provider-failed"), result);
        Assert.DoesNotContain("do-not-print", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task FileFaultsAndSizeLimitAreNonzeroAndSanitized()
    {
        using var missing = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(["validate", "--gua-schema", "selector.schema.json", "secret-path/not-found.json"], missing));
        Assert.DoesNotContain("secret-path", missing.ToString());
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[CliApplication.MaxDocumentBytes + 1]);
            using var large = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["validate", "--gua-schema", "selector.schema.json", path], large));
            Assert.Contains("document-too-large", large.ToString());
            await File.WriteAllBytesAsync(path, [0xff]);
            using var bad = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["validate", "--gua-schema", "selector.schema.json", path], bad));
            Assert.Contains("document-unreadable", bad.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TestOnlyPortsModelTimeCancellationAndOwnedCleanup()
    {
        var clock = new FakeClock();
        await clock.DelayAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Elapsed);
        var observation = new FakeObservationSource();
        var planner = new FakePlanner();
        Assert.Equal(1, await planner.DecideAsync(await observation.CaptureAsync(CancellationToken.None), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await observation.CaptureAsync(cancelled.Token));
        Assert.Equal(1, observation.Captures);
        var host = new FakeHostSession();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await host.CleanupAsync(cancelled.Token));
        Assert.False(host.OwnedResourceReleased);
        await host.CleanupAsync(CancellationToken.None);
        await host.DisposeAsync();
        Assert.Equal(1, host.CleanupCount);
        Assert.True(host.AttachedProcessRunning);
    }

    [Fact]
    public async Task CodexModuleCannotPretendToSupplyDecision()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await new CodexPlanner<int, int>().DecideAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task GuaTraceIsPackageOnlyAndRedactsBeforeSaving()
    {
        // macOS temp roots traverse /var -> /private/var. Gua intentionally rejects linked Trace paths.
        var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playtest-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new GuaTraceOptions
            { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always, Profile = "player", Secrets = ["secret-token"] });
            using var telemetry = new TraceFlushTelemetry(trace);
            var step = trace.BeginStep(GuaTraceStepKind.Mark, "fixture secret-token");
            Assert.True(trace.Record(step, "fixture", JsonSerializer.SerializeToElement(new { token = "secret-token" }), sensitive: true));
            trace.EndStep(step, GuaTraceOutcome.Passed);
            var completed = await trace.CompleteAsync(GuaTraceOutcome.Passed);
            if (!completed) telemetry.ReportFailure();
            Assert.True(completed, string.Join(",", trace.Status.Issues));
            var read = GuaTraceReader.Read(trace.ArtifactPath);
            Assert.True(read.Manifest.Finalized);
            Assert.Empty(read.Issues);
            Assert.Equal(3, read.Events.Count);
            var html = Path.Combine(root, "report.html");
            Assert.True(GuaTraceReport.WriteHtml(trace.ArtifactPath, html).Succeeded);
            Assert.False(GuaTraceReport.WriteHtml(trace.ArtifactPath, html).Succeeded);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("secret-token", await File.ReadAllTextAsync(file));
            Assert.Contains("connect-src 'none'", await File.ReadAllTextAsync(html));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
