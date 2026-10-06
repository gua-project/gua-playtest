using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Keep launched-process exit visible during Planner/action waits. Attached processes never enter this wrapper.</summary>
internal sealed class ProcessObservationFeed(IRunObservationFeed feed, IOwnedProcess process, IPreparationTrace trace) : IRunObservationFeed
{
    public async ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        CheckAlive();
        var capture = await feed.CaptureAsync(cancellationToken).ConfigureAwait(false);
        CheckAlive(); return capture;
    }
    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changed = feed.WaitForChangeAsync(wait.Token).AsTask();
        var exited = process.WaitForExitAsync(wait.Token).AsTask();
        try
        {
            var winner = await Task.WhenAny(changed, exited).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
            CheckAlive();
        }
        finally
        {
            wait.Cancel();
            _ = changed.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _ = exited.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
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
