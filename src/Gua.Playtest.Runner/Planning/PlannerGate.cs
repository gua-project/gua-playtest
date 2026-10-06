using System.Text;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Planning;

/// <summary>Serialized Run owner only. A completed response consumes one request, never grants scoring authority.</summary>
public sealed class PlannerGate
{
    private readonly RunSession run;
    private readonly IClock clock;
    private readonly IPlannerAuthority authority;
    private readonly string runId, objective;
    private readonly ResourceLimits limits;
    private PlannerRequest? active;
    private long sequence;
    private bool unsafeToContinue;
    private readonly Queue<PlannerDecisionReference> feedback = new();

    public PlannerGate(RunSession run, IClock realClock, IPlannerAuthority authority,
        string runId, string publicObjective, ResourceLimits effectiveLimits)
    {
        this.run = run; clock = realClock; this.authority = authority; this.runId = runId;
        objective = publicObjective; limits = effectiveLimits;
        // Do not advertise ceilings greater or different from the execution owner's actual limits.
        if (limits.MaxActions != run.Limits.MaxActions || limits.MaxDecisions != run.Limits.MaxDecisions ||
            limits.MaxDurationMilliseconds != run.Limits.MaxDuration.TotalMilliseconds ||
            limits.PlannerTimeoutMilliseconds != run.Limits.PlannerTimeout.TotalMilliseconds ||
            limits.WaitTimeoutMilliseconds != run.Limits.WaitTimeout.TotalMilliseconds ||
            limits.PrepareTimeoutMilliseconds != run.Limits.PreparationTimeout.TotalMilliseconds ||
            limits.CleanupTimeoutMilliseconds != run.Limits.CleanupTimeout.TotalMilliseconds ||
            limits.RecoveryDecisionLimit != run.Limits.RecoveryDecisions)
            throw new ArgumentException("EffectiveLimitsMismatch");
    }

    public PlannerRequest? Begin(ProjectedPlannerState state, bool recovering = false)
    {
        if (unsafeToContinue || active is { Closed: false } || run.State != ExecutionState.Running ||
            !authority.InputsNeutral || authority.RequiresResynchronization) return null;
        var basis = Copy(state);
        // Public observation reads must be a subset of the independently approved projection scope.
        if (basis.Observation["reads"] is not JsonArray reads || reads.Any(x => x?["read"] is not JsonObject read ||
            !basis.PublicReads.Any(allowed => JsonNode.DeepEquals(allowed, read))))
            throw new ArgumentException("ObservationOutsidePublicScope");
        var requestId = "decision-" + checked(++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var budget = run.Budget.Snapshot;
        var remaining = new JsonObject
        {
            ["actions"] = Math.Max(0, run.Limits.MaxActions - budget.Actions - budget.ReservedActions),
            ["decisions"] = Math.Max(0, run.Limits.MaxDecisions - budget.Decisions - 1),
            ["durationMilliseconds"] = Math.Max(0, (long)(run.RunningOrigin!.Value + run.Limits.MaxDuration - clock.Elapsed).TotalMilliseconds)
        };
        var input = new PlannerInputDocument(runId, requestId, basis.ObservationId, objective, limits,
            remaining, basis.Observation, basis.ActionDefinitions, feedback.Select(x => new JsonObject
            { ["code"] = x.Code.ToString(), ["relatedDecisionRequestId"] = x.DecisionRequestId }).ToArray());
        if (!PlannerExchange.Validate(Encoding.UTF8.GetBytes(ContractJson.Serialize(input)), "plannerInput").IsValid)
            throw new ArgumentException("ProjectedPlannerInputInvalid");
        var permit = run.RequestPlanner(recovering);
        if (permit is null) return null;
        active = new(this, permit, input, basis);
        return active;
    }

    public PlannerAdoption Adopt(PlannerRequest request, byte[] completedJson)
    {
        if (!ReferenceEquals(request.Owner, this) || !ReferenceEquals(active, request) || request.Closed ||
            run.State != ExecutionState.Running || clock.Elapsed >= request.Deadline)
            return new(PlannerFeedbackCode.ResponseClosed);
        // Parsing partial fragments is not supported; every completed invalid proposal is consumed once.
        var parsed = PlannerExchange.Validate(completedJson, "plannerDecision");
        if (!parsed.IsValid) return Reject(request, PlannerFeedbackCode.OutputInvalid);
        var response = (PlannerDecisionDocument)parsed.Document!;
        if (response.RunId != runId || response.DecisionRequestId != request.DecisionRequestId ||
            response.BasedOnObservationId != request.Basis.ObservationId)
            return Reject(request, PlannerFeedbackCode.CorrelationMismatch);
        var decision = (JsonObject)response.Decision.DeepClone();
        var code = Validate(decision, request.Basis);
        if (code != PlannerFeedbackCode.Approved) return Reject(request, code);
        var kind = decision["kind"]!.GetValue<string>();
        var count = kind != "execute" ? 0 : decision["mode"]!.GetValue<string>() == "timed"
            ? decision["segment"]!["inputs"]!.AsArray().Count : 1;
        var window = Window(decision);
        if (window > run.Limits.WaitTimeout || window <= TimeSpan.Zero)
            return Reject(request, PlannerFeedbackCode.BudgetDenied);
        request.Closed = true;
        // Even finish takes an approved finite final observation opportunity; it never establishes success.
        var operation = request.Permit.Approve(count, window);
        if (operation is null) return Reject(request, PlannerFeedbackCode.BudgetDenied);
        var reference = Record(request, PlannerFeedbackCode.Approved);
        return new(PlannerFeedbackCode.Approved, new(this, operation, decision, request.Basis, reference));
    }

    public void Cancel(PlannerRequest request)
    {
        if (ReferenceEquals(active, request) && !request.Closed)
        { request.Closed = true; request.Permit.CompleteWithoutOperation(); }
    }

    private PlannerAdoption Reject(PlannerRequest request, PlannerFeedbackCode code)
    {
        Cancel(request); Record(request, code);
        var retry = !unsafeToContinue && run.State == ExecutionState.Running && !run.ActionsClosing;
        return new(code, RetryAllowed: retry, TerminalEvent: retry ? null :
            new(RunReason.PlannerOutputInvalid, RunPhase.Execution, RunOrigin.Planner));
    }

    internal PlannerFeedbackCode Validate(JsonObject decision, ProjectedPlannerState basis, bool requireNeutral = true)
    {
        try
        {
            if (run.State != ExecutionState.Running) return PlannerFeedbackCode.ResponseClosed;
            if (authority.RequiresResynchronization) return PlannerFeedbackCode.ResynchronizationRequired;
            if (requireNeutral && !authority.InputsNeutral) return PlannerFeedbackCode.InputsNotNeutral;
            if (decision["reads"] is JsonArray reads && reads.Any(x => !basis.PublicReads.Any(y => JsonNode.DeepEquals(x, y))))
                return PlannerFeedbackCode.PermissionDenied;
            if (decision["condition"] is JsonObject condition && !basis.PublicWaitConditions.Any(x => JsonNode.DeepEquals(x, condition)))
                return PlannerFeedbackCode.PermissionDenied;
            // Each trusted check sees its own copy; an adapter cannot rewrite the approved payload through this API.
            if (!authority.CheckPermissions(Clone(decision))) return PlannerFeedbackCode.PermissionDenied;
            if (!authority.CheckDefinitions(Clone(decision))) return PlannerFeedbackCode.DefinitionChanged;
            if (!authority.CheckContext(Clone(decision), Copy(basis))) return PlannerFeedbackCode.ContextChanged;
            if (!authority.CheckTargets(Clone(decision), Copy(basis))) return PlannerFeedbackCode.TargetInvalid;
            if (!authority.CheckClock(Clone(decision))) return PlannerFeedbackCode.ClockUnsupported;
            if (!authority.CheckInputBoundary(Clone(decision))) return PlannerFeedbackCode.InputBoundaryInvalid;
            if (decision["segment"] is JsonObject segment &&
                (segment["durationMilliseconds"]!.GetValue<long>() > limits.MaxSegmentMilliseconds ||
                segment["durationMilliseconds"]!.GetValue<long>() > segment["executionTimeoutMilliseconds"]!.GetValue<long>() ||
                segment["maxLatenessMilliseconds"]!.GetValue<long>() > limits.MaxLatenessMilliseconds ||
                segment["executionTimeoutMilliseconds"]!.GetValue<long>() > run.Limits.ActionTimeout.TotalMilliseconds ||
                segment["cleanupTimeoutMilliseconds"]!.GetValue<long>() > run.Limits.CleanupTimeout.TotalMilliseconds))
                return PlannerFeedbackCode.BudgetDenied;
            return PlannerFeedbackCode.Approved;
        }
        catch { return PlannerFeedbackCode.ContextChanged; }
    }

    private TimeSpan Window(JsonObject decision) => decision["kind"]!.GetValue<string>() == "wait"
        ? TimeSpan.FromMilliseconds((decision["durationMilliseconds"] ?? decision["timeoutMilliseconds"])!.GetValue<long>())
        : run.Limits.WaitTimeout;

    private PlannerDecisionReference Record(PlannerRequest request, PlannerFeedbackCode code)
        => Record(new(runId, request.DecisionRequestId, request.Basis.ObservationId, code));
    internal PlannerDecisionReference Record(PlannerDecisionReference item)
    {
        feedback.Enqueue(item);
        while (feedback.Count > Math.Min(1000, run.Limits.MaxEvidenceItems)) feedback.Dequeue();
        return item;
    }
    internal void StopAfterUnconfirmedDispatch() => unsafeToContinue = true;
    private static JsonObject Clone(JsonObject json) => (JsonObject)json.DeepClone();
    private static ProjectedPlannerState Copy(ProjectedPlannerState state) => state with
    {
        Observation = Clone(state.Observation), ActionDefinitions = Clone(state.ActionDefinitions),
        PublicReads = state.PublicReads.Select(Clone).ToArray(), PublicWaitConditions = state.PublicWaitConditions.Select(Clone).ToArray()
    };
}

/// <summary>Host-held single-use operation. Dispatch rechecks the entire segment before every transport invocation.</summary>
public sealed class ApprovedDecision
{
    private readonly PlannerGate gate;
    private readonly RunSession.ApprovedOperation operation;
    private readonly JsonObject decision;
    private readonly ProjectedPlannerState basis;
    private PlannerFeedbackCode? completion;
    internal ApprovedDecision(PlannerGate gate, RunSession.ApprovedOperation operation, JsonObject decision,
        ProjectedPlannerState basis, PlannerDecisionReference reference)
    { this.gate = gate; this.operation = operation; this.decision = decision; this.basis = basis; Reference = reference; }
    public PlannerDecisionReference Reference { get; }
    public TimeSpan Deadline => operation.Deadline;
    public TimeSpan ResultDeadline => operation.ResultDeadline;
    public JsonObject CopyDecision() => (JsonObject)decision.DeepClone();
    public IReadOnlyList<DeliveryState> Deliveries => operation.Actions?.Deliveries ?? Array.Empty<DeliveryState>();
    public PlannerFeedbackCode BeginDispatch(int index)
    {
        if (completion.HasValue || index < 0 || index >= Deliveries.Count || Deliveries[index] != DeliveryState.Reserved)
            return PlannerFeedbackCode.ResponseClosed;
        // Neutrality is required for a new decision, not between inputs of an already approved segment.
        var code = gate.Validate(decision, basis, requireNeutral: false);
        if (code == PlannerFeedbackCode.Approved)
        {
            try { operation.BeginDispatch(index); return code; }
            catch (InvalidOperationException) { code = PlannerFeedbackCode.ResponseClosed; }
        }
        operation.Complete();
        if (Deliveries.Any(x => x is DeliveryState.Sent or DeliveryState.Uncertain) && !operation.ResultConfirmed)
            gate.StopAfterUnconfirmedDispatch();
        gate.Record(Reference with { Code = code }); return code;
    }
    public void ConfirmSent(int index) => operation.Actions!.ConfirmSent(index);
    public bool ConfirmResult() => operation.ConfirmResult();
    public PlannerFeedbackCode Complete()
    {
        if (completion.HasValue) return completion.Value;
        var deliveries = Deliveries;
        var sent = deliveries.Count(x => x is DeliveryState.Sent or DeliveryState.Uncertain);
        var code = sent == 0 && deliveries.Count != 0 ? PlannerFeedbackCode.NotSent :
            sent != 0 && sent < deliveries.Count ? PlannerFeedbackCode.PartialExecution :
            sent != 0 && !operation.ResultConfirmed ? PlannerFeedbackCode.SentUnconfirmed :
            operation.ResultConfirmed ? PlannerFeedbackCode.Confirmed : PlannerFeedbackCode.NotSent;
        operation.Complete(); completion = code;
        if (code is PlannerFeedbackCode.SentUnconfirmed or PlannerFeedbackCode.PartialExecution) gate.StopAfterUnconfirmedDispatch();
        gate.Record(Reference with { Code = code }); return code;
    }
}
