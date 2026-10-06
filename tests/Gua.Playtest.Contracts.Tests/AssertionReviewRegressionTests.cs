using System.Diagnostics;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

public sealed class AssertionReviewRegressionTests
{
    private static JsonObject Assertion(JsonObject expected, string op)
    {
        var type = expected.DeepClone().AsObject(); type.Remove("value");
        var read = new JsonObject
        {
            ["target"] = new JsonObject { ["source"] = "world" }, ["region"] = "property", ["name"] = "test", ["valueType"] = type
        };
        return new JsonObject
        {
            ["kind"] = "assertion", ["read"] = read, ["quantifier"] = "one", ["operator"] = op, ["expected"] = expected.DeepClone()
        };
    }

    [Theory]
    [InlineData("{\"type\":\"enum\",\"value\":\"x\"}")]
    [InlineData("{\"type\":\"enum\",\"enumType\":\"\",\"value\":\"x\"}")]
    [InlineData("{\"type\":\"list\",\"elementType\":\"enum\",\"value\":[\"x\"]}")]
    [InlineData("{\"type\":\"set\",\"elementType\":\"enum\",\"value\":[]}")]
    public void MissingEnumMetadataNeverLeaksAnException(string wire)
    {
        var invalid = JsonNode.Parse(wire)!.AsObject();
        var valid = JsonNode.Parse("{\"type\":\"string\",\"value\":\"x\"}")!.AsObject();
        var prepared = PreparedAssertion.Create(Assertion(valid, "equals"), new(10, 100));
        var result = prepared.Evaluate(invalid);
        Assert.Equal(EvaluationError.ObservationContractViolation, result.Error);
        Assert.Equal(TruthValue.Unknown, result.Truth);
        Assert.Equal(EvaluationError.ObservationContractViolation, prepared.EvaluateJson(wire).Error);
        Assert.Equal("EnumTypeInvalid", Assert.Throws<AssertionConfigurationException>(
            () => PreparedAssertion.Create(Assertion(invalid, "equals"), new(10, 100))).Code);
    }

    private static JsonObject Collection(string kind, IEnumerable<int> values)
        => new()
        {
            ["type"] = kind, ["elementType"] = "integer",
            ["value"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        };

    [Fact]
    public void LargeMembershipAndRepeatedSequenceComparisonsStayWithinPredeclaredBudget()
    {
        // These fixed sizes fit the 50000-node decoder and previously required hundreds of millions
        // of comparisons. The 10s acceptance ceiling is declared before execution and applies to CI.
        const int count = 18000;
        var clock = Stopwatch.StartNew();
        var forward = Collection("set", Enumerable.Range(0, count));
        var reverse = Collection("set", Enumerable.Range(0, count).Reverse());
        var equal = PreparedAssertion.Create(Assertion(reverse, "equals"), new(10, 100));
        Assert.Equal(EvaluationResult.Known(true), equal.EvaluateJson(forward.ToJsonString()));
        var unequal = Collection("set", Enumerable.Range(1, count));
        Assert.Equal(EvaluationResult.Known(false), equal.Evaluate(unequal));
        foreach (var kind in new[] { "list", "set" })
        {
            var actual = Collection(kind, Enumerable.Range(0, count));
            var config = Assertion(reverse, "containsAll");
            config["read"]!["valueType"]!["type"] = kind;
            var all = PreparedAssertion.Create(config, new(10, 100));
            Assert.Equal(EvaluationResult.Known(true), all.Evaluate(actual));
            config["operator"] = "containsAny";
            Assert.Equal(EvaluationResult.Known(true), PreparedAssertion.Create(config, new(10, 100)).Evaluate(actual));
        }
        var repeated = Collection("list", Enumerable.Repeat(1, count).Append(3));
        var needle = Collection("list", Enumerable.Repeat(1, count / 2).Append(2));
        var sequence = PreparedAssertion.Create(Assertion(needle, "containsSequence"), new(10, 100));
        Assert.Equal(EvaluationResult.Known(false), sequence.Evaluate(repeated));
        repeated["value"]!.AsArray()[count] = 2;
        Assert.Equal(EvaluationResult.Known(true), sequence.Evaluate(repeated));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Large comparison budget exceeded: {clock.Elapsed}.");
    }
}
