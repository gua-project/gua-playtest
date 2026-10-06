using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Keep launched-process exit visible during Planner/action waits. Attached processes never enter this wrapper.</summary>
internal sealed class ProcessObservationFeed(IRunObservationFeed feed, IOwnedProcess process,
    Func<CancellationToken, ValueTask<bool>> readStatus, Action<PreparationEvent> recordTrace, Action<Exception> recordException) : IRunObservationFeed
{
    private readonly SemaphoreSlim captureOwner = new(1);
    public async ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        await captureOwner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckAliveAsync(cancellationToken).ConfigureAwait(false);
            // Keep this task alive until the actual capture ends, even after supersession.
            // RunMonitor joins this task and retains its observation before a fresh capture.
            return await WatchAsync(token => feed.CaptureAsync(token).AsTask(), cancellationToken).ConfigureAwait(false);
        }
        finally { captureOwner.Release(); }
    }
    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await WatchAsync(async token => { await feed.WaitForChangeAsync(token).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WatchAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        await CheckAliveAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        using var wait = new CancellationTokenSource();
        using var exitWait = new CancellationTokenSource();
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        // This token belongs to the finite owner. Let its guarded cancellation collect
        // callback faults without converting a completed observation into a provider failure.
        using var registration = cancellationToken.Register(() => FiniteOperation.CancelSafely(wait, exception =>
        { faults.Enqueue(exception); recordException(exception); }));
        Task<T>? changed = null; Task? exited = null;
        Exception? failure = null; T result = default!;
        try
        {
            // Arm before source invocation, including its synchronous throw path.
            // Supersession cancels only the source; the lifecycle watch remains live.
            exited = WatchExitAsync();
            if (exited.IsCompletedSuccessfully) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            changed = request(wait.Token);
            var winner = await Task.WhenAny(changed, exited).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            // Successful exact-handle exit is authoritative even if the getter is stale.
            if (exited.IsCompletedSuccessfully) throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
            // A completed losing watch still carries authoritative lifecycle failure evidence.
            if (exited.IsFaulted || exited.IsCanceled) await exited.ConfigureAwait(false);
            await CheckAliveAsync(CancellationToken.None).ConfigureAwait(false);
            result = await changed.ConfigureAwait(false);
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
            foreach (var original in ready)
                if (!ReferenceEquals(original, failure)) faults.Enqueue(original);
        }
        finally
        {
            registration.Dispose();
            if (changed?.IsCompleted != true) FiniteOperation.CancelSafely(wait, exception => { faults.Enqueue(exception); recordException(exception); });
            FiniteOperation.CancelSafely(exitWait, exception => { faults.Enqueue(exception); recordException(exception); });
            if (changed is not null) _ = changed.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (exited is not null) _ = exited.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        Task WatchExitAsync()
        {
            // Preserve readiness of the provider's exact task; an async forwarding
            // continuation could hide a ready exit behind a ready source result.
            try { return process.WaitForExitAsync(exitWait.Token).AsTask(); }
            catch (Exception exception) { return Task.FromException(exception); }
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
