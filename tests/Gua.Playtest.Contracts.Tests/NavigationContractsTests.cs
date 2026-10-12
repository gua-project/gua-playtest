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
}
