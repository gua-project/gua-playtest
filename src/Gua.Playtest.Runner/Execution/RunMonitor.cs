using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Trusted capture time is the condition-clock boundary, not polling delivery time.
/// Both tree maps must come from that boundary; continuity remains the provider's evidence obligation.</summary>
public sealed record RunObservation(TimeSpan CapturedAt, ConditionObservationUnit Success, ConditionObservationUnit Failure);
public interface IRunObservationFeed
{
    ValueTask<RunObservation> CaptureAsync(CancellationToken cancellationToken);
    ValueTask WaitForChangeAsync(CancellationToken cancellationToken);
}
public sealed record MonitoredResult<T>(bool Completed, T? Value);

/// <summary>Single-owner notification/timer/real-deadline coordinator during a Planner/action/wait.
/// Work returns evidence only and may not mutate the RunSession. Never grants action authority to late results.</summary>
public static class RunMonitor
{
    public static async ValueTask<MonitoredResult<T>> AwaitAsync<T>(RunSession run, IClock realClock,
        IClock conditionClock, IRunObservationFeed feed, Func<CancellationToken, ValueTask<T>> work,
        Func<T, IReadOnlyList<RunEvent>> resultEvents, CancellationToken cancellationToken = default)
    {
        if (run.State != ExecutionState.Running) throw new InvalidOperationException("RunStateInvalid");
        using var workCancellation = new CancellationTokenSource();
        using var workRegistration = cancellationToken.Register(() => FiniteOperation.CancelSafely(workCancellation, run.RecordException));
        Task<T>? workTask = null;
        async ValueTask<(bool Completed, T? Value, IReadOnlyList<RunEvent> Events)> ReadyWork()
        {
            if (workTask is null || !workTask.IsCompleted) return (false, default, []);
            try
            {
                var value = await workTask.ConfigureAwait(false);
                return (true, value, resultEvents(value));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return (true, default, []); }
            catch (Exception exception)
            {
                run.RecordException(exception);
                return (true, default, [exception is RunFailureException failure ? failure.Cause : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)]);
            }
        }
        try
        {
            workTask = work(workCancellation.Token).AsTask();
            while (run.Primary is null)
            {
                using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remaining = run.NextRealEvaluationAt - realClock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    run.Evaluate(candidates: ready.Events, cancelled: cancellationToken.IsCancellationRequested); break;
                }
                RunObservation observation;
                Task<RunObservation>? captureTask = null;
                void EvaluateInterruptedCapture(IReadOnlyList<RunEvent> readyEvents, bool cancelled)
                {
                    if (captureTask?.IsCompletedSuccessfully == true)
                    {
                        var captured = captureTask.GetAwaiter().GetResult();
                        run.Evaluate(captured.Success, captured.CapturedAt, readyEvents, cancelled,
                            failureUnit: captured.Failure);
                    }
                    else run.Evaluate(candidates: readyEvents, cancelled: cancelled);
                }
                try
                {
                    observation = await FiniteOperation.RunAsync(realClock, remaining,
                        token => { captureTask = feed.CaptureAsync(token).AsTask(); return new ValueTask<RunObservation>(captureTask); },
                        captureCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    EvaluateInterruptedCapture(ready.Events, true); break;
                }
                catch (TimeoutException)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    EvaluateInterruptedCapture(ready.Events, cancellationToken.IsCancellationRequested); break;
                }
                var result = await ReadyWork().ConfigureAwait(false);
                // All ready observations/events/cancellation/current deadlines go through one arbiter.
                run.Evaluate(observation.Success, observation.CapturedAt, result.Events,
                    cancellationToken.IsCancellationRequested, failureUnit: observation.Failure);
                if (result.Completed) return new(run.Primary is null, result.Value);
                if (run.Primary is not null) break;
                using var wakeCancellation = new CancellationTokenSource();
                using var wakeRegistration = cancellationToken.Register(() => FiniteOperation.CancelSafely(wakeCancellation, run.RecordException));
                var wakes = new List<Task>();
                try
                {
                    wakes.Add(feed.WaitForChangeAsync(wakeCancellation.Token).AsTask());
                    wakes.Add(realClock.DelayAsync(Positive(run.NextRealEvaluationAt - realClock.Elapsed), wakeCancellation.Token).AsTask());
                    if (run.NextConditionEvaluationAt is { } conditionWake)
                        wakes.Add(conditionClock.DelayAsync(Positive(conditionWake - conditionClock.Elapsed), wakeCancellation.Token).AsTask());
                    var winner = await Task.WhenAny(wakes.Append(workTask)).ConfigureAwait(false);
                    if (winner != workTask) await winner.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    run.Evaluate(candidates: ready.Events, cancelled: true);
                }
                finally
                {
                    FiniteOperation.CancelSafely(wakeCancellation, run.RecordException);
                    foreach (var task in wakes) ObserveFault(task);
                }
            }
            return new(false, default);
        }
        catch (Exception exception)
        {
            run.RecordException(exception);
            var ready = await ReadyWork().ConfigureAwait(false);
            var faultEvents = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? Array.Empty<RunEvent>() : [exception is RunFailureException failure ? failure.Cause : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)];
            run.Evaluate(candidates: ready.Events.Concat(faultEvents),
                cancelled: cancellationToken.IsCancellationRequested);
            return new(false, default);
        }
        finally { FiniteOperation.CancelSafely(workCancellation, run.RecordException); if (workTask is not null) ObserveFault(workTask); }
    }
    private static TimeSpan Positive(TimeSpan duration) => duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    private static void ObserveFault(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
