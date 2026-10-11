using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class CliEntryTests
{
    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Gua.Playtest.slnx"))) return directory.FullName;
            throw new InvalidOperationException("RepositoryUnavailable");
        }
    }
    private static string Fixture(string file) => Path.Combine(Repository, "fixtures", "contracts", file);
    // Avoid macOS /var -> /private/var links: private managed storage rejects linked paths.
    private static DirectoryInfo PrivateRoot() => Directory.CreateDirectory(Path.Combine(Repository, "TestResults", "cli-private-" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedPlanValidationPreservesExitFourBeforePublication(bool create)
    {
        var root = PrivateRoot();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            async ValueTask<StaticValidationReport> Interrupt(string input, IEnumerable<string> roots, CancellationToken token)
            {
                calls++; cancellation.Cancel();
                return await new StaticContractValidator(roots).ValidateFileAsync(input, token);
            }
            string[] args = create
                ? ["plan", "create", "--managed-root", root.FullName, "--allow-root", Path.GetDirectoryName(Fixture("scenario.json"))!,
                    "--scenario", Fixture("scenario.json"), "--recording", Fixture("recording.json"), "--timing", "recorded"]
                : ["plan", "accept", "--managed-root", root.FullName, "--plan", Path.Combine(root.FullName, "plan.json"),
                    "--trial", new string('a', 32), "--approve", "--approve-expectations"];
            using var output = new StringWriter(); using var diagnostics = new StringWriter();
            Assert.Equal(4, await OfflineCommands.ExecuteAsync(args, output, diagnostics, true, cancellation.Token, Interrupt));
            Assert.Equal(1, calls);
            Assert.Contains("CommandInterrupted", output.ToString());
            Assert.Empty(Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(ResultStatus.Passed, RunReason.GoalSatisfied, RunOrigin.Condition, 0)]
    [InlineData(ResultStatus.Failed, RunReason.FailureCondition, RunOrigin.Condition, 1)]
    [InlineData(ResultStatus.Invalid, RunReason.InvalidContract, RunOrigin.Contract, 2)]
    [InlineData(ResultStatus.TimedOut, RunReason.MaxDuration, RunOrigin.Budget, 3)]
    [InlineData(ResultStatus.Aborted, RunReason.Cancelled, RunOrigin.User, 4)]
    [InlineData(ResultStatus.Unverified, RunReason.ExplorationFinished, RunOrigin.Planner, 5)]
    [InlineData(ResultStatus.Failed, RunReason.ExecutionError, RunOrigin.Runner, 10)]
    public async Task RunDisplayReusesOutcomeAndPersistenceExitsWithoutHidingPrimary(ResultStatus status, RunReason reason, RunOrigin origin, int expected)
    {
        var primary = new PrimaryResult(status, new(reason, RunPhase.Execution, origin));
        foreach (var cleanupFailed in new[] { false, true })
        foreach (var storageFailed in new[] { false, true })
        {
            var outcome = new RunOutcome(primary, [], cleanupFailed ? [new(PostProcessingReason.ResourceReleaseUnconfirmed)] : [], []);
            var persistence = new PersistenceResult(storageFailed ? PersistenceFailure.InvalidEvidence : PersistenceFailure.None);
            using var output = new StringWriter(); using var error = new StringWriter();
            var actual = await CliApplication.WriteRunResultAsync(outcome, persistence, output, error, true);
            Assert.Equal(expected == 0 && (cleanupFailed || storageFailed) ? 11 : expected, actual);
            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal(status.ToString(), result.RootElement.GetProperty("status").GetString());
            Assert.Equal(reason.ToString(), result.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task JsonIsOneDocumentAndDiagnosticsAreSeparate()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(["replay", "--json", "--set", "goal=true"], output, error));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("Invalid", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("CommandArgumentsRequired", error.ToString().Trim());
        Assert.DoesNotContain("goal=true", output.ToString() + error);
        using var plain = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(["replay"], plain));
        Assert.StartsWith("Invalid:", plain.ToString());
    }

    [Theory]
    [InlineData("explore", "--scenario", "scenario.json")]
    [InlineData("replay", "--plan", "plan.json")]
    public async Task ValidInputsFailClosedWithoutRuntimeAndNeverBecomePassed(string command, string option, string file)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync([command, "--json", "--allow-root", Path.GetDirectoryName(Fixture(file))!, option, Fixture(file),
            "--environment", Fixture("environment.json"), "--max-actions", "1000"], output, error));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("ProductionRuntimeUnavailable", result.RootElement.GetProperty("code").GetString());
        var details = result.RootElement.GetProperty("details");
        Assert.False(details.GetProperty("runtimeVerified").GetBoolean());
        Assert.Equal(50, details.GetProperty("effective").GetProperty("maxActions").GetProperty("value").GetInt64());
        Assert.DoesNotContain("Purchase one item", output.ToString());
        Assert.DoesNotContain("ScenarioPassed", output.ToString());
        Assert.Equal("ProductionRuntimeUnavailable", error.ToString().Trim());
    }

    [Fact]
    public async Task StricterCeilingsIntersectionAndDenyPreserveGoalAndSourceBytes()
    {
        var validator = new StaticContractValidator([Path.GetDirectoryName(Fixture("scenario.json"))!]);
        var source = await validator.ValidateFileAsync(Fixture("scenario.json"));
        var environment = await validator.ValidateFileAsync(Fixture("environment.json"));
        var scenario = ((ScenarioDocument)source.Document!) with { Constraints = new(100000, 100, ["purchase", "forbidden"]) };
        var env = (EnvironmentDocument)environment.Document!;
        var before = source.Files.Values.Single().CopyBytes();
        var effective = EffectiveExecution.Resolve(scenario, env, 1000, 200000, ["purchase"]);
        Assert.Equal(50, effective.Limits.MaxActions);
        Assert.Equal(30000, effective.Limits.MaxDurationMilliseconds);
        Assert.Empty(effective.ActionIds);
        Assert.Equal(new EffectiveExecution.Ceiling(50, 100, 50, 1000), effective.MaxActions);
        Assert.False(effective.HostVerified);
        Assert.Same(((ScenarioDocument)source.Document!).Goal, scenario.Goal);
        Assert.Equal(before, source.Files.Values.Single().CopyBytes());
    }

    [Fact]
    public async Task PlanDispatchPreservesPinnedBytesAndRejectsForgedTrial()
    {
        var root = PrivateRoot();
        try
        {
            using var output = new StringWriter();
            var args = new[] { "plan", "create", "--json", "--managed-root", root.FullName, "--allow-root", Path.GetDirectoryName(Fixture("scenario.json"))!,
                "--scenario", Fixture("scenario.json"), "--recording", Fixture("recording.json"), "--timing", "recorded" };
            Assert.Equal(0, await CliApplication.ExecuteAsync(args, output));
            using var result = JsonDocument.Parse(output.ToString());
            var path = result.RootElement.GetProperty("details").GetProperty("plan").GetString()!;
            Assert.Equal(await File.ReadAllBytesAsync(Fixture("scenario.json")), await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, "scenario.json")));
            using var rejected = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["plan", "accept", "--json", "--managed-root", root.FullName, "--plan", path,
                "--trial", new string('a', 32), "--approve", "--approve-expectations"], rejected));
            Assert.Contains("PlanRejected", rejected.ToString());
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "accepted.json")));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanPublicationRejectsReplacementAfterSecretSnapshot(bool replaceRecording)
    {
        var root = PrivateRoot();
        try
        {
            var scenario = Path.Combine(root.FullName, "scenario.json"); var recording = Path.Combine(root.FullName, "recording.json");
            File.Copy(Fixture("scenario.json"), scenario); File.Copy(Fixture("recording.json"), recording);
            var validator = new StaticContractValidator([root.FullName]);
            var scenarioSnapshot = await validator.ValidateFileAsync(scenario);
            var recordingSnapshot = await validator.ValidateRecordingFileAsync(recording);
            Assert.True(scenarioSnapshot.IsValid); Assert.True(recordingSnapshot.IsValid);
            var hashes = new PlanCommand.InputHashes("", scenarioSnapshot.Files.Values.Single().Sha256, recordingSnapshot.Files.Values.Single().Sha256);
            var replacement = JsonNode.Parse(await File.ReadAllTextAsync(replaceRecording ? recording : scenario))!;
            if (replaceRecording)
            {
                replacement["steps"]![0]!["action"] = "set_value";
                replacement["steps"]![0]!["sensitive"] = true;
                replacement["steps"]![0]!["secretKey"] = "new-unsupplied-key";
            }
            else replacement["goal"]!["objective"] = "changed Goal after validation";
            await File.WriteAllTextAsync(replaceRecording ? recording : scenario, replacement.ToJsonString());
            var managed = Path.Combine(root.FullName, "managed");
            using var output = new StringWriter();
            Assert.Equal(2, await PlanCommand.ExecuteAsync(["plan", "create", "--managed-root", managed, "--allow-root", root.FullName,
                "--scenario", scenario, "--recording", recording, "--timing", "recorded"], output,
                new PersistenceRedactor([], []), default, hashes));
            Assert.Contains("PlanRejected", output.ToString());
            Assert.Empty(Directory.GetFiles(managed, "plan.json", SearchOption.AllDirectories));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task AcceptanceRejectsChangedPlanBeforeReadingTrial()
    {
        var root = PrivateRoot();
        try
        {
            var manager = new Gua.Playtest.Runner.Planning.PlanManager(root.FullName, new PersistenceRedactor([], []));
            var plan = await manager.CreateAsync(Fixture("scenario.json"), Fixture("recording.json"), [Path.GetDirectoryName(Fixture("scenario.json"))!], "recorded");
            var snapshot = await new StaticContractValidator([root.FullName]).ValidateFileAsync(plan);
            var hash = snapshot.Files.Values.Single(f => f.CopyJson()["kind"]?.GetValue<string>() == "replayPlan").Sha256;
            await File.AppendAllTextAsync(plan, "\n ");
            var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await manager.AcceptAsync(plan, "missing-trial", true, true, default, hash));
            Assert.Equal("PlanInputChanged", failure.Message);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plan)!, "accepted.json")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task PlanResolvesOnlyExplicitRequiredSecretsAndRejectsMarkerBeforeCopy()
    {
        var root = PrivateRoot();
        var variable = "GUA_CLI_TEST_" + Guid.NewGuid().ToString("N");
        const string marker = "private-marker-cli-123";
        try
        {
            Environment.SetEnvironmentVariable(variable, marker);
            var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("scenario.json")))!;
            scenario["goal"]!["objective"] = marker;
            var recording = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("recording.json")))!;
            recording["steps"]![0]!["secretKey"] = "login";
            recording["steps"]![0]!["sensitive"] = true;
            recording["steps"]![0]!["action"] = "set_value";
            var scenarioPath = Path.Combine(root.FullName, "scenario.json"); var recordingPath = Path.Combine(root.FullName, "recording.json");
            await File.WriteAllTextAsync(scenarioPath, scenario.ToJsonString()); await File.WriteAllTextAsync(recordingPath, recording.ToJsonString());
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["plan", "create", "--json", "--managed-root", Path.Combine(root.FullName, "managed"),
                "--allow-root", root.FullName, "--scenario", scenarioPath, "--recording", recordingPath, "--timing", "recorded", "--secret-env", "login=" + variable], output, error));
            Assert.DoesNotContain(marker, output.ToString() + error);
            Assert.Contains("PlanRejected", output.ToString());
            Assert.Empty(Directory.GetFiles(root.FullName, "plan.json", SearchOption.AllDirectories));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); root.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialSecretResolutionFailureNeverPublishesKnownDiagnosticValues(bool oversized)
    {
        var root = PrivateRoot(); var first = "GUA_PARTIAL_" + Guid.NewGuid().ToString("N");
        var second = "GUA_LARGE_" + Guid.NewGuid().ToString("N");
        var marker = oversized ? "CommandInputRejected" : "SecretUnavailable";
        try
        {
            Environment.SetEnvironmentVariable(first, marker);
            if (oversized) Environment.SetEnvironmentVariable(second, new string('x', 32767));
            var recording = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("recording.json")))!;
            var step = recording["steps"]![0]!; step["secretKey"] = "first"; step["sensitive"] = true; step["action"] = "set_value";
            var additional = step.DeepClone(); additional["secretKey"] = "second";
            ((JsonArray)recording["steps"]!).Add(additional);
            if (oversized)
            {
                var third = step.DeepClone(); third["secretKey"] = "third";
                ((JsonArray)recording["steps"]!).Add(third);
                var fourth = step.DeepClone(); fourth["secretKey"] = "fourth";
                ((JsonArray)recording["steps"]!).Add(fourth);
            }
            var file = Path.Combine(root.FullName, "recording.json"); await File.WriteAllTextAsync(file, recording.ToJsonString());
            var validation = await new StaticContractValidator([root.FullName]).ValidateRecordingFileAsync(file);
            Assert.True(validation.IsValid, validation.Code);
            var args = new List<string> { "plan", "create", "--json", "--managed-root", Path.Combine(root.FullName, "managed"),
                "--allow-root", root.FullName, "--scenario", Path.Combine(root.FullName, "scenario.json"), "--recording", file,
                "--timing", "recorded", "--secret-env", "first=" + first };
            File.Copy(Fixture("scenario.json"), Path.Combine(root.FullName, "scenario.json"));
            if (oversized) foreach (var key in new[] { "second", "third", "fourth" }) args.AddRange(["--secret-env", key + "=" + second]);
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(args.ToArray(), output, error));
            Assert.DoesNotContain(marker, output.ToString() + error);
            Assert.Equal("{}", output.ToString().Trim());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Empty(Directory.GetFiles(root.FullName, "plan.json", SearchOption.AllDirectories));
        }
        finally { Environment.SetEnvironmentVariable(first, null); Environment.SetEnvironmentVariable(second, null); root.Delete(true); }
    }

    [Fact]
    public async Task PartialSecretFailureBoundaryPreservesCancellationWithoutRevealingItsCode()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(4, await CommandOutput.WriteKnownFailureAsync(output, error, true, "Interrupted", "CommandInterrupted", 4, ["CommandInterrupted"]));
        Assert.DoesNotContain("CommandInterrupted", output.ToString() + error);
        using var document = JsonDocument.Parse(output.ToString());
    }

    [Fact]
    public async Task SavedReportDoesNotChangeSourceAndNeverOverwrites()
    {
        var root = Path.Combine(Repository, "TestResults", "cli-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var trace = await Probe("trace", Path.Combine(root, "trace"));
            Assert.Equal(0, trace.Exit); Assert.Empty(trace.Error);
            var source = trace.Output.Trim();
            var original = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var html = Path.Combine(root, "report.html");
            using var output = new StringWriter();
            Assert.Equal(0, await CliApplication.ExecuteAsync(["report", "--json", "--allow-root", root, "--trace", source, "--output", html], output));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("CommandSucceeded", document.RootElement.GetProperty("status").GetString());
            var saved = await File.ReadAllBytesAsync(html);
            using var rejected = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["report", "--json", "--allow-root", root, "--trace", source, "--output", html], rejected));
            Assert.Contains("ReportOutputExists", rejected.ToString());
            Assert.Equal(saved, await File.ReadAllBytesAsync(html));
            using var nested = new StringWriter();
            Assert.Equal(2, await CliApplication.ExecuteAsync(["report", "--json", "--allow-root", root, "--trace", source, "--output", Path.Combine(source, "new.html")], nested));
            Assert.Contains("ReportOutputInsideSource", nested.ToString());
            var caseAlias = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(source).ToUpperInvariant());
            var sameDirectory = Directory.Exists(caseAlias);
            if (!sameDirectory) Directory.CreateDirectory(caseAlias);
            using var aliasResult = new StringWriter();
            var aliasOutput = Path.Combine(caseAlias, "alias-report.html");
            Assert.Equal(sameDirectory ? 2 : 0, await CliApplication.ExecuteAsync(["report", "--json", "--allow-root", root, "--trace", source, "--output", aliasOutput], aliasResult));
            if (sameDirectory)
            {
                Assert.Contains("ReportOutputInsideSource", aliasResult.ToString());
                Assert.False(File.Exists(aliasOutput));
            }
            Assert.Equal(original.Keys.Order(), Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in original) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActualNoisyChildCannotPolluteParentJsonOrStderr()
    {
        var result = await Probe("noisy-parent");
        Assert.Equal(0, result.Exit); Assert.True(string.IsNullOrEmpty(result.Error), $"Child stderr escaped ({result.Error.Length} chars)");
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("NoisyChildIsolated", document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<(int Exit, string Output, string Error)> Probe(params string[] args)
    {
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(Repository, "tests", "Gua.Playtest.ProcessProbe", "bin", configuration, "net10.0", "Gua.Playtest.ProcessProbe.dll");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Repository };
        start.ArgumentList.Add(dll); foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await stdout, await stderr);
    }
}
