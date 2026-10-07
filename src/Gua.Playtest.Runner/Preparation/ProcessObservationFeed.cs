using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Retain late source faults in both modes and exact exit evidence for owned launched processes.</summary>
internal sealed class ProcessObservationFeed(IRunObservationFeed feed, IOwnedProcess? process,
    Func<CancellationToken, ValueTask<bool>> readStatus, Action<PreparationEvent> recordTrace, Action<Exception> recordException,
    Action<Exception>? recordProviderFailure = null) : IRunObservationFeed
{
    private readonly SemaphoreSlim captureOwner = new(1);
    public async ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        return await WatchAsync(async token =>
        {
            await captureOwner.WaitAsync(token).ConfigureAwait(false);
            try { return await feed.CaptureAsync(token).ConfigureAwait(false); }
            // Source ownership lasts until the actual invocation ends, including
            // a late result after wrapper supersession or process exit.
            finally { captureOwner.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await WatchAsync(async token =>
        {
            await captureOwner.WaitAsync(token).ConfigureAwait(false);
            try { await feed.WaitForChangeAsync(token).ConfigureAwait(false); return true; }
            finally { captureOwner.Release(); }
        }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WatchAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        using var wait = new CancellationTokenSource();
        using var exitWait = new CancellationTokenSource();
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        // This token belongs to the finite owner. Let its guarded cancellation collect
        // callback faults without converting a completed observation into a provider failure.
        using var registration = cancellationToken.Register(() => FiniteOperation.CancelSafely(wait, exception =>
        { faults.Enqueue(exception); recordException(exception); }));
        Task<T>? changed = null; Task? exited = null;
        var changedEvidenceRetained = false; var exitedEvidenceRetained = false;
        Exception? failure = null; T result = default!;
        try
        {
            // Arm before source invocation, including its synchronous throw path.
            // Supersession cancels only the source; the lifecycle watch remains live.
            exited = WatchExitAsync();
            if (exited.IsCompletedSuccessfully) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            // Entry status is part of the watched task. After source completion
            // inspect the armed exact exit task directly; a further getter must
            // not strand an already-authoritative observation or exit notification.
            changed = ReadSourceAsync();
            var winner = await Task.WhenAny(changed, exited).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            // Successful exact-handle exit is authoritative even if the getter is stale.
            if (exited.IsCompletedSuccessfully) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            // A completed losing watch still carries authoritative lifecycle failure evidence.
            if (exited.IsFaulted || exited.IsCanceled) await exited.ConfigureAwait(false);
            result = await changed.ConfigureAwait(false);
            if (exited.IsCompletedSuccessfully) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            if (exited.IsFaulted || exited.IsCanceled) await exited.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // WhenAny can pick the source even when both tasks already failed. Retain
            // each ready fault and use the Run owner's normative priority for the cause.
            // Inspect before cancelling losers: cancellation cannot invent a competing cause.
            var ready = new List<Exception> { exception };
            if (exited?.IsCompletedSuccessfully == true)
            {
                recordTrace(new(PreparationStage.Launch, PreparationCode.ProcessExited));
                ready.Insert(0, new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution));
            }
            foreach (var task in new Task?[] { changed, exited })
                if (task?.IsFaulted == true || task?.IsCanceled == true)
                    try { task.GetAwaiter().GetResult(); }
                    catch (Exception original)
                    {
                        if (ReferenceEquals(task, changed)) changedEvidenceRetained = true;
                        else exitedEvidenceRetained = true;
                        if (ReferenceEquals(task, exited) && original is not RunFailureException &&
                            !(original is OperationCanceledException && exitWait.IsCancellationRequested))
                        {
                            ready.RemoveAll(item => ReferenceEquals(item, original));
                            ready.Add(new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, original, RunPhase.Execution));
                        }
                        else if (!ready.Any(item => ReferenceEquals(item, original))) ready.Add(original);
                    }
            RunEvent Cause(Exception item) => item is RunFailureException typed ? typed.Cause :
                item is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested &&
                    (cancelled.CancellationToken == wait.Token || cancelled.CancellationToken == cancellationToken)
                    ? new(RunReason.Cancelled, RunPhase.Execution, RunOrigin.User)
                    : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner);
            failure = ready.OrderBy(item => RunSession.Priority(Cause(item).Reason))
                .ThenBy(item => Cause(item).Reason).ThenBy(item => Cause(item).Phase).ThenBy(item => Cause(item).Origin).First();
            if (ready.Any(item => item is PreparationException { Stage: PreparationStage.Launch, Code: PreparationCode.LaunchFailed }) ||
                exited?.IsFaulted == true || exited?.IsCanceled == true && !exitWait.IsCancellationRequested)
                recordTrace(new(PreparationStage.Launch, PreparationCode.LaunchFailed));
            // Attach has no exit winner to end this wrapper early. Its actual
            // source may fail after a finite monitor stopped awaiting the wrapper;
            // post before completing it so observation-only cleanup cannot lose it.
            if (process is null && recordProviderFailure is not null &&
                !(failure is OperationCanceledException obsolete && cancellationToken.IsCancellationRequested &&
                  (obsolete.CancellationToken == wait.Token || obsolete.CancellationToken == cancellationToken)))
                recordProviderFailure(failure is RunFailureException ? failure :
                    new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), failure));
            foreach (var original in ready)
                if (!ReferenceEquals(original, failure)) faults.Enqueue(original);
        }
        finally
        {
            registration.Dispose();
            if (changed?.IsCompleted != true) FiniteOperation.CancelSafely(wait, exception => { faults.Enqueue(exception); recordException(exception); });
            FiniteOperation.CancelSafely(exitWait, exception => { faults.Enqueue(exception); recordException(exception); });
            ObserveRemaining(changed, changedEvidenceRetained, wait.Token, source: true);
            ObserveRemaining(exited, exitedEvidenceRetained, exitWait.Token, source: false);
        }
        void ObserveRemaining(Task? task, bool retained, CancellationToken abandonedToken, bool source)
        {
            if (task is null || retained) return;
            // A losing call can fail after the ready-task scan. Preserve its actual
            // original failure through the bounded owner-posting port, never by
            // mutating Run state. Use the same provider classification as the ready
            // path so actual pre-freeze failure cannot be treated as diagnostics. Posting
            // after primary confirmation is rejected by that port.
            _ = task.ContinueWith(completed =>
            {
                try { completed.GetAwaiter().GetResult(); }
                catch (Exception original)
                {
                    // Ordinary cancellation of an obsolete source/watch is not
                    // provider failure. Caller cancellation still retains its
                    // actual source exception; unrelated cancellation also does.
                    if (original is OperationCanceledException cancelled && completed.IsCanceled &&
                        abandonedToken.IsCancellationRequested && cancelled.CancellationToken == abandonedToken &&
                        (!source || !cancellationToken.IsCancellationRequested)) return;
                    if (recordProviderFailure is null) recordException(original);
                    else
                    {
                        Exception evidence = original is RunFailureException ? original : !source
                            ? new PreparationException(PreparationStage.Launch, PreparationCode.LaunchFailed, original, RunPhase.Execution)
                            : new RunFailureException(original is OperationCanceledException cancelledSource &&
                                cancellationToken.IsCancellationRequested &&
                                (cancelledSource.CancellationToken == abandonedToken || cancelledSource.CancellationToken == cancellationToken)
                                ? new(RunReason.Cancelled, RunPhase.Execution, RunOrigin.User)
                                : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), original);
                        if (!source) recordTrace(new(PreparationStage.Launch, PreparationCode.LaunchFailed));
                        recordProviderFailure(evidence);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Task WatchExitAsync()
        {
            // Preserve readiness of the provider's exact task; an async forwarding
            // continuation could hide a ready exit behind a ready source result.
            try { return process is null ? Task.Delay(Timeout.Infinite, exitWait.Token) : process.WaitForExitAsync(exitWait.Token).AsTask(); }
            catch (Exception exception) { return Task.FromException(exception); }
        }
        async Task<T> ReadSourceAsync()
        {
            await CheckAliveAsync(wait.Token).ConfigureAwait(false);
            if (exited?.IsCompletedSuccessfully == true) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            if (exited?.IsFaulted == true || exited?.IsCanceled == true) await exited.ConfigureAwait(false);
            wait.Token.ThrowIfCancellationRequested();
            var value = await request(wait.Token).ConfigureAwait(false);
            return value;
        }
        var requestedCancellation = failure is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested &&
            (cancelled.CancellationToken == wait.Token || cancelled.CancellationToken == cancellationToken);
        if (!faults.IsEmpty && failure is not null)
        {
            var evidence = new AggregateException(failure is null ? faults : faults.Prepend(failure));
            if (requestedCancellation)
                throw new OperationCanceledException("CaptureCancelled", evidence, cancellationToken);
            if (failure is PreparationException preparation)
                throw new PreparationException(preparation.Stage, preparation.Code, evidence, preparation.Cause.Phase);
            if (failure is RunFailureException typedFailure)
                throw new RunFailureException(typedFailure.Cause, evidence);
            throw new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), evidence);
        }
        if (requestedCancellation) throw new OperationCanceledException("CaptureCancelled", failure, cancellationToken);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
    private async ValueTask CheckAliveAsync(CancellationToken cancellationToken)
    {
        if (await readStatus(cancellationToken).ConfigureAwait(false))
        {
            recordTrace(new(PreparationStage.Launch, PreparationCode.ProcessExited));
            throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
        }
    }
}
