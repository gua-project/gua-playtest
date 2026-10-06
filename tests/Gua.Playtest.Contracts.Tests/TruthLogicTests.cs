using Gua.Playtest.Core.Assertions;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

public sealed class TruthLogicTests
{
    [Theory]
    [InlineData(TruthValue.False, TruthValue.False, TruthValue.False, TruthValue.False)]
    [InlineData(TruthValue.False, TruthValue.True, TruthValue.False, TruthValue.True)]
    [InlineData(TruthValue.False, TruthValue.Unknown, TruthValue.False, TruthValue.Unknown)]
    [InlineData(TruthValue.True, TruthValue.False, TruthValue.False, TruthValue.True)]
    [InlineData(TruthValue.True, TruthValue.True, TruthValue.True, TruthValue.True)]
    [InlineData(TruthValue.True, TruthValue.Unknown, TruthValue.Unknown, TruthValue.True)]
    [InlineData(TruthValue.Unknown, TruthValue.False, TruthValue.False, TruthValue.Unknown)]
    [InlineData(TruthValue.Unknown, TruthValue.True, TruthValue.Unknown, TruthValue.True)]
    [InlineData(TruthValue.Unknown, TruthValue.Unknown, TruthValue.Unknown, TruthValue.Unknown)]
    public void IndependentCompleteTruthTable(TruthValue a, TruthValue b, TruthValue all, TruthValue any)
    {
        static EvaluationResult Result(TruthValue value) => value == TruthValue.Unknown ? EvaluationResult.Unknown() : EvaluationResult.Known(value == TruthValue.True);
        Assert.Equal(all, TruthLogic.All([Result(a), Result(b)]).Truth);
        Assert.Equal(any, TruthLogic.Any([Result(a), Result(b)]).Truth);
    }

    [Fact]
    public void OrdinaryGroupsHaveNoHistoryAndErrorsCannotHideInAnotherBranch()
    {
        Assert.Equal(TruthValue.False, TruthLogic.All([EvaluationResult.Known(true), EvaluationResult.Known(false)]).Truth);
        Assert.Equal(TruthValue.False, TruthLogic.All([EvaluationResult.Known(false), EvaluationResult.Known(true)]).Truth);
        Assert.Equal(EvaluationError.InvalidConfiguration, TruthLogic.Any([EvaluationResult.Known(true), EvaluationResult.InvalidConfiguration()]).Error);
        Assert.Equal(EvaluationError.InvalidConfiguration, TruthLogic.All([EvaluationResult.Known(false), EvaluationResult.InvalidConfiguration()]).Error);
        var prepared = PreparedAssertion.Create(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"assertion\",\"quantifier\":\"one\",\"read\":{\"target\":{\"source\":\"world\"},\"region\":\"property\",\"name\":\"value\",\"valueType\":{\"type\":\"integer\"}},\"operator\":\"equals\",\"expected\":{\"type\":\"integer\",\"value\":1}}")!.AsObject(), new(10, 100));
        var violation = prepared.Evaluate(System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"string\",\"value\":\"secret\"}")!.AsObject());
        Assert.Equal(EvaluationError.ObservationContractViolation, TruthLogic.Any([EvaluationResult.Known(true), violation]).Error);
        Assert.Equal(EvaluationError.InvalidConfiguration, TruthLogic.Any([violation, EvaluationResult.InvalidConfiguration()]).Error);
        Assert.Equal(EvaluationError.InvalidConfiguration, TruthLogic.All([]).Error);
        Assert.Equal(EvaluationError.InvalidConfiguration, TruthLogic.Any([]).Error);
    }
}
