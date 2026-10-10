using System.Text;
using System.Runtime.CompilerServices;
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
    private sealed class RunPlannerState
    {
        public readonly string RequestNamespace = Guid.NewGuid().ToString("N");
        public long Sequence;
        public PlannerRequest? Active;
        public bool UnsafeToContinue, RecoveryRetry;
        public readonly Queue<PlannerDecisionReference> Feedback = new();
    }
    private static readonly ConditionalWeakTable<RunSession, RunPlannerState> States = new();
    private readonly RunPlannerState shared;

    public PlannerGate(RunSession run, IClock realClock, IPlannerAuthority authority,
        string runId, string publicObjective, ResourceLimits effectiveLimits)
    {
        this.run = run; clock = run.AuthoritativeRealClock; this.authority = authority; this.runId = runId;
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
        shared = States.GetValue(run, static _ => new());
    }

    public PlannerRequest? Begin(ProjectedPlannerState state, bool recovering = false)
    {
        if (shared.UnsafeToContinue || shared.Active is { Closed: false } || run.State != ExecutionState.Running) return null;
        try { if (!authority.InputsNeutral || authority.RequiresResynchronization) return null; }
        catch (Exception exception) { FailHost(exception); return null; }
        var basis = Copy(state);
        // Public observation reads must be a subset of the independently approved projection scope.
        if (basis.Observation["reads"] is not JsonArray reads || reads.Any(x => x?["read"] is not JsonObject read ||
            !basis.PublicReads.Any(allowed => JsonNode.DeepEquals(allowed, read))))
            throw new ArgumentException("ObservationOutsidePublicScope");
        var requestId = "decision-" + shared.RequestNamespace + "-" + checked(++shared.Sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var budget = run.Budget.Snapshot;
        recovering |= shared.RecoveryRetry;
        var decisionsRemaining = Math.Max(0, run.Limits.MaxDecisions - budget.Decisions - 1);
        if (recovering) decisionsRemaining = Math.Min(decisionsRemaining,
            Math.Max(0, run.Limits.RecoveryDecisions - budget.RecoveryDecisions - 1));
        var remaining = new JsonObject
        {
            ["actions"] = Math.Max(0, run.Limits.MaxActions - budget.Actions - budget.ReservedActions),
            ["decisions"] = decisionsRemaining,
            ["durationMilliseconds"] = Math.Max(0, (long)(run.RunningOrigin!.Value + run.Limits.MaxDuration - clock.Elapsed).TotalMilliseconds)
        };
        var input = new PlannerInputDocument(runId, requestId, basis.ObservationId, objective, limits,
            remaining, basis.Observation, basis.ActionDefinitions, shared.Feedback.Select(x => new JsonObject
            { ["code"] = x.Code.ToString(), ["relatedDecisionRequestId"] = x.DecisionRequestId }).ToArray());
        if (!PlannerExchange.Validate(Encoding.UTF8.GetBytes(ContractJson.Serialize(input)), "plannerInput").IsValid)
            throw new ArgumentException("ProjectedPlannerInputInvalid");
        var permit = run.RequestPlanner(recovering);
        if (permit is null) return null;
        shared.RecoveryRetry = recovering;
        shared.Active = new(this, permit, input, basis, recovering);
        return shared.Active;
    }

    public PlannerAdoption Adopt(PlannerRequest request, byte[] completedJson, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(request.Owner, this) || !ReferenceEquals(shared.Active, request) || request.Closed ||
            run.State != ExecutionState.Running)
            return new(PlannerFeedbackCode.ResponseClosed);
        if (clock.Elapsed >= request.Deadline) return Reject(request, PlannerFeedbackCode.ResponseClosed);
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
        // Even finish takes an approved finite final observation opportunity; it never establishes success.
        var operation = kind == "wait" && decision["durationMilliseconds"] is { } elapsed
            ? request.Permit.ApproveElapsedWait(TimeSpan.FromMilliseconds(elapsed.GetValue<long>()), run.Limits.WaitTimeout)
            : request.Permit.Approve(count, window);
        if (operation is null) return Reject(request, clock.Elapsed >= request.Deadline
            ? PlannerFeedbackCode.PlannerTimeout : PlannerFeedbackCode.BudgetDenied);
        request.Closed = true;
        var reference = Record(request, PlannerFeedbackCode.Approved);
        return new(PlannerFeedbackCode.Approved, new(this, operation, decision, request.Basis, reference, cancellationToken));
    }

    internal bool Owns(RunSession session, PlannerRequest request) => ReferenceEquals(run, session)
        && ReferenceEquals(request.Owner, this) && ReferenceEquals(shared.Active, request) && !request.Closed;

    // A completed backend response is evidence before the fresh observation join. It grants no
    // action authority; adoption and current checks still happen after machine arbitration.
    public bool ConfirmResponse(PlannerRequest request)
    {
        if (!Owns(run, request) || request.ResponseConfirmed) return false;
        var now = clock.Elapsed;
        if (!request.Permit.ConfirmResponse()) return false;
        request.ResponseConfirmed = true;
        request.ConfirmedDeadline = now + run.Limits.WaitTimeout < run.RunningOrigin!.Value + run.Limits.MaxDuration
            ? now + run.Limits.WaitTimeout : run.RunningOrigin.Value + run.Limits.MaxDuration;
        return true;
    }

    public void Cancel(PlannerRequest request)
    {
        if (ReferenceEquals(request.Owner, this) && ReferenceEquals(shared.Active, request) && !request.Closed)
        { request.Closed = true; request.Permit.CompleteWithoutOperation(); }
    }

    private PlannerAdoption Reject(PlannerRequest request, PlannerFeedbackCode code)
    {
        var expired = run.State == ExecutionState.Running && clock.Elapsed >= request.Deadline;
        if (expired) code = request.ResponseConfirmed ? PlannerFeedbackCode.ResponseClosed : PlannerFeedbackCode.PlannerTimeout;
        Cancel(request); Record(request, code);
        if (expired || code is PlannerFeedbackCode.PlannerTimeout or PlannerFeedbackCode.HostFailure) shared.UnsafeToContinue = true;
        var retry = !shared.UnsafeToContinue && run.State == ExecutionState.Running && !run.ActionsClosing;
        return new(code, RetryAllowed: retry, TerminalEvent: retry ? null : code == PlannerFeedbackCode.HostFailure
            ? run.Primary!.Cause :
            new(expired && request.ResponseConfirmed ? RunReason.WaitExpired :
                code == PlannerFeedbackCode.PlannerTimeout ? RunReason.PlannerTimeout : RunReason.PlannerOutputInvalid,
                RunPhase.Execution, expired && request.ResponseConfirmed ? RunOrigin.Host : RunOrigin.Planner));
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
        catch (Exception exception)
        {
            return FailHost(exception);
        }
    }
    internal PlannerFeedbackCode FailHost(Exception exception)
    {
        run.RecordException(exception); shared.UnsafeToContinue = true;
        run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)]);
        return PlannerFeedbackCode.HostFailure;
    }

    private TimeSpan Window(JsonObject decision) => decision["kind"]!.GetValue<string>() == "wait"
        ? TimeSpan.FromMilliseconds((decision["durationMilliseconds"] ?? decision["timeoutMilliseconds"])!.GetValue<long>())
        : run.Limits.WaitTimeout;

    private PlannerDecisionReference Record(PlannerRequest request, PlannerFeedbackCode code)
        => Record(new(runId, request.DecisionRequestId, request.Basis.ObservationId, code));
    internal PlannerDecisionReference Record(PlannerDecisionReference item)
    {
        shared.Feedback.Enqueue(item);
        while (shared.Feedback.Count > Math.Min(1000, run.Limits.MaxEvidenceItems)) shared.Feedback.Dequeue();
        return item;
    }
    internal void StopAfterUnconfirmedDispatch() => shared.UnsafeToContinue = true;
    internal void EndRecoveryAfterConfirmation() => shared.RecoveryRetry = false;
    internal void ArbitrateCancellation() => run.Evaluate(cancelled: true);
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
    private int nextDispatchIndex;
    private readonly CancellationToken dispatchCancellation;
    private readonly CancellationTokenRegistration cancellationRegistration;
    // 0 = available, 1 = revoked, 2 = dispatch commit in progress.
    private int dispatchState;
    internal ApprovedDecision(PlannerGate gate, RunSession.ApprovedOperation operation, JsonObject decision,
        ProjectedPlannerState basis, PlannerDecisionReference reference, CancellationToken cancellationToken)
    {
        this.gate = gate; this.operation = operation; this.decision = decision; this.basis = basis; Reference = reference;
        dispatchCancellation = cancellationToken;
        // Cancellation revokes only this lease off-thread. Run arbitration remains on the
        // serialized owner, and never interrupts the backend or releases game inputs here.
        // A long-lived caller source must not retain a completed Run through this callback.
        cancellationRegistration = cancellationToken.Register(static state =>
        {
            if (((WeakReference<ApprovedDecision>)state!).TryGetTarget(out var lease))
                Interlocked.Exchange(ref lease.dispatchState, 1);
        }, new WeakReference<ApprovedDecision>(this));
    }
    public PlannerDecisionReference Reference { get; }
    public TimeSpan Deadline => operation.Deadline;
    public TimeSpan ResultDeadline => operation.ResultDeadline;
    public JsonObject CopyDecision() => (JsonObject)decision.DeepClone();
    public IReadOnlyList<DeliveryState> Deliveries => operation.Actions?.Deliveries ?? Array.Empty<DeliveryState>();
    public PlannerFeedbackCode BeginDispatch(int index)
    {
        if (completion.HasValue || index != nextDispatchIndex || index < 0 || index >= Deliveries.Count || Deliveries[index] != DeliveryState.Reserved)
            return PlannerFeedbackCode.ResponseClosed;
        if (CancellationRefused()) return PlannerFeedbackCode.ResponseClosed;
        // Neutrality is required for a new decision, not between inputs of an already approved segment.
        var code = gate.Validate(decision, basis, requireNeutral: false);
        if (code == PlannerFeedbackCode.Approved)
        {
            if (CancellationRefused()) return PlannerFeedbackCode.ResponseClosed;
            // Atomic lease commit linearizes with nonblocking cancellation revocation.
            // No off-thread callback can mutate RunSession or wait for authority checks.
            if (Interlocked.CompareExchange(ref dispatchState, 2, 0) != 0)
            {
                CancellationRefused();
                return PlannerFeedbackCode.ResponseClosed;
            }
            try { operation.BeginDispatch(index); nextDispatchIndex++; return code; }
            catch (InvalidOperationException) { code = PlannerFeedbackCode.ResponseClosed; }
            catch (Exception exception) { code = gate.FailHost(exception); }
            finally { Interlocked.CompareExchange(ref dispatchState, 0, 2); }
        }
        operation.Complete(); cancellationRegistration.Unregister();
        if (Deliveries.Any(x => x is DeliveryState.Sent or DeliveryState.Uncertain) && !operation.ResultConfirmed)
            gate.StopAfterUnconfirmedDispatch();
        gate.Record(Reference with { Code = code }); return code;
    }
    private bool CancellationRefused()
    {
        if (Volatile.Read(ref dispatchState) != 1 && !dispatchCancellation.IsCancellationRequested) return false;
        gate.ArbitrateCancellation(); operation.Complete(); cancellationRegistration.Unregister();
        gate.Record(Reference with { Code = PlannerFeedbackCode.ResponseClosed }); return true;
    }
    public void ConfirmSent(int index) => operation.Actions!.ConfirmSent(index);
    public void RecordUnconfirmedSend(int index) => operation.RecordUnconfirmedSend(index);
    public bool DispatchClosedByGoal => operation.DispatchClosedByGoal;
    public bool ConfirmResult() => Deliveries.All(x => x is DeliveryState.Sent or DeliveryState.Uncertain ||
            x == DeliveryState.NotSent && operation.DispatchClosedByGoal)
        && operation.ConfirmResult();
    public PlannerFeedbackCode Complete()
    {
        if (completion.HasValue) return completion.Value;
        if (Volatile.Read(ref dispatchState) == 1 || dispatchCancellation.IsCancellationRequested) gate.ArbitrateCancellation();
        var deliveries = Deliveries;
        var sent = deliveries.Count(x => x is DeliveryState.Sent or DeliveryState.Uncertain);
        var code = operation.ResultConfirmed && operation.DispatchClosedByGoal ? PlannerFeedbackCode.Confirmed :
            sent == 0 && deliveries.Count != 0 ? PlannerFeedbackCode.NotSent :
            sent != 0 && sent < deliveries.Count ? PlannerFeedbackCode.PartialExecution :
            sent != 0 && !operation.ResultConfirmed ? PlannerFeedbackCode.SentUnconfirmed :
            operation.ResultConfirmed ? PlannerFeedbackCode.Confirmed : PlannerFeedbackCode.NotSent;
        operation.Complete(); completion = code; cancellationRegistration.Unregister();
        if (code is PlannerFeedbackCode.SentUnconfirmed or PlannerFeedbackCode.PartialExecution) gate.StopAfterUnconfirmedDispatch();
        if (code == PlannerFeedbackCode.Confirmed) gate.EndRecoveryAfterConfirmation();
        gate.Record(Reference with { Code = code }); return code;
    }
}
