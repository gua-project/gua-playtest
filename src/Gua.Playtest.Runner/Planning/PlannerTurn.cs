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
        Func<CancellationToken, ValueTask<bool>> releaseOwnedInputs, CancellationToken cancellationToken = default)
    {
        if (!gate.Owns(run, request)) throw new ArgumentException("PlannerRequestOwnerMismatch");
        realClock = run.AuthoritativeRealClock;
        conditionClock = run.AuthoritativeConditionClock;
        // Do not let RunMonitor cancel the actual Planner before owner-scoped input release.
        using var plannerCancellation = new CancellationTokenSource();
        PlannerAdoption? adoption = null;
        try
        {
            try
            {
                var result = await RunMonitor.AwaitAsync(run, realClock, conditionClock, feed,
                    _ => ProposeAsync(planner, request, plannerCancellation.Token), Events, cancellationToken,
                    reply => { if (reply.Status == PlannerReplyStatus.Completed) gate.ConfirmResponse(request); }).ConfigureAwait(false);
                if (result.Completed)
                {
                    var reply = result.Value!;
                    adoption = gate.Adopt(request, reply.CompletedJson ?? [], cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Cancellation can arrive after the monitor's final unit or during
                        // current authority checks. Retain a ready adoption failure too.
                        run.Evaluate(candidates: adoption.TerminalEvent is { } ready ? [ready] : [], cancelled: true);
                    }
                    else
                    {
                        if (adoption.TerminalEvent is not { } terminal) return new(adoption, false, true);
                        // The preceding monitor already synchronized and arbitrated its units.
                        // A ready terminal adoption error must not open another capture window
                        // after its permit has closed or wait until the global Run deadline.
                        run.Evaluate(candidates: [terminal], cancelled: cancellationToken.IsCancellationRequested);
                    }
                }
            }
            catch (Exception exception)
            {
                // Adoption can fail after the monitor's last clock read. The arbiter must
                // consume queued clock/contract evidence and close authority before cleanup.
                run.RecordException(exception);
                run.Evaluate(candidates: [exception is RunFailureException failure ? failure.Cause :
                    new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)],
                    cancelled: cancellationToken.IsCancellationRequested);
            }
            // RunSession is already closed. Late results cannot pass gate.Adopt, even if cancellation is ignored.
            var released = false;
            try
            {
                // Only cleanup uses a fresh physical clock: a rejected execution clock must
                // neither skip the owned-input release attempt nor make it unbounded.
                released = await FiniteOperation.RunAsync(new MonotonicClock(), run.Limits.CleanupTimeout,
                    releaseOwnedInputs, CancellationToken.None, run.RecordException).ConfigureAwait(false);
            }
            catch (Exception exception) { run.RecordException(exception); /* Driver maps false to postprocessing evidence. */ }
            gate.Cancel(request);
            return new(adoption, true, released);
        }
        finally
        {
            try { plannerCancellation.Cancel(); }
            catch (Exception exception) { run.RecordException(exception); }
        }
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

    public static IReadOnlyList<RunEvent> Events(PlannerReply reply) => reply.Status switch
    {
        PlannerReplyStatus.Completed => [],
        PlannerReplyStatus.UsageLimit => [new(RunReason.PlannerUsageLimit, RunPhase.Execution, RunOrigin.Planner)],
        PlannerReplyStatus.ConnectionFailure => [new(RunReason.PlannerConnectionFailure, RunPhase.Execution, RunOrigin.Planner)],
        _ => [new(RunReason.PlannerOutputInvalid, RunPhase.Execution, RunOrigin.Planner)]
    };

}
