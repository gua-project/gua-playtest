using System.Text;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class PlannerGateTests
{
    private sealed class Clock : IClock
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        private TimeSpan elapsed;
        public bool ThrowReads;
        public TimeSpan Elapsed { get => ThrowReads ? throw new IOException("PRIVATE_CLOCK_FAILURE") : elapsed; set => elapsed = value; }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            timers.Add((Elapsed + duration, completion)); return new(completion.Task);
        }
        public void At(TimeSpan time)
        {
            Elapsed = time;
            foreach (var timer in timers.Where(x => x.Due <= time).ToArray()) timer.Completion.TrySetResult();
        }
    }
    private sealed class Authority : IPlannerAuthority
    {
        public bool InputsNeutral { get; set; } = true;
        public bool RequiresResynchronization { get; set; }
        public bool Permission = true, Definition = true, Context = true, Target = true, Timing = true, Boundary = true;
        public bool Mutate;
        public Action? BeforePermissionCheck;
        public bool CheckPermissions(JsonObject proposal) { BeforePermissionCheck?.Invoke(); if (Mutate) proposal["kind"] = "finish"; return Permission; }
        public bool CheckDefinitions(JsonObject proposal) => Definition;
        public bool CheckContext(JsonObject proposal, ProjectedPlannerState basis) => Context;
        public bool CheckTargets(JsonObject proposal, ProjectedPlannerState basis) => Target;
        public bool CheckClock(JsonObject proposal) => Timing;
        public bool CheckInputBoundary(JsonObject proposal) => Boundary;
    }
    private sealed class Setup
    {
        public Clock Clock = new();
        public Authority Authority = new();
        public RunSession Run;
        public PlannerGate Gate;
        public Setup(long actions = 5, long decisions = 5, IClock? gateClock = null)
        {
            var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), actions, decisions, 2, 1024);
            Run = new(limits, Clock, Clock); Run.BeginPreparation(); Run.BeginRunning();
            var projection = new ResourceLimits(5000, actions, decisions, 1000, 1000, 1000, 2000, 1000, 0,
                1000, 100000, 100, 1024, 12, 4, 2, 100000, 1024, 1024, 100000);
            Gate = new(Run, gateClock ?? Clock, Authority, "run-1", "Buy through the exposed control", projection);
        }
        public PlannerRequest Begin() => Gate.Begin(State())!;
    }
    private static JsonObject Read() => JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"public"}}},"region":"standard","field":"visible","valueType":{"type":"bool"}}""")!.AsObject();
    private static ProjectedPlannerState State(string status = "available")
    {
        var observationRead = new JsonObject { ["read"] = Read(), ["status"] = status };
        if (status == "available") observationRead["value"] = new JsonObject { ["type"] = "bool", ["value"] = true };
        var observation = new JsonObject
        {
            ["observationId"] = "observation-1", ["observedAt"] = "2026-10-06T13:00:00Z", ["profile"] = "Player",
            ["sources"] = new JsonArray(new JsonObject { ["source"] = "ui", ["sourceId"] = "source-1", ["sessionEpoch"] = 1, ["revision"] = 1, ["frameSequence"] = 1 }),
            ["complete"] = status is "available" or "unavailable", ["reads"] = new JsonArray(observationRead)
        };
        return new("observation-1", observation,
            JsonNode.Parse("""{"schemaVersion":1,"sessionEpoch":1,"revision":1,"context":"fixture","actions":[]}""")!.AsObject(), [Read()], []);
    }
    private static JsonObject Single() => JsonNode.Parse("""{"kind":"execute","mode":"single","action":{"kind":"semantic","actionId":"purchase","operation":"press"}}""")!.AsObject();
    private static JsonObject Timed(int count = 2)
    {
        var inputs = new JsonArray();
        for (var i = 0; i < count; i++) inputs.Add(new JsonObject { ["offsetMilliseconds"] = i, ["kind"] = 1, ["operation"] = i == 0 ? 1 : 3, ["target"] = "purchase" });
        return new() { ["kind"] = "execute", ["mode"] = "timed", ["segment"] = new JsonObject
        { ["schemaVersion"] = 1, ["durationMilliseconds"] = 100, ["maxLatenessMilliseconds"] = 0, ["executionTimeoutMilliseconds"] = 1000, ["cleanupTimeoutMilliseconds"] = 1000, ["inputs"] = inputs } };
    }
    private static byte[] Response(PlannerRequest request, JsonObject decision, string runId = "run-1", string observationId = "observation-1")
        => Encoding.UTF8.GetBytes(ContractJson.Serialize(new PlannerDecisionDocument(runId, request.DecisionRequestId, observationId, decision)));

    [Fact]
    public void OneRequestAndDuplicateResponseCannotDispatchTwice()
    {
        var s = new Setup(); var request = s.Begin();
        Assert.Null(s.Gate.Begin(State()));
        var bytes = Response(request, Single());
        var approved = s.Gate.Adopt(request, bytes).Approved!;
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, s.Gate.Adopt(request, bytes).Code);
        Assert.Equal(PlannerFeedbackCode.Approved, approved.BeginDispatch(0));
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, approved.BeginDispatch(0));
        Assert.Equal(1, s.Run.Budget.Snapshot.Actions);
        approved.ConfirmSent(0); Assert.True(approved.ConfirmResult());
        Assert.Equal(PlannerFeedbackCode.Confirmed, approved.Complete());
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("owner")]
    [InlineData("shell")]
    [InlineData("url")]
    [InlineData("file")]
    [InlineData("clock")]
    [InlineData("profile")]
    public void UnknownControlFieldsAreUnsentAndFiniteRetryIsFresh(string field)
    {
        var s = new Setup(decisions: 2); var request = s.Begin(); var proposal = Single();
        proposal["action"]![field] = "PRIVATE_MARKER";
        Assert.Equal(PlannerFeedbackCode.OutputInvalid, s.Gate.Adopt(request, Response(request, proposal)).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
        var retry = s.Begin(); Assert.NotNull(retry); Assert.NotEqual(request.DecisionRequestId, retry.DecisionRequestId);
        var input = retry.CopyInput(); Assert.Equal("OutputInvalid", input.Feedback.Single()["code"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE_MARKER", ContractJson.Serialize(input));
        Assert.NotNull(s.Gate.Adopt(retry, Response(retry, Single())).Approved); // final decision can approve
    }

    [Theory]
    [InlineData("run-other", "observation-1")]
    [InlineData("run-1", "observation-other")]
    public void CorrelationMismatchConsumesTheRequest(string runId, string observationId)
    {
        var s = new Setup(); var request = s.Begin();
        Assert.Equal(PlannerFeedbackCode.CorrelationMismatch, s.Gate.Adopt(request, Response(request, Single(), runId, observationId)).Code);
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, s.Gate.Adopt(request, Response(request, Single())).Code);
    }

    [Theory]
    [InlineData("{\"kind\":")]
    [InlineData("{\"kind\":\"plannerDecision\",\"kind\":\"plannerDecision\"}")]
    [InlineData("[]")]
    public void PartialMalformedAndDuplicateKeysDoNotExecute(string json)
    {
        var s = new Setup(); var request = s.Begin();
        Assert.Equal(PlannerFeedbackCode.OutputInvalid, s.Gate.Adopt(request, Encoding.UTF8.GetBytes(json)).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Theory]
    [InlineData(0, PlannerFeedbackCode.PermissionDenied)]
    [InlineData(1, PlannerFeedbackCode.DefinitionChanged)]
    [InlineData(2, PlannerFeedbackCode.ContextChanged)]
    [InlineData(3, PlannerFeedbackCode.TargetInvalid)]
    [InlineData(4, PlannerFeedbackCode.ClockUnsupported)]
    [InlineData(5, PlannerFeedbackCode.InputBoundaryInvalid)]
    public void CurrentChecksAreIndependentAndRepeatedAtDispatch(int check, PlannerFeedbackCode expected)
    {
        var s = new Setup(); var request = s.Begin();
        var approved = s.Gate.Adopt(request, Response(request, Timed())).Approved!;
        switch (check) { case 0: s.Authority.Permission = false; break; case 1: s.Authority.Definition = false; break;
            case 2: s.Authority.Context = false; break; case 3: s.Authority.Target = false; break; case 4: s.Authority.Timing = false; break; case 5: s.Authority.Boundary = false; break; }
        Assert.Equal(expected, approved.BeginDispatch(0));
        Assert.All(approved.Deliveries, x => Assert.Equal(DeliveryState.NotSent, x));
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void EntireSegmentReservesAtomicallyAndRechecksAfterPartialExecution()
    {
        var tooSmall = new Setup(actions: 1); var r = tooSmall.Begin();
        Assert.Equal(PlannerFeedbackCode.BudgetDenied, tooSmall.Gate.Adopt(r, Response(r, Timed())).Code);
        Assert.Equal(0, tooSmall.Run.Budget.Snapshot.ReservedActions);
        var s = new Setup(); var request = s.Begin(); var approved = s.Gate.Adopt(request, Response(request, Timed())).Approved!;
        Assert.Equal(2, s.Run.Budget.Snapshot.ReservedActions);
        Assert.Equal(PlannerFeedbackCode.Approved, approved.BeginDispatch(0)); approved.ConfirmSent(0);
        s.Authority.Target = false;
        Assert.Equal(PlannerFeedbackCode.TargetInvalid, approved.BeginDispatch(1));
        Assert.Equal(PlannerFeedbackCode.PartialExecution, approved.Complete());
        Assert.Equal(RunReason.ActionUnconfirmed, s.Run.Evaluate()!.Cause.Reason);
        Assert.Null(s.Gate.Begin(State()));
    }

    [Fact]
    public void MissingResultStopsWithoutAutomaticPurchaseRetry()
    {
        var s = new Setup(); var request = s.Begin(); var approved = s.Gate.Adopt(request, Response(request, Single())).Approved!;
        approved.BeginDispatch(0);
        Assert.Equal(PlannerFeedbackCode.SentUnconfirmed, approved.Complete());
        Assert.Null(s.Gate.Begin(State())); // even before pending host evidence is arbitrated
        Assert.Equal(RunOrigin.Host, s.Run.Evaluate()!.Cause.Origin);
        Assert.Null(s.Gate.Begin(State()));
        Assert.Equal(1, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void SegmentOwnedInputDoesNotPreventItsApprovedRelease()
    {
        var s = new Setup(); var request = s.Begin(); var approved = s.Gate.Adopt(request, Response(request, Timed())).Approved!;
        approved.BeginDispatch(0); s.Authority.InputsNeutral = false;
        Assert.Equal(PlannerFeedbackCode.Approved, approved.BeginDispatch(1));
        Assert.Null(s.Gate.Begin(State()));
    }

    [Fact]
    public void MutableProjectionAndAdapterCannotRewriteApprovedData()
    {
        var s = new Setup(); var state = State(); var request = s.Gate.Begin(state)!;
        state.Observation["profile"] = "Debug";
        request.CopyInput().Observation["profile"] = "Testing";
        Assert.Equal("Player", request.CopyInput().Observation["profile"]!.GetValue<string>());
        s.Authority.Mutate = true;
        var approved = s.Gate.Adopt(request, Response(request, Single())).Approved!;
        approved.CopyDecision()["kind"] = "finish";
        Assert.Equal("execute", approved.CopyDecision()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void FinishAndWaitDoNotVerifyGoal()
    {
        foreach (var decision in new JsonObject[] { new() { ["kind"] = "finish", ["report"] = "goalClaimed" }, new() { ["kind"] = "wait", ["durationMilliseconds"] = 100 } })
        {
            var s = new Setup(); var request = s.Begin(); var approved = s.Gate.Adopt(request, Response(request, decision)).Approved!;
            Assert.Empty(approved.Deliveries); Assert.False(s.Run.GoalVerified);
            approved.ConfirmResult(); approved.Complete();
            Assert.Equal(ResultStatus.Unverified, s.Run.Evaluate(executionComplete: true)!.Status);
        }
    }

    [Fact]
    public void NonPublicConditionAndReadAreDeniedWithoutRevealingExistence()
    {
        var s = new Setup(); var r = s.Begin();
        var read = Read(); read["target"]!["selector"]!["id"]!["value"] = "private";
        Assert.Equal(PlannerFeedbackCode.PermissionDenied, s.Gate.Adopt(r, Response(r, new() { ["kind"] = "observe", ["reads"] = new JsonArray(read) })).Code);
        r = s.Begin();
        var condition = new JsonObject { ["kind"] = "assertion", ["read"] = Read(), ["quantifier"] = "one", ["operator"] = "equals", ["expected"] = new JsonObject { ["type"] = "bool", ["value"] = true } };
        Assert.Equal(PlannerFeedbackCode.PermissionDenied, s.Gate.Adopt(r, Response(r, new() { ["kind"] = "wait", ["condition"] = condition, ["timeoutMilliseconds"] = 100 })).Code);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("omitted")]
    [InlineData("gap")]
    public void ProjectionPreservesCompletenessAndNoInventedValue(string status)
    {
        var s = new Setup(); var request = s.Gate.Begin(State(status))!;
        var observation = request.CopyInput().Observation;
        Assert.Equal(status, observation["reads"]![0]!["status"]!.GetValue<string>());
        Assert.Null(observation["reads"]![0]!["value"]);
        Assert.Equal(status == "unavailable", observation["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void LostBaselineRequiresHostResynchronizationAndNeutralInputs()
    {
        var s = new Setup(); s.Authority.RequiresResynchronization = true;
        Assert.Null(s.Gate.Begin(State("gap"))); Assert.Equal(0, s.Run.Budget.Snapshot.Decisions);
        s.Authority.RequiresResynchronization = false; s.Authority.InputsNeutral = false;
        Assert.Null(s.Gate.Begin(State()));
        s.Authority.InputsNeutral = true; Assert.NotNull(s.Begin());
    }

    [Fact]
    public void CancelledAndLateResponsesHaveNoAuthority()
    {
        var s = new Setup(); var request = s.Begin(); s.Gate.Cancel(request);
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, s.Gate.Adopt(request, Response(request, Single())).Code);
        request = s.Begin(); s.Clock.Elapsed = request.Deadline;
        Assert.Equal(PlannerFeedbackCode.PlannerTimeout, s.Gate.Adopt(request, Response(request, Single())).Code);
        Assert.Equal(RunReason.PlannerTimeout, s.Run.Evaluate()!.Cause.Reason);
    }

    private sealed class Feed : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => new(new RunObservation(TimeSpan.Zero, new([]), new([])));
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
    }
    private sealed class BlockedPlanner(List<string> order) : IPlanner<PlannerInputDocument, PlannerReply>
    {
        public TaskCompletionSource<PlannerReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
        { token.Register(() => order.Add("cancel-planner")); return new(Completion.Task); }
    }
    [Fact]
    public async Task UnresponsivePlannerClosesAuthorityReleasesThenCancelsAndIgnoresLateReply()
    {
        var s = new Setup(); var request = s.Begin(); var order = new List<string>(); var planner = new BlockedPlanner(order);
        var work = PlannerTurn.AwaitAsync(s.Gate, request, s.Run, s.Clock, s.Clock, new Feed(), planner,
            _ => { Assert.Equal(ExecutionState.Completing, s.Run.State); order.Add("release-inputs"); return new ValueTask<bool>(true); }).AsTask();
        s.Clock.At(request.Deadline);
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Interrupted); Assert.True(result.OwnedInputsReleased);
        Assert.Equal(new[] { "release-inputs", "cancel-planner" }, order);
        Assert.Equal(RunReason.PlannerTimeout, s.Run.Primary!.Cause.Reason);
        planner.Completion.SetResult(new(PlannerReplyStatus.Completed, Response(request, Single())));
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, s.Gate.Adopt(request, Response(request, Single())).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void ProjectionCannotIncludeUnapprovedReadOrFabricatedGapValue()
    {
        var s = new Setup(); var state = State();
        Assert.Throws<ArgumentException>(() => s.Gate.Begin(state with { PublicReads = [] }));
        state = State("gap"); state.Observation["reads"]![0]!["value"] = new JsonObject { ["type"] = "bool", ["value"] = true };
        Assert.Throws<ArgumentException>(() => s.Gate.Begin(state));
    }

    [Fact]
    public void RevisionChangeAloneDoesNotRejectRelevantValidPrerequisites()
    {
        var s = new Setup(); var state = State(); var request = s.Gate.Begin(state)!;
        state.ActionDefinitions["revision"] = 2;
        state.Observation["sources"]![0]!["revision"] = 2;
        // The authority's current relevant-context/target/definition checks still pass.
        Assert.Equal(PlannerFeedbackCode.Approved, s.Gate.Adopt(request, Response(request, Single())).Code);
    }

    [Fact]
    public void ApprovedPublicObserveAndExistingWaitHaveFiniteAuthority()
    {
        var s = new Setup(); var r = s.Begin();
        var observed = s.Gate.Adopt(r, Response(r, new() { ["kind"] = "observe", ["reads"] = new JsonArray(Read()) })).Approved!;
        Assert.Empty(observed.Deliveries); Assert.True(observed.ConfirmResult()); observed.Complete();
        var condition = new JsonObject { ["kind"] = "assertion", ["read"] = Read(), ["quantifier"] = "one", ["operator"] = "equals", ["expected"] = new JsonObject { ["type"] = "bool", ["value"] = true } };
        r = s.Gate.Begin(State() with { PublicWaitConditions = [condition] })!;
        var wait = s.Gate.Adopt(r, Response(r, new() { ["kind"] = "wait", ["condition"] = condition, ["timeoutMilliseconds"] = 100 })).Approved!;
        Assert.Equal(TimeSpan.FromMilliseconds(100), wait.Deadline);
        Assert.False(s.Run.GoalVerified);
        Assert.Equal(PlannerFeedbackCode.NotSent, wait.Complete()); // no invented confirmation
    }

    [Theory]
    [InlineData("durationMilliseconds", 1001)]
    [InlineData("maxLatenessMilliseconds", 1)]
    [InlineData("executionTimeoutMilliseconds", 1001)]
    [InlineData("cleanupTimeoutMilliseconds", 1001)]
    public void TimedProposalCannotRaiseEffectiveCeilings(string field, int value)
    {
        var s = new Setup(); var r = s.Begin(); var proposal = Timed(); proposal["segment"]![field] = value;
        Assert.Equal(PlannerFeedbackCode.BudgetDenied, s.Gate.Adopt(r, Response(r, proposal)).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.ReservedActions);
    }

    [Theory]
    [InlineData(6, 10, "")]
    [InlineData(4, 9, "")]
    public void InternalRawOperationsRemainUnsent(int kind, int operation, string target)
    {
        var s = new Setup(); var r = s.Begin(); var proposal = new JsonObject { ["kind"] = "execute", ["mode"] = "single",
            ["action"] = new JsonObject { ["kind"] = "raw", ["input"] = new JsonObject { ["offsetMilliseconds"] = 0, ["kind"] = kind, ["operation"] = operation, ["target"] = target } } };
        Assert.Equal(PlannerFeedbackCode.OutputInvalid, s.Gate.Adopt(r, Response(r, proposal)).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    private sealed class ImmediatePlanner(PlannerReply reply, bool throws = false) : IPlanner<PlannerInputDocument, PlannerReply>
    {
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
            => throws ? throw new IOException("PRIVATE_BACKEND_EXCEPTION") : new(reply);
    }
    [Theory]
    [InlineData(PlannerReplyStatus.UsageLimit, RunReason.PlannerUsageLimit)]
    [InlineData(PlannerReplyStatus.ConnectionFailure, RunReason.PlannerConnectionFailure)]
    [InlineData(PlannerReplyStatus.OutputInvalid, RunReason.PlannerOutputInvalid)]
    public async Task BackendFaultsHaveCanonicalPlannerOrigin(PlannerReplyStatus status, RunReason reason)
    {
        var s = new Setup(); var r = s.Begin();
        var result = await PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(), new ImmediatePlanner(new(status)), _ => new(true));
        Assert.True(result.Interrupted); Assert.True(result.OwnedInputsReleased);
        Assert.Equal(reason, s.Run.Primary!.Cause.Reason); Assert.Equal(RunOrigin.Planner, s.Run.Primary.Cause.Origin);
        Assert.Equal(ResultStatus.Failed, s.Run.Primary.Status);
    }

    [Fact]
    public async Task BackendExceptionDoesNotBecomeScenarioInvalidOrLeakItsMessage()
    {
        var s = new Setup(); var r = s.Begin();
        await PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(), new ImmediatePlanner(new(PlannerReplyStatus.Completed), throws: true), _ => new(true));
        Assert.Equal(RunReason.PlannerConnectionFailure, s.Run.Primary!.Cause.Reason);
        Assert.DoesNotContain("PRIVATE_BACKEND_EXCEPTION", s.Run.Primary.ToString());
        Assert.Empty(s.Run.Exceptions);
    }

    [Fact]
    public async Task ExhaustedInvalidOutputRetriesTerminateWithPlannerCause()
    {
        var s = new Setup(decisions: 1); var r = s.Begin();
        var result = await PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(),
            new ImmediatePlanner(new(PlannerReplyStatus.Completed, Encoding.UTF8.GetBytes("{\"partial\":"))), _ => new(true));
        Assert.False(result.Adoption!.RetryAllowed); Assert.True(result.Interrupted);
        Assert.Equal(PlannerFeedbackCode.OutputInvalid, result.Adoption.Code);
        Assert.Equal(RunReason.PlannerOutputInvalid, s.Run.Primary!.Cause.Reason);
        Assert.Equal(RunOrigin.Planner, s.Run.Primary.Cause.Origin);
        Assert.Equal(ResultStatus.Failed, s.Run.Primary.Status);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void RecoveryRetryCannotDowngradeToGeneralDecisionBudget()
    {
        var s = new Setup(); var r = s.Gate.Begin(State(), recovering: true)!;
        Assert.True(r.Recovering); Assert.Equal(1, s.Run.Budget.Snapshot.RecoveryDecisions);
        var rejection = s.Gate.Adopt(r, Encoding.UTF8.GetBytes("{\"partial\":")); Assert.True(rejection.RetryAllowed);
        r = s.Begin(); Assert.True(r.Recovering); Assert.Equal(2, s.Run.Budget.Snapshot.RecoveryDecisions);
        rejection = s.Gate.Adopt(r, Encoding.UTF8.GetBytes("{\"partial\":"));
        Assert.False(rejection.RetryAllowed); Assert.NotNull(rejection.TerminalEvent);
        Assert.Null(s.Gate.Begin(State())); Assert.Equal(2, s.Run.Budget.Snapshot.Decisions);
    }

    [Fact]
    public void RecoveryModeAlsoSurvivesUnsentDispatchRejection()
    {
        var s = new Setup(); var r = s.Gate.Begin(State(), recovering: true)!;
        var approved = s.Gate.Adopt(r, Response(r, Single())).Approved!;
        s.Authority.Permission = false;
        Assert.Equal(PlannerFeedbackCode.PermissionDenied, approved.BeginDispatch(0));
        Assert.Equal(PlannerFeedbackCode.NotSent, approved.Complete());
        s.Authority.Permission = true; r = s.Begin();
        Assert.True(r.Recovering); Assert.Equal(2, s.Run.Budget.Snapshot.RecoveryDecisions);
        Assert.NotNull(s.Gate.Adopt(r, Response(r, Single())).Approved);
    }

    [Fact]
    public void TimedInputsCannotDispatchReleaseBeforePress()
    {
        var s = new Setup(); var r = s.Begin(); var approved = s.Gate.Adopt(r, Response(r, Timed())).Approved!;
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, approved.BeginDispatch(1));
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
        Assert.Equal(PlannerFeedbackCode.Approved, approved.BeginDispatch(0));
        Assert.Equal(PlannerFeedbackCode.Approved, approved.BeginDispatch(1));
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, approved.BeginDispatch(0));
    }

    [Fact]
    public void GateUsesOwnerClockEvenWhenCallerSuppliesAnotherClock()
    {
        var s = new Setup(gateClock: new Clock { Elapsed = TimeSpan.FromHours(1) });
        var r = s.Begin();
        Assert.Equal(5000, r.CopyInput().Remaining["durationMilliseconds"]!.GetValue<long>());
        s.Clock.At(r.Deadline);
        Assert.Equal(PlannerFeedbackCode.PlannerTimeout, s.Gate.Adopt(r, Response(r, Single())).Code);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void ExpiredConfirmedResponseClosesPermitAndCarriesWaitExpired()
    {
        var s = new Setup(); var r = s.Begin();
        s.Clock.At(TimeSpan.FromMilliseconds(500)); Assert.True(s.Gate.ConfirmResponse(r));
        Assert.False(s.Gate.ConfirmResponse(r)); // confirmation cannot rebase its window
        s.Clock.At(r.Deadline);
        var adoption = s.Gate.Adopt(r, Response(r, Single()));
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, adoption.Code); Assert.False(adoption.RetryAllowed);
        Assert.Equal(new RunEvent(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Host), adoption.TerminalEvent);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Run.NextRealEvaluationAt); // old permit already closed
        Assert.Equal(PlannerFeedbackCode.ResponseClosed, s.Gate.Adopt(r, Response(r, Single())).Code);
        Assert.Null(s.Gate.Begin(State())); Assert.Equal(RunReason.WaitExpired, s.Run.Evaluate()!.Cause.Reason);
    }

    private sealed class CompletedRecordingPlanner(byte[] response, List<string> order) : IPlanner<PlannerInputDocument, PlannerReply>
    {
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
        { token.Register(() => order.Add("cancel-planner")); return new(new PlannerReply(PlannerReplyStatus.Completed, response)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptionClockFaultArbitratesThenAttemptsFiniteReleaseBeforeInterrupt(bool blockedRelease)
    {
        var s = new Setup(); var r = s.Begin(); var order = new List<string>();
        s.Authority.BeforePermissionCheck = () => s.Clock.ThrowReads = true;
        var result = await PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(),
            new CompletedRecordingPlanner(Response(r, Single()), order), _ =>
            {
                Assert.Equal(ExecutionState.Completing, s.Run.State);
                Assert.Equal(new RunEvent(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock), s.Run.Primary!.Cause);
                order.Add("release-inputs");
                return blockedRelease ? new(new TaskCompletionSource<bool>().Task) : new(true);
            }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Interrupted); Assert.Equal(!blockedRelease, result.OwnedInputsReleased);
        Assert.Equal(new[] { "release-inputs", "cancel-planner" }, order);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
        Assert.NotEmpty(s.Run.Exceptions); Assert.All(s.Run.Exceptions, x => Assert.DoesNotContain("PRIVATE_CLOCK_FAILURE", x.ToString()));
    }

    private sealed class JoinedFeed(Clock clock) : IRunObservationFeed
    {
        public int Calls;
        public TaskCompletionSource<RunObservation> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token)
            => ++Calls == 1 ? new(First.Task) : new(new RunObservation(clock.Elapsed, new([]), new([])));
        public ValueTask WaitForChangeAsync(CancellationToken token) => new(Task.Delay(Timeout.Infinite, token));
    }

    [Theory]
    [InlineData(true, 5, RunReason.Cancelled)]
    [InlineData(false, 1, RunReason.PlannerOutputInvalid)]
    public async Task CancellationDuringAdoptionRevokesAuthorityAndReleasesBeforeInterrupt(bool permission, long decisions, RunReason reason)
    {
        var s = new Setup(decisions: decisions); var r = s.Begin(); var order = new List<string>();
        using var caller = new CancellationTokenSource();
        s.Authority.Permission = permission;
        s.Authority.BeforePermissionCheck = caller.Cancel;
        var result = await PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(),
            new CompletedRecordingPlanner(Response(r, Single()), order), _ =>
            {
                Assert.Equal(ExecutionState.Completing, s.Run.State);
                order.Add("release-inputs"); return new(true);
            }, caller.Token);
        Assert.True(result.Interrupted); Assert.True(result.OwnedInputsReleased);
        Assert.Equal(reason, s.Run.Primary!.Cause.Reason);
        Assert.Equal(new[] { "release-inputs", "cancel-planner" }, order);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
        if (permission) Assert.Equal(PlannerFeedbackCode.ResponseClosed, result.Adoption!.Approved!.BeginDispatch(0));
        else { Assert.Null(result.Adoption!.Approved); Assert.False(result.Adoption.RetryAllowed); }
    }

    [Fact]
    public async Task CompletedReplyJoinsOldCaptureThenAdoptsAfterOriginalPlannerDeadline()
    {
        var s = new Setup(); var r = s.Begin(); var originalDeadline = r.Deadline;
        var feed = new JoinedFeed(s.Clock); var planner = new BlockedPlanner([]);
        var work = PlannerTurn.AwaitAsync(s.Gate, r, s.Run, new Clock(), new Clock(), feed, planner, _ => new(true)).AsTask();
        s.Clock.At(TimeSpan.FromMilliseconds(500));
        planner.Completion.SetResult(new(PlannerReplyStatus.Completed, Response(r, Single())));
        for (var i = 0; r.Deadline == originalDeadline && i < 1000; i++) await Task.Delay(1);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), r.Deadline);
        Assert.Equal(1, feed.Calls); // ordinary adapter cannot start a replacement while underlying capture runs
        s.Clock.At(TimeSpan.FromMilliseconds(1100));
        feed.First.SetResult(new(s.Clock.Elapsed, new([]), new([])));
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, feed.Calls); Assert.False(result.Interrupted);
        Assert.Equal(PlannerFeedbackCode.Approved, result.Adoption!.Code);
        Assert.Null(s.Run.Primary); Assert.Equal(PlannerFeedbackCode.Approved, result.Adoption.Approved!.BeginDispatch(0));
    }

    [Fact]
    public async Task TurnRejectsAnotherRunBeforeInvokingPlanner()
    {
        var s = new Setup(); var other = new Setup(); var r = s.Begin();
        var planner = new BlockedPlanner([]);
        await Assert.ThrowsAsync<ArgumentException>(() => PlannerTurn.AwaitAsync(s.Gate, r, other.Run,
            s.Clock, s.Clock, new Feed(), planner, _ => new(true)).AsTask());
        Assert.Equal(0, other.Run.Budget.Snapshot.Decisions);
    }

    private sealed class ThrowingCancellationPlanner : IPlanner<PlannerInputDocument, PlannerReply>
    {
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
        {
            token.Register(() => throw new IOException("PRIVATE_CANCEL_EXCEPTION"));
            return new(new TaskCompletionSource<PlannerReply>().Task);
        }
    }
    [Fact]
    public async Task ThrowingCancellationCallbackCannotReplaceReleaseOrPrimaryEvidence()
    {
        var s = new Setup(); var r = s.Begin();
        var work = PlannerTurn.AwaitAsync(s.Gate, r, s.Run, s.Clock, s.Clock, new Feed(), new ThrowingCancellationPlanner(), _ => new(true)).AsTask();
        s.Clock.At(r.Deadline);
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Interrupted); Assert.True(result.OwnedInputsReleased);
        Assert.Equal(RunReason.PlannerTimeout, s.Run.Primary!.Cause.Reason);
        Assert.Contains(s.Run.Exceptions, x => x.Type == typeof(IOException).FullName); Assert.All(s.Run.Exceptions, x => Assert.DoesNotContain("PRIVATE_CANCEL_EXCEPTION", x.ToString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApprovalExpiringDuringAuthorityChecksClosesPermitAndPreservesTimeout(bool permission)
    {
        var s = new Setup(); var r = s.Begin();
        s.Authority.Permission = permission;
        s.Authority.BeforePermissionCheck = () => s.Clock.Elapsed = r.Deadline;
        var result = s.Gate.Adopt(r, Response(r, Single()));
        Assert.Equal(PlannerFeedbackCode.PlannerTimeout, result.Code); Assert.False(result.RetryAllowed);
        Assert.Equal(RunReason.PlannerTimeout, result.TerminalEvent!.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Run.NextRealEvaluationAt); // expired planner operation closed
        Assert.Null(s.Gate.Begin(State()));
        Assert.Equal(RunReason.PlannerTimeout, s.Run.Evaluate()!.Cause.Reason);
        Assert.Equal(0, s.Run.Budget.Snapshot.Actions);
    }

    [Fact]
    public void RecoveryProjectionIncludesOnlyTheEffectiveRemainingDecisions()
    {
        var s = new Setup(decisions: 5); var r = s.Gate.Begin(State(), recovering: true)!;
        Assert.Equal(1, r.CopyInput().Remaining["decisions"]!.GetValue<long>());
        s.Gate.Adopt(r, Encoding.UTF8.GetBytes("{\"partial\":"));
        r = s.Begin(); Assert.True(r.Recovering);
        Assert.Equal(0, r.CopyInput().Remaining["decisions"]!.GetValue<long>());
        Assert.NotNull(s.Gate.Adopt(r, Response(r, Single())).Approved); // last request still approves work
        var normal = new Setup(); Assert.Equal(4, normal.Begin().CopyInput().Remaining["decisions"]!.GetValue<long>());
    }

    [Fact]
    public void PartialTimedDispatchCannotConfirmOrSuppressTerminalFailure()
    {
        var s = new Setup(); var r = s.Begin(); var approved = s.Gate.Adopt(r, Response(r, Timed())).Approved!;
        Assert.False(approved.ConfirmResult()); approved.BeginDispatch(0); approved.ConfirmSent(0);
        Assert.False(approved.ConfirmResult());
        Assert.Equal(PlannerFeedbackCode.PartialExecution, approved.Complete());
        Assert.False(approved.ConfirmResult()); Assert.Null(s.Gate.Begin(State()));
        Assert.Equal(RunReason.ActionUnconfirmed, s.Run.Evaluate()!.Cause.Reason);
        var full = new Setup(); r = full.Begin(); approved = full.Gate.Adopt(r, Response(r, Timed())).Approved!;
        approved.BeginDispatch(0); approved.BeginDispatch(1);
        Assert.True(approved.ConfirmResult()); Assert.Equal(PlannerFeedbackCode.Confirmed, approved.Complete());
    }
}
