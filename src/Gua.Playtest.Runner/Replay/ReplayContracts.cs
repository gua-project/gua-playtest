using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Runner.Replay;

/// <summary>All three trees share this new synchronized capture. No cached pre-arrival checkpoint evidence.</summary>
public sealed record ReplayObservation(RunObservation Run, ConditionObservationUnit Checkpoint);
public interface IReplayObservationFeed : IRunObservationFeed
{
    ValueTask<ReplayObservation> CaptureAsync(PreparedCondition checkpoint, CancellationToken cancellationToken);
}
/// <summary>Initial Plan condition is read in the exact certified initial Running unit, after Setup.
/// The trusted preparation adapter receives ResolvedReplay.Initial's requests before synchronization.</summary>
public sealed record ReplayPreparedHost(PreparedHost Host, ConditionObservationUnit? Initial);
public sealed record ReplayProgress(int TotalSteps, int DispatchedSteps, int CompletedSteps,
    int CompletedCheckpoints, bool PlanCompleted, int? OmittedFromStep, bool GoalVerified);
/// <summary>Only ReplayDriver can issue completion evidence. Callers cannot construct or
/// use a record 'with' expression to replace the progress or retained identities.</summary>
public sealed class ReplayOutcome
{
    public RunOutcome Run { get; }
    public ReplayProgress Progress { get; }
    public string PlanSha256 { get; }
    public string ScenarioSha256 { get; }
    public string RecordingSha256 { get; }
    public CompletionPolicy Completion { get; }
    public int TotalCheckpoints { get; }
    internal string? TrialRunId { get; }
    internal ReplayOutcome(RunOutcome run, ReplayProgress progress, ResolvedReplay replay, string? trialRunId)
    {
        Run = run with { Events = Array.AsReadOnly(run.Events.ToArray()),
            PostProcessing = Array.AsReadOnly(run.PostProcessing.ToArray()),
            Exceptions = Array.AsReadOnly(run.Exceptions.ToArray()) };
        Progress = progress; PlanSha256 = replay.PlanSha256; ScenarioSha256 = replay.ScenarioSha256;
        RecordingSha256 = replay.RecordingSha256; Completion = replay.Completion;
        TotalCheckpoints = replay.Checkpoints.Count;
        TrialRunId = trialRunId;
    }
}
