using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

[CollectionDefinition("cwd", DisableParallelization = true)]
public sealed class CurrentDirectoryCollection;

[Collection("cwd")]
public sealed class StaticContractsTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static StaticContractValidator Validator => new([Fixtures]);

    [Fact]
    public async Task CommittedIndependentFixtureExpectations()
    {
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Fixtures, "expected.json")))!.AsArray();
        Assert.True(cases.Count >= 18);
        foreach (var test in cases)
        {
            var result = await Validator.ValidateFileAsync(Path.Combine(Fixtures, test!["file"]!.GetValue<string>()));
            Assert.Equal(test["expected"]!.GetValue<string>(), result.Code);
            Assert.False(result.RuntimeVerified);
        }
    }

    [Fact]
    public async Task JsonAndYamlProduceTheSameModelAndDefault()
    {
        var json = await Validator.ValidateFileAsync(Path.Combine(Fixtures, "scenario.json"));
        var yaml = await Validator.ValidateFileAsync(Path.Combine(Fixtures, "scenario.yaml"));
        Assert.True(json.IsValid, json.Code); Assert.True(yaml.IsValid, yaml.Code);
        var a = Assert.IsType<ScenarioDocument>(json.Document); var b = Assert.IsType<ScenarioDocument>(yaml.Document);
        Assert.Equal(a.ScenarioId, b.ScenarioId); Assert.Equal(a.DefinitionVersion, b.DefinitionVersion);
        Assert.Equal(a.Goal.Objective, b.Goal.Objective); Assert.True(JsonNode.DeepEquals(a.Goal.Success, b.Goal.Success));
        Assert.Equal(a.Constraints, b.Constraints);
        var environment = await Validator.ValidateFileAsync(Path.Combine(Fixtures, "environment.json"));
        Assert.Equal(100, Assert.IsType<EnvironmentDocument>(environment.Document).Limits.TraceRecentSteps);
    }

    [Fact]
    public async Task FileRelativeReferencesIgnoreCwdButEntryArgumentsUseCwd()
    {
        var cwd = Directory.GetCurrentDirectory();
        var absolute = Path.Combine(Fixtures, "plan.json");
        var temp = Directory.CreateTempSubdirectory("gua-contract-cwd-");
        try
        {
            Directory.SetCurrentDirectory(temp.FullName);
            var result = await Validator.ValidateFileAsync(absolute);
            Assert.True(result.IsValid, result.Code); Assert.Equal(3, result.Files.Count);
            Assert.Equal("ReferenceMissing", (await Validator.ValidateFileAsync("plan.json")).Code);
            Directory.SetCurrentDirectory(Fixtures);
            Assert.True((await Validator.ValidateFileAsync("plan.json")).IsValid);
        }
        finally { Directory.SetCurrentDirectory(cwd); temp.Delete(); }
    }

    [Fact]
    public async Task HashPinsExactBytesAndRetainedContentCannotBeReplaced()
    {
        using var scope = new FixtureScope();
        var validator = new StaticContractValidator([scope.Root]);
        var result = await validator.ValidateFileAsync(Path.Combine(scope.Root, "plan.json"));
        Assert.True(result.IsValid, result.Code);
        var file = Assert.Single(result.Files.Values, v => Path.GetFileName(v.Path) == "scenario.json"); var original = file.CopyBytes();
        await File.AppendAllTextAsync(file.Path, " \n");
        Assert.Equal(original, file.CopyBytes());
        var mutableCopy = file.CopyBytes(); mutableCopy[0] ^= 1; Assert.Equal(original, file.CopyBytes());
        Assert.Equal("HashMismatch", (await validator.ValidateFileAsync(Path.Combine(scope.Root, "plan.json"))).Code);
    }

    [Fact]
    public async Task PathsRejectParentEscapeSiblingPrefixAndJunctionEscape()
    {
        using var scope = new FixtureScope();
        var outside = Directory.CreateTempSubdirectory("gua-contract-outside-");
        var sibling = Directory.CreateDirectory(scope.Root + "-sibling");
        var junction = Path.Combine(scope.Root, "escape-link");
        try
        {
            File.Copy(Path.Combine(scope.Root, "scenario.json"), Path.Combine(outside.FullName, "scenario.json"));
            File.Copy(Path.Combine(scope.Root, "scenario.json"), Path.Combine(sibling.FullName, "scenario.json"));
            var validator = new StaticContractValidator([scope.Root]);
            Assert.Equal("PathOutsideAllowedRoots", (await validator.ValidateFileAsync(Path.Combine(sibling.FullName, "scenario.json"))).Code);
            await ChangeReference(scope.Root, Path.GetRelativePath(scope.Root, Path.Combine(outside.FullName, "scenario.json")));
            Assert.Equal("PathOutsideAllowedRoots", (await validator.ValidateFileAsync(Path.Combine(scope.Root, "plan.json"))).Code);
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(outside.FullName);
                using var process = Process.Start(start)!; await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(junction, outside.FullName);
            await ChangeReference(scope.Root, "escape-link/scenario.json");
            Assert.Equal("PathOutsideAllowedRoots", (await validator.ValidateFileAsync(Path.Combine(scope.Root, "plan.json"))).Code);
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction); // unlink only, never recurse into target
            outside.Delete(true); sibling.Delete(true);
        }
    }

    [Fact]
    public async Task SecretsAndParserMessagesNeverEnterDiagnostics()
    {
        using var scope = new FixtureScope();
        const string marker = "SECRET_MARKER_ISSUE_2";
        var path = Path.Combine(scope.Root, "secret.json");
        var validator = new StaticContractValidator([scope.Root]);
        foreach (var contents in new[] { "{\"" + marker + "\":123}", "{\"kind\":\"" + marker + "\"", "{\"value\":1e9999,\"secret\":\"" + marker + "\"}" })
        {
            await File.WriteAllTextAsync(path, contents);
            var result = await validator.ValidateFileAsync(path);
            Assert.False(result.IsValid); Assert.DoesNotContain(marker, result.ToString()); Assert.Empty(result.Files);
        }
    }

    [Fact]
    public async Task RegistryRejectsDuplicateAndWrongDefinitionIdentity()
    {
        using var scope = new FixtureScope();
        var path = Path.Combine(scope.Root, "registry.json"); var registry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        registry["scenarios"]!.AsArray().Add(registry["scenarios"]![0]!.DeepClone());
        await File.WriteAllTextAsync(path, registry.ToJsonString());
        Assert.Equal("DuplicateScenarioId", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
        registry["scenarios"]!.AsArray().RemoveAt(1); registry["scenarios"]![0]!["definitionVersion"] = "v2";
        await File.WriteAllTextAsync(path, registry.ToJsonString());
        Assert.Equal("IdentityMismatch", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
    }

    [Fact]
    public async Task PlanRejectsOutOfRangeAndRecordedWaitConflict()
    {
        using var scope = new FixtureScope(); var path = Path.Combine(scope.Root, "plan.json");
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(path))!; plan["checkpoints"]![0]!["beforeStep"] = 2;
        await File.WriteAllTextAsync(path, plan.ToJsonString());
        Assert.Equal("RecordingRangeInvalid", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
        plan["checkpoints"]![0]!["beforeStep"] = 1; plan["timing"] = "recorded";
        await File.WriteAllTextAsync(path, plan.ToJsonString());
        Assert.Equal("RecordingWaitConflict", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
    }

    [Fact]
    public async Task StandardAndObserveAreDifferentRegionsAndRuntimeUnverified()
    {
        using var scope = new FixtureScope(); var path = Path.Combine(scope.Root, "scenario.json");
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var read = scenario["goal"]!["success"]!["read"]!;
        read["field"] = "visible"; await File.WriteAllTextAsync(path, scenario.ToJsonString());
        Assert.True((await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).IsValid);
        read["region"] = "observe"; read.AsObject().Remove("field"); read["name"] = "visible";
        await File.WriteAllTextAsync(path, scenario.ToJsonString());
        Assert.True((await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).IsValid);
        read["target"]!["selector"]!["runtimeId"] = "stale";
        await File.WriteAllTextAsync(path, scenario.ToJsonString());
        Assert.Equal("SchemaInvalid", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
    }

    [Fact]
    public async Task CancellingValidationAndMissingFiniteLimitsFailExplicitly()
    {
        var cancellation = new CancellationToken(true);
        var adapter = new ContractValidatorAdapter([Fixtures]);
        Assert.Equal(ValidationStatus.Interrupted, (await adapter.ValidateAsync(Path.Combine(Fixtures, "scenario.json"), cancellation)).Status);
        using var scope = new FixtureScope(); var path = Path.Combine(scope.Root, "environment.json");
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(-1), JsonValue.Create(0), JsonValue.Create("Infinity") })
        {
            environment["limits"]!["plannerTimeoutMilliseconds"] = value;
            await File.WriteAllTextAsync(path, environment.ToJsonString());
            Assert.Equal("SchemaInvalid", (await new StaticContractValidator([scope.Root]).ValidateFileAsync(path)).Code);
        }
    }

    [Fact]
    public void EmbeddedSchemasHaveClosedLocalReferencesAndGuaBytesMatchManifest()
    {
        Assert.Contains("value-v1.schema.json", ContractSchemas.Names);
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/schemas/gua-1.1.1"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "manifest.json")))!;
        foreach (var (name, hash) in manifest["files"]!.AsObject())
        {
            var bytes = File.ReadAllBytes(Path.Combine(source, name));
            Assert.Equal(hash!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(File.ReadAllText(Path.Combine(source, name)), ContractSchemas.ReadSchema(name));
        }
    }

    private static async Task ChangeReference(string root, string path)
    {
        var file = Path.Combine(root, "plan.json"); var plan = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        plan["scenario"]!["path"] = path; await File.WriteAllTextAsync(file, plan.ToJsonString());
    }

    private sealed class FixtureScope : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("gua-contract-fixture-").FullName;
        public FixtureScope() { foreach (var file in Directory.GetFiles(Fixtures)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file))); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
