using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Runner.Conditions;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class ConditionTests
{
    private sealed class Clock : IClock
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? RequestedDelay { get; private set; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedDelay = duration;
            Elapsed += duration;
            return ValueTask.CompletedTask;
        }
        public void At(double milliseconds) => Elapsed = TimeSpan.FromMilliseconds(milliseconds);
    }
    private static JsonObject Json(string value) => JsonNode.Parse(value)!.AsObject();
    private static JsonObject Assertion(string quantifier = "one") => Json("""
        {"kind":"assertion","read":{"region":"standard","target":{"source":"ui","selector":{"role":{"value":"button"}}},"field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}
        """).With("quantifier", quantifier);
    private static JsonObject Targets(string op, long? count = null)
    {
        var node = Json("""{"kind":"targets","target":{"source":"world"},"operator":"exists"}""");
        node["operator"] = op;
        if (count is not null) node["count"] = count.Value;
        return node;
    }
    private static JsonObject Group(string kind, params JsonObject[] nodes) => new() { ["kind"] = kind, ["conditions"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()) };
    private static JsonObject Time(JsonObject node, int? within = null, int? duration = null)
    {
        var result = new JsonObject { ["kind"] = "time", ["condition"] = node };
        if (within is not null) result["withinMilliseconds"] = within.Value;
        if (duration is not null) result["forMilliseconds"] = duration.Value;
        return result;
    }
    private static PreparedCondition Prepare(JsonObject node) => PreparedCondition.Create(node, new(10, 1000));
    private static ConditionLeafObservation Observe(string values, bool complete = true, bool continuous = false, string scope = "scope", string identity = "id")
        => new(scope, complete, values.Select((c, i) => c == '?' ? new ConditionTargetObservation(identity + i, Unavailable: EvaluationCode.Missing)
            : c == '!' ? new ConditionTargetObservation(identity + i, "{\"type\":\"bool\",\"value\":1}")
            : new ConditionTargetObservation(identity + i, "{\"type\":\"bool\",\"value\":" + (c == 'T' ? "true" : "false") + "}")), continuous);
    private static ConditionObservationUnit Unit(params (string Path, ConditionLeafObservation Observation)[] leaves)
        => new(leaves.Select(p => KeyValuePair.Create(p.Path, p.Observation)));
    private static ConditionEvaluation Evaluate(ConditionSession session, string path, string values, bool continuous = false, bool complete = true, string identity = "id", string scope = "scope")
        => session.Evaluate(Unit((path, Observe(values, complete, continuous, scope, identity))));

    // Literal external expectation table: complete empty searches, every quantifier, partial searches, Unknown and violations.
    public static TheoryData<string, string, bool, TruthValue, EvaluationError> Quantifiers => new()
    {
        {"one","",true,TruthValue.False,EvaluationError.None}, {"any","",true,TruthValue.False,EvaluationError.None},
        {"all","",true,TruthValue.False,EvaluationError.None}, {"none","",true,TruthValue.True,EvaluationError.None},
        {"one","T",true,TruthValue.True,EvaluationError.None}, {"one","F",true,TruthValue.False,EvaluationError.None},
        {"one","?",true,TruthValue.Unknown,EvaluationError.None}, {"one","TT",true,TruthValue.Unknown,EvaluationError.ObservationContractViolation},
        {"one","TF",false,TruthValue.Unknown,EvaluationError.ObservationContractViolation},
        {"any","TF",true,TruthValue.True,EvaluationError.None}, {"any","F?",true,TruthValue.Unknown,EvaluationError.None},
        {"any","T?",true,TruthValue.True,EvaluationError.None}, {"all","T?",true,TruthValue.Unknown,EvaluationError.None},
        {"all","F?",true,TruthValue.False,EvaluationError.None}, {"all","TT",true,TruthValue.True,EvaluationError.None},
        {"none","F?",true,TruthValue.Unknown,EvaluationError.None}, {"none","T?",true,TruthValue.False,EvaluationError.None},
        {"none","FF",true,TruthValue.True,EvaluationError.None},
        {"one","",false,TruthValue.Unknown,EvaluationError.None}, {"any","",false,TruthValue.Unknown,EvaluationError.None},
        {"all","",false,TruthValue.Unknown,EvaluationError.None}, {"none","",false,TruthValue.Unknown,EvaluationError.None},
        {"one","T",false,TruthValue.Unknown,EvaluationError.None}, {"any","T",false,TruthValue.True,EvaluationError.None},
        {"any","F",false,TruthValue.Unknown,EvaluationError.None}, {"all","F",false,TruthValue.Unknown,EvaluationError.None},
        {"all","T",false,TruthValue.Unknown,EvaluationError.None}, {"none","T",false,TruthValue.Unknown,EvaluationError.None},
        {"none","F",false,TruthValue.Unknown,EvaluationError.None},
        {"any","T!",true,TruthValue.Unknown,EvaluationError.ObservationContractViolation},
        {"all","F!",false,TruthValue.Unknown,EvaluationError.ObservationContractViolation},
        {"none","F!",true,TruthValue.Unknown,EvaluationError.ObservationContractViolation}
    };

    [Theory, MemberData(nameof(Quantifiers))]
    public void SelectorQuantifierTable(string q, string values, bool complete, TruthValue truth, EvaluationError error)
    {
        var result = Evaluate(Prepare(Assertion(q)).Start(new Clock()), "$", values, complete: complete);
        Assert.Equal(truth, result.Evaluation.Truth);
        Assert.Equal(error, result.Evaluation.Error);
        Assert.Equal(ConditionCompletion.Open, result.Completion);
        Assert.Null(result.NextEvaluationAt);
        if (q == "one" && values.Length > 1 && !values.Contains('!')) Assert.Equal(EvaluationCode.TargetAmbiguous, result.Evaluation.Code);
    }

    public static TheoryData<string, int, long?, bool, TruthValue> TargetTable => new()
    {
        {"exists",0,null,true,TruthValue.False}, {"notExists",0,null,true,TruthValue.True},
        {"exists",1,null,false,TruthValue.True}, {"notExists",1,null,false,TruthValue.False},
        {"exists",0,null,false,TruthValue.Unknown}, {"notExists",0,null,false,TruthValue.Unknown},
        {"countEquals",2,2,true,TruthValue.True}, {"countEquals",2,1,true,TruthValue.False},
        {"countNotEquals",2,1,true,TruthValue.True}, {"countNotEquals",2,2,true,TruthValue.False},
        {"countGreaterThan",2,1,true,TruthValue.True}, {"countGreaterThan",2,2,true,TruthValue.False},
        {"countGreaterThanOrEqual",2,2,true,TruthValue.True}, {"countGreaterThanOrEqual",2,3,true,TruthValue.False},
        {"countLessThan",2,3,true,TruthValue.True}, {"countLessThan",2,2,true,TruthValue.False},
        {"countLessThanOrEqual",2,2,true,TruthValue.True}, {"countLessThanOrEqual",2,1,true,TruthValue.False},
        {"countEquals",2,2,false,TruthValue.Unknown}, {"countNotEquals",2,3,false,TruthValue.Unknown},
        {"countGreaterThan",2,1,false,TruthValue.Unknown}, {"countGreaterThanOrEqual",2,1,false,TruthValue.Unknown},
        {"countLessThan",2,3,false,TruthValue.Unknown}, {"countLessThanOrEqual",2,3,false,TruthValue.Unknown},
        {"countEquals",0,0,true,TruthValue.True}
    };
    [Theory, MemberData(nameof(TargetTable))]
    public void TargetCountIsSeparateFromElementCollectionCount(string op, int observed, long? expected, bool complete, TruthValue truth)
        => Assert.Equal(truth, Evaluate(Prepare(Targets(op, expected)).Start(new Clock()), "$", new string('T', observed), complete: complete).Evaluation.Truth);

    [Fact]
    public void OrdinaryAllNeverUnionsHistoricalTruths()
    {
        var session = Prepare(Group("all", Assertion(), Assertion())).Start(new Clock());
        Assert.Equal(TruthValue.False, session.Evaluate(Unit(("$/conditions/0", Observe("T")), ("$/conditions/1", Observe("F")))).Evaluation.Truth);
        Assert.Equal(TruthValue.False, session.Evaluate(Unit(("$/conditions/0", Observe("F")), ("$/conditions/1", Observe("T")))).Evaluation.Truth);
    }

    // These nine pairs are fixed, not calculated from the implementation or a second copy of its reduction.
    [Theory]
    [InlineData("T","T",TruthValue.True,TruthValue.True)]
    [InlineData("T","F",TruthValue.False,TruthValue.True)]
    [InlineData("T","?",TruthValue.Unknown,TruthValue.True)]
    [InlineData("F","T",TruthValue.False,TruthValue.True)]
    [InlineData("F","F",TruthValue.False,TruthValue.False)]
    [InlineData("F","?",TruthValue.False,TruthValue.Unknown)]
    [InlineData("?","T",TruthValue.Unknown,TruthValue.True)]
    [InlineData("?","F",TruthValue.False,TruthValue.Unknown)]
    [InlineData("?","?",TruthValue.Unknown,TruthValue.Unknown)]
    public void FullOrdinaryGroupTable(string left, string right, TruthValue all, TruthValue any)
    {
        foreach (var (kind, expected) in new[] { ("all", all), ("any", any) })
        {
            var session = Prepare(Group(kind, Assertion(), Assertion())).Start(new Clock());
            Assert.Equal(expected, session.Evaluate(Unit(("$/conditions/0", Observe(left)), ("$/conditions/1", Observe(right)))).Evaluation.Truth);
        }
    }

    [Fact]
    public void IndividualWithinRetainsFactsButTimedGroupNeedsSimultaneousTruth()
    {
        var clock = new Clock();
        var independent = Prepare(Group("all", Time(Assertion(), 5000), Time(Assertion(), 5000))).Start(clock);
        var grouped = Prepare(Time(Group("all", Assertion(), Assertion()), 5000)).Start(clock);
        var first = Unit(("$/conditions/0/condition", Observe("T")), ("$/conditions/1/condition", Observe("F")));
        Assert.Equal(TruthValue.False, independent.Evaluate(first).Evaluation.Truth);
        Assert.Equal(TruthValue.False, grouped.Evaluate(Unit(("$/condition/conditions/0", Observe("T")), ("$/condition/conditions/1", Observe("F")))).Evaluation.Truth);
        clock.At(1000);
        var result = independent.Evaluate(Unit(("$/conditions/0/condition", Observe("F")), ("$/conditions/1/condition", Observe("T"))));
        Assert.Equal(TruthValue.True, result.Evaluation.Truth);
        Assert.Equal(ConditionCompletion.Satisfied, result.Completion);
        Assert.Equal(TruthValue.False, grouped.Evaluate(Unit(("$/condition/conditions/0", Observe("F")), ("$/condition/conditions/1", Observe("T")))).Evaluation.Truth);
    }

    [Theory]
    [InlineData(4500,6500)]
    [InlineData(5000,7000)]
    public void WithinIsInclusiveStartDeadlineAndForCanFinishLater(int start, int finish)
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 5000, 2000)).Start(clock);
        clock.At(start);
        var pending = Evaluate(session, "$/condition", "T");
        Assert.Equal(ConditionCompletion.Pending, pending.Completion);
        Assert.Equal(TruthValue.False, pending.Evaluation.Truth);
        Assert.Equal(TimeSpan.FromMilliseconds(finish), pending.NextEvaluationAt);
        clock.At(finish - 1);
        Assert.Equal(ConditionCompletion.Pending, Evaluate(session, "$/condition", "T", true).Completion);
        clock.At(finish);
        Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "T", true).Completion);
        clock.At(finish + 1);
        Assert.Equal(TruthValue.True, Evaluate(session, "$/condition", "F").Evaluation.Truth);
    }

    [Fact]
    public void NoNewStartAfterDeadlineAndUnknownDoesNotExtendIt()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 5000, 2000)).Start(clock);
        clock.At(5000);
        var boundary = Evaluate(session, "$/condition", "?");
        Assert.Equal(ConditionCompletion.Pending, boundary.Completion);
        Assert.Equal(TimeSpan.FromMilliseconds(5000) + TimeSpan.FromTicks(1), boundary.NextEvaluationAt);
        clock.Elapsed += TimeSpan.FromTicks(1);
        var expired = Evaluate(session, "$/condition", "?");
        Assert.Equal(ConditionCompletion.Expired, expired.Completion);
        Assert.Equal(TruthValue.Unknown, expired.Evaluation.Truth);
        Assert.Null(expired.NextEvaluationAt);
        clock.At(7000);
        Assert.Equal(ConditionCompletion.Expired, Evaluate(session, "$/condition", "T", true).Completion);
    }

    [Theory]
    [InlineData("F",true,"id","scope")]
    [InlineData("?",true,"id","scope")]
    [InlineData("T",false,"id","scope")]
    [InlineData("T",true,"replacement","scope")]
    [InlineData("T",true,"id","new-epoch")]
    public void HoldResetAfterDeadlineCannotRestart(string values, bool continuous, string identity, string scope)
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 5000, 2000)).Start(clock);
        clock.At(4500); Evaluate(session, "$/condition", "T");
        clock.At(5500);
        Assert.Equal(ConditionCompletion.Expired, Evaluate(session, "$/condition", values, continuous, identity: identity, scope: scope).Completion);
    }

    [Fact]
    public void FalseBeforeDeadlineRestartsAndUnknownNeverContributesTime()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 5000, 2000)).Start(clock);
        Evaluate(session, "$/condition", "T");
        clock.At(1000); Evaluate(session, "$/condition", "F", true);
        clock.At(2000); Evaluate(session, "$/condition", "?", true);
        clock.At(3000); Evaluate(session, "$/condition", "T", true);
        clock.At(4999); Assert.Equal(ConditionCompletion.Pending, Evaluate(session, "$/condition", "T", true).Completion);
        clock.At(5000); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "T", true).Completion);
    }

    [Fact]
    public async Task TimerMakesProgressWithoutNotificationsAndMissingEvidenceBreaksHold()
    {
        var clock = new Clock(); var prepared = Prepare(Time(Assertion(), duration: 2000));
        var session = prepared.Start(clock); var pending = Evaluate(session, "$/condition", "T");
        await ConditionTimer.WaitAsync(clock, pending, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), clock.RequestedDelay);
        Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "T", true).Completion);
        clock.At(0); var missing = prepared.Start(clock); pending = Evaluate(missing, "$/condition", "T");
        await ConditionTimer.WaitAsync(clock, pending, CancellationToken.None);
        var unknown = missing.Evaluate(Unit());
        Assert.Equal(ConditionCompletion.Pending, unknown.Completion);
        Assert.Equal(TruthValue.Unknown, unknown.Evaluation.Truth);
        clock.At(3000); Evaluate(missing, "$/condition", "T", true);
        clock.At(4000); Assert.Equal(ConditionCompletion.Pending, Evaluate(missing, "$/condition", "T", true).Completion);
        clock.At(5000); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(missing, "$/condition", "T", true).Completion);
    }

    [Fact]
    public void RunningAndWaitArrivalHaveDistinctExplicitOriginsAndSessions()
    {
        var clock = new Clock(); clock.At(10000);
        var prepared = Prepare(Time(Assertion(), 5000, 2000)); var running = prepared.Start(clock);
        Assert.Equal(TimeSpan.FromMilliseconds(10000), running.Origin);
        Evaluate(running, "$/condition", "T");
        clock.At(11000); var wait = prepared.Start(clock); Evaluate(wait, "$/condition", "T");
        clock.At(12000);
        Assert.Equal(ConditionCompletion.Satisfied, Evaluate(running, "$/condition", "T", true).Completion);
        Assert.Equal(ConditionCompletion.Pending, Evaluate(wait, "$/condition", "T", true).Completion);
        clock.At(13000); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(wait, "$/condition", "T", true).Completion);
    }

    [Theory]
    [InlineData("all",ConditionCompletion.Expired,TruthValue.False)]
    [InlineData("any",ConditionCompletion.Open,TruthValue.False)]
    public void ExpiryPropagationDoesNotInventRunOutcome(string kind, ConditionCompletion expected, TruthValue truth)
    {
        var clock = new Clock(); var session = Prepare(Group(kind, Time(Assertion(), 100), Assertion())).Start(clock);
        clock.At(101);
        var result = session.Evaluate(Unit(("$/conditions/0/condition", Observe("?")), ("$/conditions/1", Observe("F"))));
        Assert.Equal(expected, result.Completion);
        Assert.Equal(truth, result.Evaluation.Truth);
        var later = session.Evaluate(Unit(("$/conditions/0/condition", Observe("?")), ("$/conditions/1", Observe("T"))));
        Assert.Equal(kind == "any" ? TruthValue.True : TruthValue.False, later.Evaluation.Truth);
    }

    [Fact]
    public void RequiredFailureMonitorViolationCannotHideBehindSatisfiedGoalOrUnusedBranch()
    {
        var clock = new Clock(); var session = Prepare(Group("any", Time(Assertion(), 100), Time(Assertion(), 100))).Start(clock);
        session.Evaluate(Unit(("$/conditions/0/condition", Observe("T")), ("$/conditions/1/condition", Observe("F"))));
        clock.At(101);
        var good = session.Evaluate(Unit(("$/conditions/0/condition", Observe("?")), ("$/conditions/1/condition", Observe("?"))));
        Assert.Equal(TruthValue.True, good.Evaluation.Truth); Assert.Null(good.NextEvaluationAt);
        var bad = session.Evaluate(Unit(("$/conditions/0/condition", Observe("?")), ("$/conditions/1/condition", Observe("!"))));
        Assert.Equal(EvaluationError.ObservationContractViolation, bad.Evaluation.Error);
        Assert.Equal(TruthValue.Unknown, bad.Evaluation.Truth);
    }

    [Fact]
    public void EveryCompositeAndUnvisitedLeafMustBeValidBeforeEvaluation()
    {
        var bad = Assertion(); bad["operator"] = "matches";
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("any", Assertion(), Time(bad, 10))));
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("any", Assertion(), Time(Assertion()))));
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("any", Assertion(), Targets("countEquals"))));
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("any", Assertion(), Targets("exists", 1))));
        var unknown = Time(Assertion(), 10); unknown["extra"] = true;
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("any", Assertion(), unknown)));
        Assert.Throws<AssertionConfigurationException>(() => Prepare(Group("all")));
        var nullChild = Json("""{"kind":"any","conditions":[null]}""");
        Assert.Throws<AssertionConfigurationException>(() => Prepare(nullChild));
    }

    [Fact]
    public void RawIntegerLexemesAndPreparedDefinitionsArePreserved()
    {
        var node = Assertion(); node["read"]!["field"] = "state.caretPosition"; node["read"]!["valueType"]!["type"] = "integer";
        node["expected"] = Json("""{"type":"integer","value":9007199254740991}""");
        var prepared = Prepare(node); node["expected"]!["value"] = 0;
        var session = prepared.Start(new Clock());
        var result = session.Evaluate(Unit(("$", new("scope", true, new[] { new ConditionTargetObservation("id", "{\"type\":\"integer\",\"value\":9007199254740991.0}") }))));
        Assert.Equal(TruthValue.True, result.Evaluation.Truth);
        var invalid = session.Evaluate(Unit(("$", new("scope", true, new[] { new ConditionTargetObservation("id", "{\"type\":\"integer\",\"value\":9007199254740991.1}") }))));
        Assert.Equal(EvaluationError.ObservationContractViolation, invalid.Evaluation.Error);
        Assert.DoesNotContain("9007199254740991", prepared.Leaves.Single().DefinitionJson);
        Assert.True(JsonNode.DeepEquals(node["read"], Json(prepared.Leaves.Single().DefinitionJson)["read"]));
    }

    [Fact]
    public void DuplicateIdentityAndClockRegressionAreRejected()
    {
        var clock = new Clock(); var session = Prepare(Assertion("any")).Start(clock);
        var duplicate = new ConditionLeafObservation("scope", true, new[] { new ConditionTargetObservation("id", "{}"), new ConditionTargetObservation("id", "{}") });
        Assert.Equal(EvaluationCode.InvalidObservation, session.Evaluate(Unit(("$", duplicate))).Evaluation.Code);
        clock.At(10); Evaluate(session, "$", "T"); clock.At(9);
        Assert.Throws<InvalidOperationException>(() => Evaluate(session, "$", "T"));
    }

    [Fact]
    public void IndependentFullTemporalGroupStateTable()
    {
        var fixture = Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "condition-state-table.json")));
        int checkedCases = 0;
        foreach (var example in fixture["cases"]!.AsArray())
        {
            var left = example!["left"]!.GetValue<string>(); var right = example["right"]!.GetValue<string>();
            foreach (var kind in new[] { "all", "any" })
            {
                var clock = new Clock();
                var session = Prepare(Group(kind, Node(left), Node(right))).Start(clock);
                session.Evaluate(Unit((LeafPath(left, 0), Observe(left == "E" ? "F" : "T")), (LeafPath(right, 1), Observe(right == "E" ? "F" : "T"))));
                clock.At(101);
                var result = session.Evaluate(Unit((LeafPath(left, 0), Observe(left is "E" or "S" ? "F" : "T", continuous: true)),
                    (LeafPath(right, 1), Observe(right is "E" or "S" ? "F" : "T", continuous: true))));
                Assert.Equal(Enum.Parse<TruthValue>(example[kind + "Truth"]!.GetValue<string>()), result.Evaluation.Truth);
                Assert.Equal(Enum.Parse<ConditionCompletion>(example[kind + "State"]!.GetValue<string>()), result.Completion);
                checkedCases++;
            }
        }
        Assert.Equal(32, checkedCases);
        static JsonObject Node(string state) => state == "O" ? Assertion() : state == "P" ? Time(Assertion(), duration: 1000) : Time(Assertion(), 100);
        static string LeafPath(string state, int index) => "$/conditions/" + index + (state == "O" ? "" : "/condition");
    }

    [Theory]
    [InlineData("0.00000000000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000000000001")]
    [InlineData("86400000.00000000000000000000000000001")]
    [InlineData("-1")]
    [InlineData("86400001")]
    public void TimeIntegersMustNotRoundIntoValidConfiguration(string lexeme)
        => Assert.Throws<AssertionConfigurationException>(() => Prepare(Json("{\"kind\":\"time\",\"withinMilliseconds\":" + lexeme + ",\"condition\":" + Assertion().ToJsonString() + "}")));

    [Fact]
    public void ExactExponentDurationAndCountAreAcceptedWithoutChangingInput()
    {
        var node = Json("{\"kind\":\"time\",\"withinMilliseconds\":5e3,\"forMilliseconds\":2e3,\"condition\":" + Assertion().ToJsonString() + "}");
        var original = node.ToJsonString(); var clock = new Clock(); var session = Prepare(node).Start(clock);
        clock.At(4500); var pending = Evaluate(session, "$/condition", "T");
        Assert.Equal(TimeSpan.FromMilliseconds(6500), pending.NextEvaluationAt); Assert.Equal(original, node.ToJsonString());
        Assert.Equal(TruthValue.True, Evaluate(Prepare(Json("""{"kind":"targets","target":{"source":"world"},"operator":"countEquals","count":2e0}""")).Start(new Clock()), "$", "TT").Evaluation.Truth);
    }

    [Theory]
    [InlineData(EvaluationCode.Missing)]
    [InlineData(EvaluationCode.GetterError)]
    [InlineData(EvaluationCode.Stale)]
    [InlineData(EvaluationCode.Gap)]
    [InlineData(EvaluationCode.Truncated)]
    public void UnavailableObservationBreaksForEvenIfEndpointsWouldMatch(EvaluationCode code)
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), duration: 1000)).Start(clock);
        Evaluate(session, "$/condition", "T"); clock.At(500);
        var unavailable = session.Evaluate(Unit(("$/condition", new("scope", false, Array.Empty<ConditionTargetObservation>(), true, code))));
        Assert.Equal(TruthValue.Unknown, unavailable.Evaluation.Truth); Assert.Null(unavailable.NextEvaluationAt);
        clock.At(1000); Evaluate(session, "$/condition", "T", true);
        clock.At(1500); Assert.Equal(ConditionCompletion.Pending, Evaluate(session, "$/condition", "T", true).Completion);
        clock.At(2000); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "T", true).Completion);
    }

    [Fact]
    public void MembershipChangeAndAnyWitnessSwitchResetGroupFor()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion("all"), duration: 1000)).Start(clock);
        Evaluate(session, "$/condition", "T"); clock.At(500); Evaluate(session, "$/condition", "TT", true);
        clock.At(1000); Assert.Equal(ConditionCompletion.Pending, Evaluate(session, "$/condition", "TT", true).Completion);
        clock.At(1500); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "TT", true).Completion);
        clock.At(0); var any = Prepare(Time(Group("any", Assertion(), Assertion()), duration: 1000)).Start(clock);
        any.Evaluate(Unit(("$/condition/conditions/0", Observe("T")), ("$/condition/conditions/1", Observe("F"))));
        clock.At(500); any.Evaluate(Unit(("$/condition/conditions/0", Observe("F", continuous: true)), ("$/condition/conditions/1", Observe("T", continuous: true))));
        clock.At(1000);
        Assert.Equal(ConditionCompletion.Pending, any.Evaluate(Unit(("$/condition/conditions/0", Observe("F", continuous: true)), ("$/condition/conditions/1", Observe("T", continuous: true)))).Completion);
        clock.At(1500);
        Assert.Equal(ConditionCompletion.Satisfied, any.Evaluate(Unit(("$/condition/conditions/0", Observe("F", continuous: true)), ("$/condition/conditions/1", Observe("T", continuous: true)))).Completion);
    }

    [Fact]
    public void QuantifiedAnyCannotBorrowHoldFromDifferentProvingTarget()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion("any"), duration: 1000)).Start(clock);
        Evaluate(session, "$/condition", "TF");
        clock.At(500); Evaluate(session, "$/condition", "FT", true);
        clock.At(1000); Assert.Equal(ConditionCompletion.Pending, Evaluate(session, "$/condition", "FT", true).Completion);
        clock.At(1500); Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "FT", true).Completion);
    }

    [Fact]
    public void NestedTimersChooseEarliestDeadlineAndZeroDurationsLatch()
    {
        var clock = new Clock(); var session = Prepare(Time(Group("all", Time(Assertion(), 1000, 500), Assertion()), 2000, 1000)).Start(clock);
        var unit = Unit(("$/condition/conditions/0/condition", Observe("T", continuous: true)), ("$/condition/conditions/1", Observe("T", continuous: true)));
        Assert.Equal(TimeSpan.FromMilliseconds(500), session.Evaluate(unit).NextEvaluationAt);
        clock.At(500); Assert.Equal(TimeSpan.FromMilliseconds(1500), session.Evaluate(unit).NextEvaluationAt);
        clock.At(1500); Assert.Equal(ConditionCompletion.Satisfied, session.Evaluate(unit).Completion);
        clock.At(0); var zero = Prepare(Time(Assertion(), 0, 0)).Start(clock);
        Assert.Equal(ConditionCompletion.Satisfied, Evaluate(zero, "$/condition", "T").Completion);
    }

    [Fact]
    public void ExistingFixtureExpectationsRemainPinnedToTheirOwners()
    {
        var fixture = Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "temporal-golden.json")));
        var cases = fixture["cases"]!.AsArray().ToDictionary(c => c!["id"]!.GetValue<string>());
        Assert.Equal("False", cases["all-simultaneous"]!["expectedTruth"]!.GetValue<string>());
        Assert.Equal("False", cases["all-empty"]!["expectedTruth"]!.GetValue<string>());
        Assert.Equal("Unknown", cases["three-valued-missing"]!["expectedTruth"]!.GetValue<string>());
        Assert.Equal("Unknown", cases["truncated-search"]!["expectedTruth"]!.GetValue<string>());
        Assert.Equal("Expired", cases["within-expired-unknown"]!["expectedTemporalState"]!.GetValue<string>());
        // #6 owns these Run outcomes; this module deliberately does not reinterpret them as condition expiry.
        Assert.Equal("Failed", cases["same-cycle-failure"]!["expectedStatus"]!.GetValue<string>());
        Assert.Equal("TimedOut", cases["global-deadline"]!["expectedStatus"]!.GetValue<string>());
    }

    [Fact]
    public void EvaluationFeedbackDoesNotExposePrivateValuesOrIdentities()
    {
        var session = Prepare(Time(Assertion(), 100)).Start(new Clock());
        var result = Evaluate(session, "$/condition", "!", identity: "private-owner-secret", scope: "private-profile-secret");
        var feedback = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Equal(EvaluationError.ObservationContractViolation, result.Evaluation.Error);
        Assert.DoesNotContain("private", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("ValueJson", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("DefinitionJson", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimerCancellationAndOverdueTimerDoNotWaitOrFabricateSuccess()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), duration: 1000)).Start(clock);
        var pending = Evaluate(session, "$/condition", "T");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConditionTimer.WaitAsync(clock, pending, new CancellationToken(true)).AsTask());
        Assert.Null(clock.RequestedDelay);
        clock.At(2000); await ConditionTimer.WaitAsync(clock, pending, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, clock.RequestedDelay);
        Assert.Equal(ConditionCompletion.Pending, session.Evaluate(Unit()).Completion);
    }

    [Fact]
    public void AuthoritativeBoundaryTimestampAllowsInitialWithinZeroDespiteDeliveryDelay()
    {
        var clock = new Clock(); clock.At(100);
        var boundary = clock.Elapsed;
        clock.At(101); var prepared = Prepare(Time(Assertion(), 0, 0));
        var success = prepared.Start(clock, boundary);
        clock.At(102); var failure = prepared.Start(clock, boundary);
        var unit = Unit(("$/condition", Observe("T")));
        Assert.Equal(ConditionCompletion.Satisfied, success.EvaluateAt(unit, boundary).Completion);
        Assert.Equal(ConditionCompletion.Satisfied, failure.EvaluateAt(unit, boundary).Completion);
        Assert.Throws<InvalidOperationException>(() => success.EvaluateAt(unit, boundary - TimeSpan.FromTicks(1)));
        Assert.Throws<InvalidOperationException>(() => success.EvaluateAt(unit, clock.Elapsed + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => prepared.Start(clock, clock.Elapsed + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void DeliveredLateObservationGetsNoCreditBeforeFirstObservedTrue()
    {
        var clock = new Clock(); var prepared = Prepare(Time(Assertion(), 5000, 2000)); var session = prepared.Start(clock, TimeSpan.Zero);
        clock.At(10000);
        var trueAt4500 = Unit(("$/condition", Observe("T")));
        var pending = session.EvaluateAt(trueAt4500, TimeSpan.FromMilliseconds(4500));
        Assert.Equal(ConditionCompletion.Pending, pending.Completion);
        Assert.Equal(TimeSpan.FromMilliseconds(6500), pending.NextEvaluationAt);
        Assert.Equal(ConditionCompletion.Pending, session.EvaluateAt(Unit(("$/condition", Observe("T", continuous: true))), TimeSpan.FromMilliseconds(6499)).Completion);
        Assert.Equal(ConditionCompletion.Satisfied, session.EvaluateAt(Unit(("$/condition", Observe("T", continuous: true))), TimeSpan.FromMilliseconds(6500)).Completion);
    }

    [Theory]
    [InlineData(200,ConditionCompletion.Pending)]
    [InlineData(500,ConditionCompletion.Pending)]
    [InlineData(501,ConditionCompletion.Expired)]
    [InlineData(600,ConditionCompletion.Expired)]
    public void ObservationViolationRetainsErrorAndStillAdvancesInclusiveDeadline(int errorAt, ConditionCompletion expected)
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 500, 1000)).Start(clock);
        Evaluate(session, "$/condition", "T"); clock.At(errorAt);
        var violation = Evaluate(session, "$/condition", "!", true);
        Assert.Equal(EvaluationError.ObservationContractViolation, violation.Evaluation.Error);
        Assert.Equal(TruthValue.Unknown, violation.Evaluation.Truth);
        Assert.Equal(expected, violation.Completion);
        Assert.Equal(expected == ConditionCompletion.Pending ? TimeSpan.FromMilliseconds(500) + TimeSpan.FromTicks(1) : (TimeSpan?)null, violation.NextEvaluationAt);
        clock.At(700); var recovered = Evaluate(session, "$/condition", "T", true);
        Assert.Equal(ConditionCompletion.Expired, recovered.Completion);
        Assert.Equal(TruthValue.False, recovered.Evaluation.Truth);
    }

    [Fact]
    public void ViolationsDoNotEraseEstablishedTemporalFactsOrHideExpiredChildState()
    {
        var clock = new Clock(); var session = Prepare(Time(Assertion(), 500)).Start(clock);
        Assert.Equal(ConditionCompletion.Satisfied, Evaluate(session, "$/condition", "T").Completion);
        clock.At(600); var violation = Evaluate(session, "$/condition", "!");
        Assert.Equal(ConditionCompletion.Satisfied, violation.Completion);
        Assert.Equal(EvaluationError.ObservationContractViolation, violation.Evaluation.Error);
        Assert.Null(violation.NextEvaluationAt);
        Assert.Equal(TruthValue.True, Evaluate(session, "$/condition", "?").Evaluation.Truth);
        clock.At(0); var group = Prepare(Group("all", Time(Assertion(), 500, 1000), Assertion())).Start(clock);
        clock.At(600);
        var failed = group.Evaluate(Unit(("$/conditions/0/condition", Observe("!")), ("$/conditions/1", Observe("T"))));
        Assert.Equal(ConditionCompletion.Expired, failed.Completion);
        Assert.Equal(EvaluationError.ObservationContractViolation, failed.Evaluation.Error);
        Assert.Equal(TruthValue.Unknown, failed.Evaluation.Truth);
    }

    [Fact]
    public void ObservationAdapterRequestsContainOnlyReadOrTargetAndNeverComparisonConfiguration()
    {
        var assertion = Json("""
            {"kind":"assertion","quantifier":"any","operator":"matches","read":{"region":"property","target":{"source":"world"},"name":"field","valueType":{"type":"string"}},"expected":{"type":"string","value":"^secret-pattern$"}}
            """);
        var target = Targets("countEquals", 123456);
        var prepared = Prepare(Group("all", assertion, target));
        var readRequest = Json(prepared.Leaves[0].DefinitionJson);
        var targetRequest = Json(prepared.Leaves[1].DefinitionJson);
        Assert.Equal(new[] { "read" }, readRequest.Select(p => p.Key));
        Assert.Equal(new[] { "target" }, targetRequest.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(assertion["read"], readRequest["read"]));
        Assert.True(JsonNode.DeepEquals(target["target"], targetRequest["target"]));
        Assert.DoesNotContain("secret-pattern", prepared.Leaves[0].DefinitionJson);
        Assert.DoesNotContain("123456", prepared.Leaves[1].DefinitionJson);
        assertion["read"]!["name"] = "mutated";
        target["target"]!["source"] = "mutated";
        Assert.Equal("field", Json(prepared.Leaves[0].DefinitionJson)["read"]!["name"]!.GetValue<string>());
        Assert.Equal("world", Json(prepared.Leaves[1].DefinitionJson)["target"]!["source"]!.GetValue<string>());
    }
}

internal static class ConditionTestJson
{
    internal static JsonObject With(this JsonObject node, string name, string value) { node[name] = value; return node; }
}
