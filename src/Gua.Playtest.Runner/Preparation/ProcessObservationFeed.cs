using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Keep launched-process exit visible during Planner/action waits. Attached processes never enter this wrapper.</summary>
internal sealed class ProcessObservationFeed(IRunObservationFeed feed, IOwnedProcess process, IPreparationTrace trace) : IRunObservationFeed
{
    private readonly SemaphoreSlim captureOwner = new(1);
    public async ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        await captureOwner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckAlive();
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
        CheckAlive(); cancellationToken.ThrowIfCancellationRequested();
        using var wait = new CancellationTokenSource();
        using var exitWait = new CancellationTokenSource();
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        // This token belongs to the finite owner. Let its guarded cancellation collect
        // callback faults without converting a completed observation into a provider failure.
        using var registration = cancellationToken.Register(wait.Cancel);
        Task<T>? changed = null; Task? exited = null;
        Exception? failure = null; T result = default!;
        try
        {
            changed = request(wait.Token);
            // Supersession cancels the source, not the process-exit watch: ending the wrapper
            // early here would hide a late authoritative observation from the monitor's join.
            exited = process.WaitForExitAsync(exitWait.Token).AsTask();
            var winner = await Task.WhenAny(changed, exited).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            CheckAlive();
            result = await changed.ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            registration.Dispose();
            if (changed?.IsCompleted != true) FiniteOperation.CancelSafely(wait, faults.Enqueue);
            FiniteOperation.CancelSafely(exitWait, faults.Enqueue);
            if (changed is not null) _ = changed.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (exited is not null) _ = exited.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        var requestedCancellation = failure is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested &&
            (cancelled.CancellationToken == wait.Token || cancelled.CancellationToken == cancellationToken);
        if (!faults.IsEmpty)
        {
            var evidence = new AggregateException(failure is null ? faults : faults.Prepend(failure));
            if (requestedCancellation)
                throw new OperationCanceledException("CaptureCancelled", evidence, cancellationToken);
            if (failure is PreparationException preparation)
                throw new PreparationException(preparation.Stage, preparation.Code, evidence, preparation.Cause.Phase);
            if (failure is RunFailureException typedFailure)
                throw new RunFailureException(typedFailure.Cause, evidence);
            throw new PreparationException(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed, evidence, RunPhase.Execution);
        }
        if (requestedCancellation) throw new OperationCanceledException("CaptureCancelled", failure, cancellationToken);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
    private void CheckAlive()
    {
        if (process.HasExited)
        {
            trace.Record(new(PreparationStage.Launch, PreparationCode.ProcessExited));
            throw new PreparationException(PreparationStage.Launch, PreparationCode.ProcessExited, phase: RunPhase.Execution);
        }
    }
}
