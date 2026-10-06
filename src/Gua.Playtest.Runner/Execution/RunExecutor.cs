using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Owns all-path finalization around the trusted, serialized engine driver.
/// The driver feeds complete evidence units, monitors during Planner waits, and registers acquisitions.
/// Transport/gate/Replay implementations remain separate adapters, not alternate result writers.</summary>
public static class RunExecutor
{
    public static async ValueTask<RunOutcome> ExecuteAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<OwnedCleanup, CancellationToken, ValueTask<bool>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(cleanup); ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(execute);
        try
        {
            run.BeginPreparation();
            var prepared = await FiniteOperation.RunAsync(realClock, run.Limits.PreparationTimeout,
                token => prepare(cleanup, token), cancellationToken).ConfigureAwait(false);
            if (!prepared) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)]);
            else
            {
                run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                if (run.Primary is null)
                {
                    run.BeginRunning();
                    var complete = await FiniteOperation.RunAsync(realClock, run.Limits.MaxDuration,
                        token => execute(run, token), cancellationToken).ConfigureAwait(false);
                    run.Evaluate(cancelled: cancellationToken.IsCancellationRequested, executionComplete: complete);
                    if (run.Primary is null) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)]);
                }
            }
        }
        catch (Exception exception)
        {
            run.RecordException(exception);
            var phase = run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation;
            var reason = exception switch
            {
                OperationCanceledException when cancellationToken.IsCancellationRequested => RunReason.Cancelled,
                TimeoutException when phase == RunPhase.Preparation => RunReason.PreparationTimeout,
                TimeoutException => RunReason.MaxDuration,
                _ => RunReason.ExecutionError
            };
            run.Evaluate(candidates: [new(reason, phase, RunOrigin.Runner)], cancelled: cancellationToken.IsCancellationRequested);
        }
        // Input/resource releases have fresh bounded tokens; main result is already immutable.
        return await cleanup.CompleteAsync(run, realClock, cancellationToken).ConfigureAwait(false);
    }
}
