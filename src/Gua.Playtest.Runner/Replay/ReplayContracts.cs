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
public sealed record ReplayOutcome(RunOutcome Run, ReplayProgress Progress);
