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
            token => prepare(cleanup, token), ready => new PreparedRun(ready, null), execute, cancellationToken, confirmPrimary).ConfigureAwait(false);

    /// <summary>Production initial-boundary path: preparation arms a readiness capability after Setup/compat checks,
    /// synchronizes the subscription and capture, verifies prerequisites in that unit, and returns its certificate.
    /// The optional serialized owner hook registers a reached initial observation before the first Goal arbitration.
    /// It must be bounded and must not dispatch or request Planner work; its failure retains the certified unit.</summary>
    public static async ValueTask<RunOutcome> ExecuteAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<RunSession, OwnedCleanup, CancellationToken, ValueTask<RunStartBoundary>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute,
        CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
        => await ExecuteAsync(run, realClock, cleanup, prepare, execute, cancellationToken, confirmPrimary, null).ConfigureAwait(false);

    /// <summary>Certified initial-boundary execution with bounded owner observation registration.
    /// The existing overload remains available without registration.</summary>
    public static async ValueTask<RunOutcome> ExecuteAsync(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<RunSession, OwnedCleanup, CancellationToken, ValueTask<RunStartBoundary>> prepare,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute,
        CancellationToken cancellationToken,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary,
        Action<RunSession>? registerInitialObservation)
        => await ExecuteCoreAsync(run, realClock, cleanup,
            token => prepare(run, cleanup, token), boundary => new PreparedRun(true, boundary
                ?? throw new InvalidOperationException("RunningBoundaryCertificateRequired")), execute, cancellationToken, confirmPrimary, registerInitialObservation).ConfigureAwait(false);

    private static async ValueTask<RunOutcome> ExecuteCoreAsync<T>(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<CancellationToken, ValueTask<T>> prepare, Func<T, PreparedRun> mapPreparation,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute, CancellationToken cancellationToken,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary, Action<RunSession>? registerInitialObservation = null)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(cleanup); ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(execute);
        realClock = run.AuthoritativeRealClock;
        RunStartBoundary? completedBoundary = null;
        IReadOnlyList<RunEvent> completedPreparationEvents = [];
        Task<bool>? executionTask = null;
        try
        {
            run.BindCancellation(cancellationToken);
            run.BeginPreparation();
            Task<T>? preparationTask = null;
            T preparation;
            try
            {
                preparation = await FiniteOperation.RunUntilAsync(realClock, run.NextRealEvaluationAt,
                    token => { preparationTask = run.RunExecutorStage(() => prepare(token)).AsTask(); return new ValueTask<T>(preparationTask); }, cancellationToken, run.RecordException).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && preparationTask?.IsCompletedSuccessfully == true)
            {
                // Completed preparation is evidence, even when cancellation wins its wait.
                // A certified initial failure outranks cancellation; no driver starts after cancellation.
                preparation = preparationTask.GetAwaiter().GetResult();
            }
            catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception) && preparationTask?.IsCompletedSuccessfully == true)
            {
                var completed = mapPreparation(preparationTask.GetAwaiter().GetResult());
                completedBoundary = completed.Boundary;
                if (!completed.Ready) completedPreparationEvents = [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)];
                throw;
            }
            var prepared = mapPreparation(preparation);
            completedBoundary = prepared.Boundary;
            if (!prepared.Ready) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)],
                cancelled: cancellationToken.IsCancellationRequested);
            else
            {
                if (prepared.Boundary is { } boundary)
                {
                    run.BeginRunning(boundary);
                    var initial = boundary.InitialObservation;
                    run.RetainCertifiedInitialFacts(initial);
                    await FiniteOperation.RunUntilAsync(realClock, run.RunningOrigin!.Value + run.Limits.MaxDuration,
                        _ => run.RunExecutorStage(() => new ValueTask<bool>(Task.Factory.StartNew(() =>
                        {
                            try { registerInitialObservation?.Invoke(run); }
                            catch (Exception exception)
                            {
                                // Keep the certified failure/lifecycle unit even if registration fails.
                                var reason = exception is OperationCanceledException interrupted && cancellationToken.IsCancellationRequested &&
                                    interrupted.CancellationToken == cancellationToken ? RunReason.Cancelled :
                                    FiniteOperation.IsDeadline(exception) ? RunReason.MaxDuration : RunReason.ExecutionError;
                                // Transfer both cause and original diagnostics before another
                                // provider read can stall this owner stage. Terminal closure
                                // rejects a late post from an already abandoned hook.
                                run.PostProviderException(exception is RunFailureException ? exception :
                                    new RunFailureException(new(reason, RunPhase.Execution, RunOrigin.Runner), exception));
                            }
                            run.ReadAuthoritativeCondition();
                            run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                            return true;
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))), cancellationToken, run.RecordException).ConfigureAwait(false);
                }
                else run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                if (run.Primary is null)
                {
                    if (run.State == ExecutionState.Preparing) run.BeginRunning();
                    var deadline = run.RunningOrigin!.Value + run.Limits.MaxDuration;
                    await FiniteOperation.RunUntilAsync(realClock, deadline,
                        token => run.RunExecutorStage(async () =>
                        {
                            executionTask = execute(run, token).AsTask();
                            var complete = await executionTask.ConfigureAwait(false);
                            // Completion arbitration can read providers too: keep it
                            // inside the original deadline/cancellation waiter.
                            run.Evaluate(candidates: !complete
                                ? [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)] : [],
                                cancelled: cancellationToken.IsCancellationRequested, executionComplete: complete);
                            if (run.Primary is null) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)],
                                cancelled: cancellationToken.IsCancellationRequested);
                            return complete;
                        }), cancellationToken, run.RecordException).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            if (run.State != ExecutionState.Running) run.RecordException(exception);
            var phase = run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation;
            var reason = exception switch
            {
                OperationCanceledException cancelled when cancellationToken.IsCancellationRequested && cancelled.CancellationToken == cancellationToken => RunReason.Cancelled,
                TimeoutException when FiniteOperation.IsDeadline(exception) && phase == RunPhase.Preparation => RunReason.PreparationTimeout,
                TimeoutException when FiniteOperation.IsDeadline(exception) => RunReason.MaxDuration,
                _ => RunReason.ExecutionError
            };
            var retained = FiniteOperation.IsDeadline(exception) && phase == RunPhase.Preparation && completedBoundary is not null
                ? run.InterruptedBoundaryFailureEvents(completedBoundary) : completedPreparationEvents;
            bool? completedExecution = phase == RunPhase.Execution && executionTask?.IsCompletedSuccessfully == true
                ? executionTask.GetAwaiter().GetResult() : null;
            if (completedExecution == false)
                retained = retained.Append(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)).ToArray();
            var candidates = retained.Append(exception is RunFailureException failure ? failure.Cause : new(reason, phase, RunOrigin.Runner));
            if (run.State == ExecutionState.Running)
                run.FinalizeInterruptedExecution(candidates, cancellationToken.IsCancellationRequested, completedExecution == true, exception);
            else run.Evaluate(candidates: candidates,
                cancelled: cancellationToken.IsCancellationRequested, executionComplete: completedExecution == true);
        }
        // Input/resource releases have fresh bounded tokens; main result is already immutable.
        return await cleanup.CompleteAsync(run, realClock, cancellationToken, confirmPrimary).ConfigureAwait(false);
    }
}
