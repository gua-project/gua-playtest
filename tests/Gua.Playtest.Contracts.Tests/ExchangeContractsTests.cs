using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

public sealed class ExchangeContractsTests
{
    private static JsonObject Envelope(JsonObject decision) => new()
    {
        ["kind"]="plannerDecision", ["schemaVersion"]=1, ["runId"]="run-1",
        ["decisionRequestId"]="decision-1", ["basedOnObservationId"]="observation-1", ["decision"]=decision.DeepClone()
    };

    [Fact]
    public async Task AllDecisionBranchesHaveTypedModelsAndCannotClaimResult()
    {
        using var scope = new Scope();
        JsonObject[] branches = [
            new() { ["kind"]="execute", ["mode"]="single", ["action"]=new JsonObject { ["kind"]="semantic", ["actionId"]="jump", ["operation"]="press" } },
            new() { ["kind"]="execute", ["mode"]="timed", ["segment"]=new JsonObject { ["schemaVersion"]=1,["durationMilliseconds"]=100,["maxLatenessMilliseconds"]=0,["executionTimeoutMilliseconds"]=1000,["cleanupTimeoutMilliseconds"]=1000,["inputs"]=new JsonArray(new JsonObject { ["offsetMilliseconds"]=0,["kind"]=1,["operation"]=1,["target"]="jump" }) } },
            new() { ["kind"]="observe", ["reads"]=new JsonArray(new JsonObject { ["target"]=new JsonObject { ["source"]="world" },["region"]="property",["name"]="phase",["valueType"]=new JsonObject { ["type"]="enum",["enumType"]="Fixture.Phase" } }) },
            new() { ["kind"]="wait", ["durationMilliseconds"]=100 },
            new() { ["kind"]="finish", ["report"]="goalClaimed" }
        ];
        foreach (var branch in branches)
        {
            var result = await scope.Validate(Envelope(branch));
            Assert.True(result.IsValid, result.Code); Assert.IsType<PlannerDecisionDocument>(result.Document);
            Assert.False(result.RuntimeVerified);
            branch["status"]="Passed";
            Assert.Equal("SchemaInvalid", (await scope.Validate(Envelope(branch))).Code);
            branch.Remove("status");
        }
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("owner")]
    [InlineData("requestId")]
    [InlineData("shell")]
    [InlineData("url")]
    [InlineData("profile")]
    public async Task PlannerCannotAddControlFields(string name)
    {
        using var scope = new Scope();
        var action = new JsonObject { ["kind"]="semantic", ["actionId"]="jump", ["operation"]="press", [name]="SECRET_MARKER" };
        var result = await scope.Validate(Envelope(new() { ["kind"]="execute", ["mode"]="single", ["action"]=action }));
        Assert.Equal("SchemaInvalid", result.Code); Assert.DoesNotContain("SECRET_MARKER", result.ToString());
    }

    [Fact]
    public async Task TimedBoundsAndSingleRawCleanupAreRejected()
    {
        using var scope = new Scope();
        var input = new JsonObject { ["offsetMilliseconds"]=2,["kind"]=2,["operation"]=1,["target"]="Space" };
        var segment = new JsonObject { ["schemaVersion"]=1,["durationMilliseconds"]=1,["maxLatenessMilliseconds"]=0,["executionTimeoutMilliseconds"]=1000,["cleanupTimeoutMilliseconds"]=1000,["inputs"]=new JsonArray(input) };
        Assert.Equal("SegmentTimingInvalid", (await scope.Validate(Envelope(new() { ["kind"]="execute",["mode"]="timed",["segment"]=segment }))).Code);
        var raw = new JsonObject { ["kind"]="raw", ["input"]=new JsonObject { ["offsetMilliseconds"]=0,["kind"]=6,["operation"]=10,["target"]="" } };
        Assert.Equal("ActionForbidden", (await scope.Validate(Envelope(new() { ["kind"]="execute", ["mode"]="single", ["action"]=raw }))).Code);
    }

    [Fact]
    public async Task EnumCollectionAndUnsupportedOperatorTypesFailStatically()
    {
        using var scope = new Scope();
        var scenario = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","scenario.json")))!.AsObject();
        var assertion = scenario["goal"]!["success"]!;
        assertion["read"]=new JsonObject { ["target"]=new JsonObject { ["source"]="world" },["region"]="property",["name"]="phases",["valueType"]=new JsonObject { ["type"]="set",["elementType"]="enum",["enumType"]="Fixture.Phase" } };
        assertion["expected"]=new JsonObject { ["type"]="set",["elementType"]="enum",["enumType"]="Other.Phase",["value"]=new JsonArray("Second") };
        Assert.Equal("TypeMismatch", (await scope.Validate(scenario)).Code);
        assertion["expected"]!["enumType"]="Fixture.Phase";
        Assert.True((await scope.Validate(scenario)).IsValid);
        assertion["operator"]="containsSequence";
        Assert.Equal("TypeMismatch", (await scope.Validate(scenario)).Code);
        assertion["operator"]="equals"; assertion["expected"]!["value"]=new JsonArray("Second","Second");
        Assert.Equal("SchemaInvalid", (await scope.Validate(scenario)).Code);
    }

    [Fact]
    public async Task ResultStatusesAreRepresentationNotGradingAndDoNotRequireCodex()
    {
        using var scope = new Scope();
        foreach (var status in Enum.GetNames<ResultStatus>())
        {
            var result = await scope.Validate(new JsonObject { ["kind"]="result",["schemaVersion"]=1,["runId"]="run-1",["status"]=status,["phase"]="Running",["origin"]="runner",["reason"]="fixture",["postProcessing"]=new JsonObject { ["complete"]=false,["reasons"]=new JsonArray("input-release-unconfirmed") },["finishedAt"]="2026-10-06T13:00:00Z" });
            Assert.True(result.IsValid, result.Code); Assert.IsType<ResultDocument>(result.Document); Assert.False(result.RuntimeVerified);
        }
    }

    [Fact]
    public async Task PlannerInputKeepsIndependentSourcesCompletenessAndValueType()
    {
        using var scope = new Scope();
        var environment = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","environment.json")))!;
        var read = environment["permissions"]!["reads"]![0]!.DeepClone();
        var observationRead = new JsonObject { ["read"]=read,["status"]="available",["value"]=new JsonObject { ["type"]="bool",["value"]=true } };
        var input = new JsonObject { ["kind"]="plannerInput",["schemaVersion"]=1,["runId"]="run-1",["decisionRequestId"]="decision-1",["basedOnObservationId"]="observation-1",["objective"]="Check the exposed checkbox",["limits"]=environment["limits"]!.DeepClone(),["remaining"]=new JsonObject { ["actions"]=1,["decisions"]=1,["durationMilliseconds"]=1000 },["observation"]=new JsonObject { ["observationId"]="observation-1",["observedAt"]="2026-10-06T13:00:00Z",["profile"]="Player",["sources"]=new JsonArray(new JsonObject { ["source"]="ui",["sourceId"]="source-1",["sessionEpoch"]=1,["revision"]=1,["frameSequence"]=1 }),["complete"]=true,["reads"]=new JsonArray(observationRead) },["actionDefinitions"]=new JsonObject { ["schemaVersion"]=1,["sessionEpoch"]=1,["revision"]=1,["context"]="fixture",["actions"]=new JsonArray() },["feedback"]=new JsonArray() };
        var result = await scope.Validate(input); Assert.True(result.IsValid, result.Code); Assert.IsType<PlannerInputDocument>(result.Document);
        observationRead["status"]="gap"; observationRead.Remove("value");
        Assert.Equal("ObservationShapeInvalid", (await scope.Validate(input)).Code);
        input["observation"]!["complete"]=false;
        Assert.True((await scope.Validate(input)).IsValid);
        input["basedOnObservationId"]="different";
        Assert.Equal("IdentityMismatch", (await scope.Validate(input)).Code);
    }

    [Fact]
    public async Task ModelsSerializeToTheirSchemaWithOptionalFieldsOmitted()
    {
        using var scope = new Scope();
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory,"fixtures");
        foreach (var name in new[] { "scenario.json", "environment.json" })
        {
            var original = await new StaticContractValidator([fixtureRoot]).ValidateFileAsync(Path.Combine(fixtureRoot,name));
            Assert.True(original.IsValid, original.Code);
            var serialized = JsonNode.Parse(ContractJson.Serialize(original.Document!))!.AsObject();
            Assert.Equal(original.Document!.Kind, serialized["kind"]!.GetValue<string>());
            var roundtrip = await scope.Validate(serialized);
            Assert.True(roundtrip.IsValid, roundtrip.Code); Assert.Equal(original.Document.GetType(), roundtrip.Document!.GetType());
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("gua-exchange-").FullName;
        public async Task<StaticValidationReport> Validate(JsonObject value)
        {
            var file = Path.Combine(root,"input.json"); await File.WriteAllTextAsync(file,value.ToJsonString());
            return await new StaticContractValidator([root]).ValidateFileAsync(file);
        }
        public void Dispose() => Directory.Delete(root,true);
    }
}
