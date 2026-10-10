using System.Text.Json.Nodes;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Runner.Exploration;

/// <summary>Trusted independent public projection plus private reads, all from the indicated capture.
/// The host must retain source continuity and never derive public data from private judgement reads.</summary>
public sealed record ExploreObservation(RunObservation Run, ProjectedPlannerState Public, ProgressObservation Progress);
public interface IExploreObservationFeed
{
    ValueTask<ExploreObservation> CaptureAsync(CancellationToken cancellationToken);
    ValueTask WaitForChangeAsync(CancellationToken cancellationToken);
}
public enum ExploreWorkStatus { Completed, NotSent, Failed, Unconfirmed }
public sealed record ExploreReceipt(ExploreWorkStatus Status, bool InputsNeutral, Exception? OriginalException = null);
public sealed record ExploreSend<T>(T Value, bool Sent);
/// <summary>The owner verified OnGoal and cancelled this still-unsent suffix.
/// Stop sending, settle only the actual prefix, and release owned inputs before returning Completed.</summary>
public sealed class ExploreDispatchClosedByGoalException() : Exception("ExploreDispatchClosedByGoal");

/// <summary>Worker calls are marshalled onto the serialized Run owner. The send callback invokes beforeSend
/// immediately before its actual side effect, exactly once. Callbacks must return promptly and perform no async work.</summary>
public interface IExploreCalls
{
    ValueTask<T> ReadAsync<T>(Func<T> read, CancellationToken cancellationToken);
    ValueTask<T> SendAsync<T>(int index, Func<Action, ExploreSend<T>> send, CancellationToken cancellationToken);
}
/// <summary>Trusted host work, not a Planner capability. Uses the existing Gua implementations of execute,
/// observe and condition wait; confirms all sent requests and normal owned input release before Completed.
/// It cannot repair a decision, restart/reset, change Goal conditions or dispatch outside calls.SendAsync.</summary>
public interface IExploreWork
{
    ValueTask<ExploreReceipt> ExecuteAsync(JsonObject approvedDecision, IExploreCalls calls, CancellationToken cancellationToken);
}
