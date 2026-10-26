using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Owns all-path finalization around the trusted, serialized engine driver.
/// The driver feeds complete evidence units, monitors during Planner waits, and registers acquisitions.
/// Transport/gate/Replay implementations remain separate adapters, not alternate result writers.</summary>
public static class RunExecutor
{
    private sealed record PreparedRun(bool Ready, RunStartBoundary? Boundary);
    private sealed class OwnerEvidence
    {
        public volatile RunStartBoundary? Boundary;
        public volatile IReadOnlyList<RunEvent> PreparationEvents = [];
        public volatile Task? Preparation;
        public volatile Task<bool>? Execution;
        public volatile Exception? Failure;
    }
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
        var settlement = new ExecutionSettlement(run, cleanup);
        using var cancellation = cancellationToken.Register(settlement.Begin);
        var evidence = new OwnerEvidence();
        // Keep the original provider invocation/timer ordering on the owner. The
        // independent controller also bounds synchronous code before a Task exists.
        var owner = Task.Factory.StartNew(() => run.RunExecutorStage(async () =>
        {
            await DriveCoreAsync(run, realClock, cleanup, prepare, mapPreparation, execute,
                cancellationToken, evidence, settlement, registerInitialObservation).ConfigureAwait(false);
            return true;
        }).AsTask(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Task<TimeSpan>? clockRead = null;
        var physical = Stopwatch.StartNew();
        var observedState = ExecutionState.Created;
        var physicalLimit = run.Limits.PreparationTimeout;
        Exception? interruption = null;
        RunEvent? interruptionCause = null;
        while (!owner.IsCompleted && !settlement.Started)
        {
            if (run.Primary is not null) { settlement.Begin(); break; }
            if (cancellationToken.IsCancellationRequested) { settlement.Begin(); break; }
            if (clockRead?.IsCompleted == true)
            {
                try
                {
                    var now = await clockRead.ConfigureAwait(false);
                    var bound = run.ExecutorDeadline;
                    if (run.State != ExecutionState.Created && now >= bound.Deadline)
                    {
                        interruption = FiniteOperation.DeadlineReached("ExecutorDeadlineReached");
                        interruptionCause = new(bound.Reason, bound.Phase, RunOrigin.Clock);
                    }
                }
                catch (Exception exception)
                {
                    interruption = exception;
                    interruptionCause = new(RunReason.InvalidContract,
                        run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock);
                }
                clockRead = null;
                if (interruption is not null)
                {
                    run.PostProviderException(new RunFailureException(interruptionCause!, interruption));
                    settlement.Interrupt(interruption); break;
                }
            }
            if (run.State != observedState && run.State is ExecutionState.Preparing or ExecutionState.Running)
            {
                observedState = run.State; physical.Restart();
                var bound = run.ExecutorDeadline;
                physicalLimit = bound.Deadline - run.LastValidatedReal;
            }
            if (physical.Elapsed >= physicalLimit)
            {
                var bound = run.ExecutorDeadline;
                // A stale watermark cannot prove that a healthy clock stopped.
                // Close new authority, then validate the outstanding/current sample
                // using a share of the same cleanup epoch, without joining its thread.
                settlement.ReserveClockValidation();
                clockRead ??= Task.Factory.StartNew(run.ReadAuthoritativeReal, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var allowance = settlement.EvidenceRemaining;
                if (!clockRead.IsCompleted && allowance > TimeSpan.Zero)
                    await Task.WhenAny(clockRead, owner, FiniteOperation.DelayIndependentAsync(allowance, CancellationToken.None)).ConfigureAwait(false);
                if (owner.IsCompleted || run.Primary is not null) { settlement.Begin(); break; }
                try
                {
                    if (!clockRead.IsCompleted) throw new ClockProviderException();
                    var now = await clockRead.ConfigureAwait(false);
                    if (run.State == ExecutionState.Created || now < bound.Deadline) throw new ClockProviderException();
                    interruption = FiniteOperation.DeadlineReached("ExecutorDeadlineReached");
                    interruptionCause = new(bound.Reason, bound.Phase, RunOrigin.Clock);
                }
                catch (Exception exception)
                {
                    interruption = exception;
                    interruptionCause = new(RunReason.InvalidContract,
                        run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation, RunOrigin.Clock);
                }
                run.PostProviderException(new RunFailureException(interruptionCause, interruption));
                settlement.Interrupt(interruption); break;
            }
            await Task.WhenAny(owner, Task.Delay(10)).ConfigureAwait(false);
            if (!owner.IsCompleted && !settlement.Started && run.Primary is null && clockRead is null)
                clockRead = Task.Factory.StartNew(run.ReadAuthoritativeReal, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        if (settlement.Started && !owner.IsCompleted)
        {
            var remaining = settlement.EvidenceRemaining;
            if (remaining > TimeSpan.Zero)
                await Task.WhenAny(owner, FiniteOperation.DelayIndependentAsync(remaining, CancellationToken.None)).ConfigureAwait(false);
        }
        if (owner.IsFaulted) evidence.Failure = owner.Exception!.InnerException ?? owner.Exception;
        if (run.Primary is null)
        {
            settlement.Begin();
            // Only inspect an already-successful task. Mapping is the executor's
            // own bool/certificate projection, never a new provider invocation.
            if (evidence.Preparation is Task<T> { IsCompletedSuccessfully: true } completedPreparation)
            {
                try
                {
                    var prepared = mapPreparation(completedPreparation.Result);
                    evidence.Boundary = prepared.Boundary;
                    if (!prepared.Ready) evidence.PreparationEvents = [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)];
                }
                catch (Exception exception) { evidence.Failure ??= exception; }
            }
            var phase = run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation;
            var failure = evidence.Failure ?? settlement.ProviderFailure ?? settlement.Failure ?? interruption ?? (cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken) : new InvalidOperationException("ExecutorOwnerIncomplete"));
            var candidates = evidence.PreparationEvents.ToList();
            if (evidence.Boundary is { } boundary) candidates.AddRange(run.KnownInterruptedBoundaryEvents(boundary));
            var knownFailure = evidence.Failure ?? settlement.ProviderFailure ?? settlement.Failure;
            if (knownFailure is RunFailureException typed) candidates.Add(typed.Cause);
            else if (knownFailure is not null)
            {
                var reason = knownFailure is OperationCanceledException canceled && cancellationToken.IsCancellationRequested && canceled.CancellationToken == cancellationToken
                    ? RunReason.Cancelled : FiniteOperation.IsDeadline(knownFailure)
                        ? phase == RunPhase.Preparation ? RunReason.PreparationTimeout : RunReason.MaxDuration : RunReason.ExecutionError;
                candidates.Add(new(reason, phase, RunOrigin.Runner));
            }
            if (interruptionCause is not null) candidates.Add(interruptionCause);
            if (cancellationToken.IsCancellationRequested) candidates.Add(new(RunReason.Cancelled, phase, RunOrigin.User));
            if (evidence.Execution?.IsCompletedSuccessfully == true && !evidence.Execution.Result)
                candidates.Add(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner));
            if (candidates.Count == 0) candidates.Add(new(RunReason.ExecutionError, phase, RunOrigin.Runner));
            run.FinalizeInterruptedExecution(candidates, cancellationToken.IsCancellationRequested,
                evidence.Execution?.IsCompletedSuccessfully == true && evidence.Execution.Result, failure);
        }
        _ = owner.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        if (clockRead is not null) _ = clockRead.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await cleanup.CompleteAsync(run, run.AuthoritativeRealClock, cancellationToken, confirmPrimary, settlement).ConfigureAwait(false);
    }

    private static async ValueTask DriveCoreAsync<T>(RunSession run, IClock realClock,
        OwnedCleanup cleanup, Func<CancellationToken, ValueTask<T>> prepare, Func<T, PreparedRun> mapPreparation,
        Func<RunSession, CancellationToken, ValueTask<bool>> execute, CancellationToken cancellationToken,
        OwnerEvidence evidence, ExecutionSettlement settlement, Action<RunSession>? registerInitialObservation)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(cleanup); ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(execute);
        realClock = run.AuthoritativeRealClock;
        try
        {
            run.BindCancellation(cancellationToken);
            run.BeginPreparation();
            Task<T>? preparationTask = null;
            T preparation;
            try
            {
                preparation = await FiniteOperation.RunUntilAsync(realClock, run.NextRealEvaluationAt,
                    token =>
                    {
                        preparationTask = run.RunExecutorStage(() => prepare(token)).AsTask();
                        evidence.Preparation = preparationTask;
                        return new ValueTask<T>(preparationTask);
                    }, cancellationToken, run.RecordException, settlement).ConfigureAwait(false);
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
                evidence.Boundary = completed.Boundary;
                if (!completed.Ready) evidence.PreparationEvents = [new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host)];
                throw;
            }
            var prepared = mapPreparation(preparation);
            evidence.Boundary = prepared.Boundary;
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
                        _ => run.RunExecutorStage(() => new ValueTask<bool>(Task.Factory.StartNew(async () =>
                        {
                            await settlement.RejectNewWorkAsync(cancellationToken).ConfigureAwait(false);
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
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap())), cancellationToken, run.RecordException, settlement).ConfigureAwait(false);
                }
                else run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                if (run.Primary is null)
                {
                    if (run.State == ExecutionState.Preparing) run.BeginRunning();
                    var deadline = run.RunningOrigin!.Value + run.Limits.MaxDuration;
                    var complete = await FiniteOperation.RunUntilAsync(realClock, deadline,
                        token => { evidence.Execution = run.RunExecutorStage(() => execute(run, token)).AsTask(); return new ValueTask<bool>(evidence.Execution); },
                        cancellationToken, run.RecordException, settlement).ConfigureAwait(false);
                    // Provider cancellation callbacks settle before primary publication.
                    // The independent owner controller still bounds this synchronous arbitration.
                    run.Evaluate(candidates: !complete
                        ? [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)] : [],
                        cancelled: cancellationToken.IsCancellationRequested, executionComplete: complete);
                    if (run.Primary is null) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)],
                        cancelled: cancellationToken.IsCancellationRequested);
                }
            }
        }
        catch (Exception exception)
        {
            if (run.Primary is not null) return;
            evidence.Failure = exception;
            settlement.Begin();
            if (run.State != ExecutionState.Running) run.RecordException(exception);
            var phase = run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation;
            var reason = exception switch
            {
                OperationCanceledException cancelled when cancellationToken.IsCancellationRequested && cancelled.CancellationToken == cancellationToken => RunReason.Cancelled,
                TimeoutException when FiniteOperation.IsDeadline(exception) && phase == RunPhase.Preparation => RunReason.PreparationTimeout,
                TimeoutException when FiniteOperation.IsDeadline(exception) => RunReason.MaxDuration,
                _ => RunReason.ExecutionError
            };
            var retained = FiniteOperation.IsDeadline(exception) && phase == RunPhase.Preparation && evidence.Boundary is not null
                ? run.InterruptedBoundaryFailureEvents(evidence.Boundary) : evidence.PreparationEvents;
            bool? completedExecution = phase == RunPhase.Execution && evidence.Execution?.IsCompletedSuccessfully == true
                ? evidence.Execution.GetAwaiter().GetResult() : null;
            if (completedExecution == false)
                retained = retained.Append(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)).ToArray();
            var candidates = retained.Append(exception is RunFailureException failure ? failure.Cause : new(reason, phase, RunOrigin.Runner));
            if (run.State == ExecutionState.Running)
                run.FinalizeInterruptedExecution(candidates, cancellationToken.IsCancellationRequested, completedExecution == true, exception);
            else run.Evaluate(candidates: candidates,
                cancelled: cancellationToken.IsCancellationRequested, executionComplete: completedExecution == true);
        }
        // Input/resource releases have fresh bounded tokens; main result is already immutable.
        return;
    }
}
