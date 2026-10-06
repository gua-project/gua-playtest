using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Owns all-path finalization around the trusted, serialized engine driver.
/// The driver feeds complete evidence units, monitors during Planner waits, and registers acquisitions.
/// Transport/gate/Replay implementations remain separate adapters, not alternate result writers.</summary>
public static class RunExecutor
{
    private sealed record PreparedRun(bool Ready, RunStartBoundary? Boundary);
    public static async ValueTask<RunOutcome> ExecuteAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<OwnedCleanup, CancellationToken, ValueTask<bool>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute,
        CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
        => await ExecuteCoreAsync(run, realClock, cleanup,
            async token => new PreparedRun(await prepare(cleanup, token).ConfigureAwait(false), null), execute, cancellationToken, confirmPrimary).ConfigureAwait(false);

    /// <summary>Production initial-boundary path: preparation arms a readiness capability after Setup/compat checks,
    /// synchronizes the subscription and capture, verifies prerequisites in that unit, and returns its certificate.</summary>
    public static async ValueTask<RunOutcome> ExecuteAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<RunSession, OwnedCleanup, CancellationToken, ValueTask<RunStartBoundary>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute,
        CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
        => await ExecuteCoreAsync(run, realClock, cleanup,
            async token => new PreparedRun(true, await prepare(run, cleanup, token).ConfigureAwait(false)), execute, cancellationToken, confirmPrimary).ConfigureAwait(false);

    private static async ValueTask<RunOutcome> ExecuteCoreAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<CancellationToken, ValueTask<PreparedRun>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute, CancellationToken cancellationToken,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(cleanup); ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(execute);
        try
        {
            run.BeginPreparation();
            var prepared = await FiniteOperation.RunAsync(realClock, run.Limits.PreparationTimeout,
                prepare, cancellationToken).ConfigureAwait(false);
            if (!prepared.Ready) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)]);
            else
            {
                if (prepared.Boundary is { } boundary)
                {
                    run.BeginRunning(boundary);
                    var initial = boundary.InitialObservation;
                    run.Evaluate(initial.Success, initial.CapturedAt, cancelled: cancellationToken.IsCancellationRequested, failureUnit: initial.Failure);
                }
                else run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                if (run.Primary is null)
                {
                    if (run.State == ExecutionState.Preparing) run.BeginRunning();
                    var remaining = run.RunningOrigin!.Value + run.Limits.MaxDuration - realClock.Elapsed;
                    var complete = await FiniteOperation.RunAsync(realClock, remaining,
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
            run.Evaluate(candidates: [exception is RunFailureException failure ? failure.Cause : new(reason, phase, RunOrigin.Runner)],
                cancelled: cancellationToken.IsCancellationRequested);
        }
        // Input/resource releases have fresh bounded tokens; main result is already immutable.
        return await cleanup.CompleteAsync(run, realClock, cancellationToken, confirmPrimary).ConfigureAwait(false);
    }
}
