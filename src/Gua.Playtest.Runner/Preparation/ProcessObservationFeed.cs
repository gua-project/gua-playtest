using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Keep launched-process exit visible during Planner/action waits. Attached processes never enter this wrapper.</summary>
internal sealed class ProcessObservationFeed(IRunObservationFeed feed, IOwnedProcess process, IPreparationTrace trace) : IRunObservationFeed
{
    private readonly SemaphoreSlim captureOwner = new(1);
    private Task<RunObservation>? underlyingCapture;
    public async ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        await captureOwner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckAlive();
            // A cancelled wrapper is not proof the provider stopped. Join its actual request
            // before creating another capture; the monitor bounds this join by its original deadline.
            if (underlyingCapture is { } previous)
            {
                await WatchAsync(_ => previous, cancellationToken).ConfigureAwait(false);
                underlyingCapture = null;
            }
            CheckAlive();
            return await WatchAsync(token =>
            {
                underlyingCapture = feed.CaptureAsync(token).AsTask();
                return underlyingCapture;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { captureOwner.Release(); }
    }
    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) =>
        _ = await WatchAsync(async token => { await feed.WaitForChangeAsync(token).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WatchAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        CheckAlive(); cancellationToken.ThrowIfCancellationRequested();
        using var wait = new CancellationTokenSource();
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var registration = cancellationToken.Register(() => FiniteOperation.CancelSafely(wait, faults.Enqueue));
        Task<T>? changed = null; Task? exited = null;
        Exception? failure = null; T result = default!;
        try
        {
            changed = request(wait.Token);
            exited = process.WaitForExitAsync(wait.Token).AsTask();
            var winner = await Task.WhenAny(changed, exited).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            CheckAlive();
            result = await changed.ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            registration.Dispose();
            FiniteOperation.CancelSafely(wait, faults.Enqueue);
            if (changed is not null) _ = changed.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (exited is not null) _ = exited.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        if (!faults.IsEmpty)
        {
            var evidence = new AggregateException(failure is null ? faults : faults.Prepend(failure));
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("CaptureCancelled", evidence, cancellationToken);
            if (failure is PreparationException preparation)
                throw new PreparationException(preparation.Stage, preparation.Code, evidence, preparation.Cause.Phase);
            throw new PreparationException(PreparationStage.Synchronize, PreparationCode.SynchronizationFailed, evidence, RunPhase.Execution);
        }
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
