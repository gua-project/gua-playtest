using System.Text.Json.Nodes;

namespace Gua.Playtest.Core.Contracts;

// Gua payloads remain their public JSON representation: no native Value construction.
public abstract record ContractDocument(string Kind, int SchemaVersion);
public sealed record GoalDefinition(string Objective, JsonObject? Success, JsonObject? Failure);
public sealed record ScenarioConstraints(long MaxDurationMilliseconds, long MaxActions, string[]? AllowedActionIds);
public sealed record ScenarioDocument(string ScenarioId, string DefinitionVersion, string Name,
    GoalDefinition Goal, ScenarioConstraints Constraints, JsonObject? Setup) : ContractDocument("scenario", 1);
public sealed record FixedFileReference(string Path, string Sha256);
public sealed record SecretReference(string SecretKey);
public sealed record ConnectionDefinition(string Endpoint, SecretReference? Credential);
public sealed record PermissionDefinition(string[] ActionIds, bool RawInput, JsonObject[] Reads);
public sealed record TraceDefinition(string CaptureMode, string SavePolicy);
public sealed record ResourceLimits(long MaxDurationMilliseconds, long MaxActions, long MaxDecisions,
    long PrepareTimeoutMilliseconds, long CleanupTimeoutMilliseconds, long PlannerTimeoutMilliseconds,
    long WaitTimeoutMilliseconds, long MaxSegmentMilliseconds, long MaxLatenessMilliseconds,
    long MaxObservationNodes, long MaxObservationBytes, long RegexTimeoutMilliseconds,
    long RegexMaxPatternLength, long StagnationActionLimit, long StagnationRepeatLimit,
    long RecoveryDecisionLimit, long TraceMaxBytes, long TraceMaxEvents, long TraceQueueCapacity,
    long TraceMaxAttachmentBytes, long TraceRecentSteps = 100);
public sealed record EnvironmentDocument(string EnvironmentId, string BuildId, ConnectionDefinition Connection,
    string Profile, ResourceLimits Limits, PermissionDefinition Permissions, TraceDefinition Trace,
    JsonObject? Fixture) : ContractDocument("environment", 1);
public sealed record CheckpointDefinition(long BeforeStep, JsonObject Condition, long TimeoutMilliseconds);
public sealed record ReplayPlanDocument(string PlanId, FixedFileReference Scenario, FixedFileReference Recording,
    string Timing, string Completion, CheckpointDefinition[] Checkpoints, JsonObject? Initial) : ContractDocument("replayPlan", 1);
public sealed record PlannerInputDocument(string RunId, string DecisionRequestId, string BasedOnObservationId,
    string Objective, ResourceLimits Limits, JsonObject Remaining, JsonObject Observation,
    JsonObject ActionDefinitions, JsonObject[] Feedback) : ContractDocument("plannerInput", 1);
public sealed record PlannerDecisionDocument(string RunId, string DecisionRequestId, string BasedOnObservationId,
    JsonObject Decision) : ContractDocument("plannerDecision", 1);
public sealed record RunIdentity(string ScenarioId, string DefinitionVersion, string ScenarioSha256,
    string BuildId, string EnvironmentSha256);
public enum ExecutionState { Created, Preparing, Running, Completing, Finished }
public enum ResultStatus { Passed, Failed, TimedOut, Aborted, Invalid, Unverified }
public sealed record RunDocument(string RunId, RunIdentity Identity, string Mode, ExecutionState ExecutionState,
    DateTimeOffset StartedAt, FixedFileReference Scenario, FixedFileReference Environment,
    FixedFileReference? Plan) : ContractDocument("run", 1);
public sealed record PostProcessingResult(bool Complete, string[] Reasons);
public sealed record ResultDocument(string RunId, ResultStatus Status, string Phase, string Origin, string Reason,
    PostProcessingResult PostProcessing, DateTimeOffset FinishedAt) : ContractDocument("result", 1);
public sealed record RegisteredScenario(string ScenarioId, string DefinitionVersion, FixedFileReference File);
public sealed record ScenarioRegistryDocument(string RegistryId, RegisteredScenario[] Scenarios,
    string[] ArtifactRoots) : ContractDocument("scenarioRegistry", 1);
public sealed record AdoptionEvidenceDocument(FixedFileReference Plan, FixedFileReference Scenario,
    FixedFileReference Recording, string TestRunId, RunIdentity Identity, string Completion,
    bool PlanCompleted, bool GoalVerified, bool PostProcessingComplete, bool AiUsed)
    : ContractDocument("adoptionEvidence", 1);
