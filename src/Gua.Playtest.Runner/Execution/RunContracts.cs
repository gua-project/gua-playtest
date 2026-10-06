using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

public enum RunPhase { Preparation, Execution, PostProcessing }
public enum RunOrigin { Contract, Condition, Host, Planner, Budget, Clock, User, Runner }
// Declaration order is the normative, stable tie-break inside a priority class.
public enum RunReason
{
    InvalidContract, ObservationContractViolation, FailureCondition, PreparationTimeout,
    ActionFailed, ActionUnconfirmed, PlannerTimeout, ExecutionError, Cancelled, MaxDuration,
    GoalImpossible, ActionsExhausted, DecisionsExhausted, RecoveryExhausted,
    WaitExpired, SuccessUnconfirmed, ExplorationFinished, GoalSatisfied
}
public enum CompletionPolicy { OnGoal, AfterPlan }
public sealed record RunEvent(RunReason Reason, RunPhase Phase, RunOrigin Origin);
public sealed record PrimaryResult(ResultStatus Status, RunEvent Cause)
{
    public string Message => Cause.Reason switch
    {
        RunReason.GoalSatisfied => "Machine goal verified.",
        RunReason.ExplorationFinished => "Exploration completed without a machine success condition.",
        RunReason.Cancelled => "Run cancelled before primary result confirmation.",
        RunReason.MaxDuration => "Running real-time deadline reached.",
        _ => "Run terminated; inspect the structured reason."
    };
    public int ExitCode => Cause.Reason == RunReason.ExecutionError && Cause.Origin == RunOrigin.Runner ? 10 : Status switch
    {
        ResultStatus.Passed => 0, ResultStatus.Failed => 1, ResultStatus.Invalid => 2,
        ResultStatus.TimedOut => 3, ResultStatus.Aborted => 4, ResultStatus.Unverified => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(Status))
    };
}
public enum PostProcessingReason
{
    DiagnosticsFailed, ArtifactFailed, InputReleaseUnconfirmed, ResourceReleaseUnconfirmed,
    Cancelled, CleanupTimeout
}
public sealed record ExceptionEvidence(string Type, string? StackTrace);
public sealed record PostProcessingIssue(PostProcessingReason Reason, ExceptionEvidence? Exception = null);
public sealed record RunOutcome(PrimaryResult Primary, IReadOnlyList<RunEvent> Events,
    IReadOnlyList<PostProcessingIssue> PostProcessing, IReadOnlyList<ExceptionEvidence> Exceptions)
{
    public bool PostProcessingComplete => PostProcessing.Count == 0;
    public int ExitCode => Primary.Status == ResultStatus.Passed && !PostProcessingComplete ? 11 : Primary.ExitCode;
}

/// <summary>Explicit effective ceilings. No example durations become product defaults.</summary>
public sealed record RunLimits
{
    public TimeSpan MaxDuration { get; }
    public TimeSpan PreparationTimeout { get; }
    public TimeSpan CleanupTimeout { get; }
    public TimeSpan PlannerTimeout { get; }
    public TimeSpan WaitTimeout { get; }
    public TimeSpan ActionTimeout { get; }
    public long MaxActions { get; }
    public long MaxDecisions { get; }
    public long RecoveryDecisions { get; }
    public int MaxEvidenceItems { get; }
    public RunLimits(TimeSpan maxDuration, TimeSpan preparationTimeout, TimeSpan cleanupTimeout,
        TimeSpan plannerTimeout, TimeSpan waitTimeout, TimeSpan actionTimeout,
        long maxActions, long maxDecisions, long recoveryDecisions, int maxEvidenceItems)
    {
        foreach (var duration in new[] { maxDuration, preparationTimeout, cleanupTimeout, plannerTimeout, waitTimeout, actionTimeout })
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(maxDuration));
        if (maxActions <= 0 || maxDecisions <= 0 || recoveryDecisions <= 0) throw new ArgumentOutOfRangeException(nameof(maxActions));
        if (maxEvidenceItems < 32 || maxEvidenceItems > 100000) throw new ArgumentOutOfRangeException(nameof(maxEvidenceItems));
        MaxDuration = maxDuration; PreparationTimeout = preparationTimeout; CleanupTimeout = cleanupTimeout;
        PlannerTimeout = plannerTimeout; WaitTimeout = waitTimeout; ActionTimeout = actionTimeout;
        MaxActions = maxActions; MaxDecisions = maxDecisions; RecoveryDecisions = recoveryDecisions;
        MaxEvidenceItems = maxEvidenceItems;
    }
}
