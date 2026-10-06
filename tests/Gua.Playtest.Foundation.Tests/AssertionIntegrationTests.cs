using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Runner;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class AssertionIntegrationTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();
    private static JsonObject Fixture() => Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gua-value-v1.json")));
    private static JsonObject Assertion(JsonObject expected)
    {
        var type = expected.DeepClone().AsObject(); type.Remove("value");
        var assertion = Json("{\"kind\":\"assertion\",\"quantifier\":\"one\",\"operator\":\"equals\",\"read\":{\"target\":{\"source\":\"world\"},\"region\":\"property\",\"name\":\"test\"}}");
        assertion["read"]!["valueType"] = type; assertion["expected"] = expected.DeepClone(); return assertion;
    }

    [Fact]
    public void RealPinnedNativeValueAndIndependentUpstreamGoldenAgree()
    {
        var fixture = Fixture();
        using var nativeCatalog = GuaEnumCatalog.FromJson(fixture["catalog"]!.ToJsonString());
        var catalog = EnumCatalogSnapshot.Create(fixture["catalog"]!.AsObject());
        int valid = 0, comparisons = 0, invalid = 0;
        foreach (var example in fixture["valid"]!.AsArray())
        {
            var wire = example!["json"]!.GetValue<string>();
            using var native = GuaValue.FromJson(wire, nativeCatalog);
            using var roundtrip = GuaValue.FromJson(native.ToJson(), nativeCatalog);
            Assert.True(native.ValueEquals(roundtrip));
            var prepared = PreparedAssertion.Create(Assertion(Json(wire)), new(10, 1000), catalog);
            Assert.Equal(EvaluationResult.Known(true), prepared.EvaluateJson(wire, catalog));
            Assert.Equal(EvaluationResult.Known(true), prepared.EvaluateJson(native.ToJson(), catalog));
            valid++;
        }
        foreach (var example in fixture["comparisons"]!.AsArray())
        {
            var leftWire = example!["left"]!.GetValue<string>(); var rightWire = example["right"]!.GetValue<string>();
            var expected = example["equal"]!.GetValue<bool>();
            using var leftNative = GuaValue.FromJson(leftWire, nativeCatalog);
            using var rightNative = GuaValue.FromJson(rightWire, nativeCatalog);
            Assert.Equal(expected, leftNative.ValueEquals(rightNative));
            var left = Json(leftWire); var right = Json(rightWire);
            var leftType = left.DeepClone().AsObject(); leftType.Remove("value");
            var rightType = right.DeepClone().AsObject(); rightType.Remove("value");
            var config = Assertion(right); config["read"]!["valueType"] = leftType.DeepClone();
            if (JsonNode.DeepEquals(leftType, rightType))
                Assert.Equal(EvaluationResult.Known(expected), PreparedAssertion.Create(config, new(10, 1000), catalog).EvaluateJson(leftWire, catalog));
            else
                Assert.Throws<AssertionConfigurationException>(() => PreparedAssertion.Create(config, new(10, 1000), catalog)); // assertions require matching declared types
            comparisons++;
        }
        var comparison = PreparedAssertion.Create(Assertion(Json("{\"type\":\"string\",\"value\":\"x\"}")), new(10, 1000));
        foreach (var example in fixture["invalid"]!.AsArray())
        {
            var wire = example!["json"]!.GetValue<string>();
            Assert.Throws<GuaValueException>(() => GuaValue.FromJson(wire, nativeCatalog));
            Assert.Equal(EvaluationError.ObservationContractViolation, comparison.EvaluateJson(wire, catalog).Error);
            invalid++;
        }
        Assert.True(valid >= 30); Assert.Equal(16, comparisons); Assert.True(invalid >= 20);
    }

    [Fact]
    public void RunnerPreparesEveryLeafIncludingUnselectedNestedAndTimedBranches()
    {
        var good = Assertion(Json("{\"type\":\"bool\",\"value\":true}"));
        var bad = Assertion(Json("{\"type\":\"string\",\"value\":\"[\"}")); bad["operator"] = "matches";
        var tree = new JsonObject { ["kind"] = "any", ["conditions"] = new JsonArray(good, new JsonObject { ["kind"] = "time", ["withinMilliseconds"] = 10, ["condition"] = bad }) };
        Assert.Equal("RegexInvalid", Assert.Throws<AssertionConfigurationException>(() => AssertionPreparation.Prepare(tree, new(10, 100))).Code);
        bad["expected"]!["value"] = "^ok$";
        var prepared = AssertionPreparation.Prepare(tree, new(10, 100));
        Assert.Equal(new[] { "$/conditions/0", "$/conditions/1/condition" }, prepared.Select(leaf => leaf.ConditionPath));
        Assert.Equal(EvaluationResult.Known(true), prepared[1].Comparison.EvaluateJson("{\"type\":\"string\",\"value\":\"ok\"}")); // trusted Runner-side regex
    }
}
