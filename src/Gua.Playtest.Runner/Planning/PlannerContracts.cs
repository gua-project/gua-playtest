using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Planning;

public enum PlannerFeedbackCode
{
    Approved, OutputInvalid, CorrelationMismatch, ResponseClosed, InputsNotNeutral,
    PermissionDenied, DefinitionChanged, ContextChanged, TargetInvalid, ClockUnsupported, InputBoundaryInvalid,
    BudgetDenied, ResynchronizationRequired, NotSent, SentUnconfirmed, PartialExecution,
    Confirmed, PlannerTimeout, PlannerUsageLimit, PlannerConnectionFailure
}

public sealed record PlannerDecisionReference(string RunId, string DecisionRequestId,
    string BasedOnObservationId, PlannerFeedbackCode Code);

/// <summary>Host-projected public data only. Never pass raw Scenario, Environment, expected criteria or host secrets.</summary>
public sealed record ProjectedPlannerState(string ObservationId, JsonObject Observation,
    JsonObject ActionDefinitions, JsonObject[] PublicReads, JsonObject[] PublicWaitConditions);

/// <summary>Trusted host adapter, not a Planner capability. Checks the entire proposal using current
/// permissions, Gua definitions, context/epoch/profile, target identity and actual timing support.
/// It must not change RunSession, dispatch, block or throw. Revision alone is not context invalidation.</summary>
public interface IPlannerAuthority
{
    bool InputsNeutral { get; }
    bool RequiresResynchronization { get; }
    bool CheckPermissions(JsonObject proposal);
    bool CheckDefinitions(JsonObject proposal);
    bool CheckContext(JsonObject proposal, ProjectedPlannerState basis);
    bool CheckTargets(JsonObject proposal, ProjectedPlannerState basis);
    bool CheckClock(JsonObject proposal);
    bool CheckInputBoundary(JsonObject proposal);
}

public sealed record PlannerAdoption(PlannerFeedbackCode Code, ApprovedDecision? Approved = null,
    bool RetryAllowed = false, Execution.RunEvent? TerminalEvent = null);

public sealed class PlannerRequest
{
    internal PlannerRequest(PlannerGate owner, Execution.RunSession.PlannerPermit permit,
        PlannerInputDocument input, ProjectedPlannerState basis, bool recovering)
    { Owner = owner; Permit = permit; Input = input; Basis = basis; Recovering = recovering; }
    internal PlannerGate Owner { get; }
    internal Execution.RunSession.PlannerPermit Permit { get; }
    internal PlannerInputDocument Input { get; }
    internal ProjectedPlannerState Basis { get; }
    internal bool Closed { get; set; }
    public string DecisionRequestId => Input.DecisionRequestId;
    public bool Recovering { get; }
    public TimeSpan Deadline => Permit.Deadline;
    public PlannerInputDocument CopyInput() => Input with
    {
        Remaining = (JsonObject)Input.Remaining.DeepClone(), Observation = (JsonObject)Input.Observation.DeepClone(),
        ActionDefinitions = (JsonObject)Input.ActionDefinitions.DeepClone(),
        Feedback = Input.Feedback.Select(x => (JsonObject)x.DeepClone()).ToArray()
    };
}
