using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

public sealed class NavigationContractsTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "navigation-environment.json");
    [Fact]
    public async Task OptionalNavigationRoundTripsAndLegacyRemainsAbsent()
    {
        var validator = new StaticContractValidator([Path.GetDirectoryName(Fixture)!]);
        var legacy = await validator.ValidateFileAsync(Path.Combine(Path.GetDirectoryName(Fixture)!, "environment.json"));
        Assert.True(legacy.IsValid, legacy.Code);
        Assert.Null(Assert.IsType<EnvironmentDocument>(legacy.Document).Navigation);
        Assert.False(JsonNode.Parse(ContractJson.Serialize(legacy.Document!))!.AsObject().ContainsKey("navigation"));
        var report = await validator.ValidateFileAsync(Fixture);
        Assert.True(report.IsValid, report.Code); Assert.False(report.RuntimeVerified);
        var environment = Assert.IsType<EnvironmentDocument>(report.Document);
        Assert.Equal("Valid", NavigationProfileValidation.Validate(environment.Navigation!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(Fixture))!["navigation"],
            JsonNode.Parse(ContractJson.Serialize(environment))!["navigation"]));
    }

    [Theory]
    [InlineData("shape", "NavigationShapeInvalid")]
    [InlineData("offset", "NavigationShapeInvalid")]
    [InlineData("basis", "NavigationBasisInvalid")]
    [InlineData("camera", "NavigationBasisRequired")]
    [InlineData("body", "NavigationBasisRequired")]
    [InlineData("worldBasis", "NavigationBasisRequired")]
    [InlineData("tank", "SchemaInvalid")]
    [InlineData("layers", "SchemaInvalid")]
    [InlineData("region", "NavigationRegionInvalid")]
    [InlineData("freshness", "SchemaInvalid")]
    [InlineData("approach", "NavigationApproachInvalid")]
    [InlineData("identity", "NavigationIdentityInvalid")]
    [InlineData("velocityActor", "NavigationIdentityInvalid")]
    [InlineData("velocityWorld", "NavigationIdentityInvalid")]
    [InlineData("duplicateSelector", "NavigationIdentityInvalid")]
    [InlineData("actorDomain", "NavigationIdentityInvalid")]
    [InlineData("actorSelector", "NavigationIdentityInvalid")]
    [InlineData("disjointBand", "NavigationRegionInvalid")]
    [InlineData("targetCeiling", "SchemaInvalid")]
    [InlineData("vectorObject", "SchemaInvalid")]
    [InlineData("type", "NavigationObservationInvalid")]
    [InlineData("fixture", "NavigationFixtureMismatch")]
    [InlineData("missing", "SchemaInvalid")]
    [InlineData("unknown", "SchemaInvalid")]
    [InlineData("nonfinite", "NonFiniteNumber")]
    public async Task RejectsConcreteContractViolations(string change, string expected)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Fixture))!;
        var n = document["navigation"]!;
        switch (change)
        {
            case "shape": n["capsule"]!["height"] = 0.1; break;
            case "offset": n["capsule"]!["centerOffset"]!["y"] = 0; break;
            case "basis": n["plane"]!["axisY"] = n["plane"]!["axisX"]!.DeepClone(); break;
            case "camera": case "body": n["movement"]!["relativeTo"] = change; break;
            case "worldBasis": n["movement"]!["basis"] = new JsonObject { ["axisX"] = n["motion"]!["velocity"]!.DeepClone(), ["axisY"] = n["motion"]!["velocity"]!.DeepClone() }; break;
            case "tank": n["movement"]!["model"] = "tank"; break;
            case "layers": n["survey"]!["topology"] = "stackedLayers"; break;
            case "region": n["survey"]!["min"]!["x"] = 10; break;
            case "freshness": n["budgets"]!["maxObservationAgeMilliseconds"] = 0; break;
            case "approach": n["targets"]![0]!["approachPoints"]![0]!["x"] = 100; break;
            case "identity": n["targets"]![0]!["uncollected"]!["target"]!["selector"] = n["actorSelector"]!.DeepClone(); break;
            case "velocityActor": n["motion"]!["velocity"]!["x"]!["target"]!["selector"] = n["targets"]![0]!["selector"]!.DeepClone(); break;
            case "velocityWorld": n["motion"]!["velocity"]!["x"] = JsonNode.Parse("""{"target":{"source":"world"},"region":"property","name":"otherVelocityX","valueType":{"type":"number"}}"""); break;
            case "duplicateSelector": var duplicate = n["targets"]![0]!.DeepClone(); duplicate["domainId"] = "other-domain"; n["targets"]!.AsArray().Add(duplicate); break;
            case "actorDomain": n["targets"]![0]!["domainId"] = n["actorDomainId"]!.DeepClone(); break;
            case "actorSelector": n["targets"]![0]!["selector"] = n["actorSelector"]!.DeepClone(); n["targets"]![0]!["uncollected"]!["target"]!["selector"] = n["actorSelector"]!.DeepClone(); break;
            case "disjointBand": n["targets"] = new JsonArray(); n["survey"]!["minHeight"] = 4; n["survey"]!["maxHeight"] = 5; break;
            case "targetCeiling":
                var template = n["targets"]![0]!.DeepClone(); var targets = new JsonArray();
                for (var i = 0; i < 991; i++) { var t = template.DeepClone(); t["domainId"] = "coin-domain-" + i;
                    t["selector"]!["label"]!["value"] = "coin-" + i; t["uncollected"]!["target"]!["selector"] = t["selector"]!.DeepClone(); targets.Add(t); }
                n["targets"] = targets; break;
            case "vectorObject": n["motion"]!["velocity"]!["x"]!["valueType"]!["type"] = "object"; break;
            case "type": n["motion"]!["grounded"]!["valueType"]!["type"] = "number"; break;
            case "fixture": document.AsObject().Remove("fixture"); break;
            case "missing": n.AsObject().Remove("provenance"); break;
            case "unknown": n["calibrate"] = true; break;
        }
        var dir = Directory.CreateTempSubdirectory("navigation-contract-");
        try
        {
            var file = Path.Combine(dir.FullName, "environment.json");
            var text = document.ToJsonString();
            if (change == "nonfinite") text = text.Replace("\"maxSpeed\":4", "\"maxSpeed\":1e999", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, text);
            Assert.Equal(expected, (await new StaticContractValidator([dir.FullName]).ValidateFileAsync(file)).Code);
        }
        finally { dir.Delete(true); }
    }
    [Fact]
    public async Task HeightProjectionUsesSignedAndRotatedUp()
    {
        var report = await new StaticContractValidator([Path.GetDirectoryName(Fixture)!]).ValidateFileAsync(Fixture);
        var p = Assert.IsType<EnvironmentDocument>(report.Document).Navigation!;
        var negativeUp = new NavigationVector(-1,0,0);
        var negative = p with { Plane = new(negativeUp, new(0,1,0), new(0,0,1)), Targets = [],
            Capsule = p.Capsule with { CenterOffset = negativeUp.Scale(p.Capsule.Height/2) },
            Survey = p.Survey with { Min = new(0,-1,-5), Max = new(1,3,5), MinHeight = -0.75, MaxHeight = -0.25 } };
        Assert.Equal("Valid", NavigationProfileValidation.Validate(negative));
        Assert.Equal("NavigationRegionInvalid", NavigationProfileValidation.Validate(negative with { Survey = negative.Survey with { MinHeight = 1, MaxHeight = 2 } }));
        var v = 1/Math.Sqrt(2); var up = new NavigationVector(v,0,v);
        var rotated = p with { Plane = new(up,new(v,0,-v),new(0,1,0)), Targets = [],
            Capsule = p.Capsule with { CenterOffset = up.Scale(p.Capsule.Height/2) } };
        Assert.Equal("Valid", NavigationProfileValidation.Validate(rotated));
    }
}
