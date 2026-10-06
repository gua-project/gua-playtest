using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Planning;

public enum PlannerReplyStatus { Completed, UsageLimit, ConnectionFailure, OutputInvalid }
public sealed record PlannerReply(PlannerReplyStatus Status, byte[]? CompletedJson = null);
public sealed record PlannerTurnResult(PlannerAdoption? Adoption, bool Interrupted, bool OwnedInputsReleased);

/// <summary>Monitor while the Planner proposes. Terminal Run authority closes before bounded owned-input
/// release, then Planner cancellation. Adapter returns only completed JSON or a typed safe failure.</summary>
public static class PlannerTurn
{
    public static async ValueTask<PlannerTurnResult> AwaitAsync(PlannerGate gate, PlannerRequest request,
        RunSession run, IClock realClock, IClock conditionClock, IRunObservationFeed feed,
        IPlanner<Core.Contracts.PlannerInputDocument, PlannerReply> planner,
        Func<CancellationToken, ValueTask<bool>> releaseOwnedInputs,
        Func<PlannerReply, IReadOnlyList<RunEvent>> resultEvents, CancellationToken cancellationToken = default)
    {
        // Do not let RunMonitor cancel the actual Planner before owner-scoped input release.
        using var plannerCancellation = new CancellationTokenSource();
        try
        {
            var result = await RunMonitor.AwaitAsync(run, realClock, conditionClock, feed,
                _ => ProposeAsync(planner, request, plannerCancellation.Token), resultEvents, cancellationToken).ConfigureAwait(false);
            if (result.Completed)
            {
                var reply = result.Value!;
                return new(gate.Adopt(request, reply.CompletedJson ?? []), false, true);
            }
            // RunSession is already closed. Late results cannot pass gate.Adopt, even if cancellation is ignored.
            var released = false;
            try
            {
                released = await FiniteOperation.RunAsync(realClock, run.Limits.CleanupTimeout,
                    releaseOwnedInputs, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* The driver maps false to owned cleanup/postprocessing evidence. */ }
            gate.Cancel(request);
            return new(null, true, released);
        }
        finally { plannerCancellation.Cancel(); }
    }

    private static async ValueTask<PlannerReply> ProposeAsync(
        IPlanner<Core.Contracts.PlannerInputDocument, PlannerReply> planner, PlannerRequest request, CancellationToken token)
    {
        try
        {
            var reply = await planner.DecideAsync(request.CopyInput(), token).ConfigureAwait(false);
            return Enum.IsDefined(reply.Status) ? reply : new(PlannerReplyStatus.OutputInvalid);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new(PlannerReplyStatus.ConnectionFailure); }
    }

}
