using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using System.Collections.Concurrent;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Trusted capture time is the condition-clock boundary, not polling delivery time.
/// Both tree maps must come from that boundary; continuity remains the provider's evidence obligation.</summary>
public sealed record RunObservation(TimeSpan CapturedAt, ConditionObservationUnit Success, ConditionObservationUnit Failure);
public interface IRunObservationFeed
{
    /// <summary>Produce a new synchronized unit covering source state through this invocation.
    /// A cached unit from before the request does not satisfy this contract.</summary>
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
        realClock = run.AuthoritativeRealClock;
        conditionClock = run.AuthoritativeConditionClock;
        if (cancellationToken.IsCancellationRequested)
        {
            run.Evaluate(cancelled: true);
            return new(false, default);
        }
        var cancellationFaults = new ConcurrentQueue<Exception>();
        var cancellationFaultCount = 0;
        void QueueCancellationFault(Exception exception)
        {
            if (Interlocked.Increment(ref cancellationFaultCount) <= run.Limits.MaxEvidenceItems)
                cancellationFaults.Enqueue(exception);
        }
        void DrainCancellationFaults()
        {
            while (cancellationFaults.TryDequeue(out var exception)) run.RecordException(exception);
        }
        void EvaluateCapture(RunObservation? captured, IReadOnlyList<RunEvent> readyEvents, bool cancelled)
        {
            if (captured is null || captured.Success is null || captured.Failure is null)
                run.Evaluate(candidates: readyEvents.Append(new(RunReason.ObservationContractViolation,
                    RunPhase.Execution, RunOrigin.Contract)), cancelled: cancelled);
            else run.Evaluate(captured.Success, captured.CapturedAt, readyEvents, cancelled, failureUnit: captured.Failure);
        }
        using var workCancellation = new CancellationTokenSource();
        using var workRegistration = cancellationToken.Register(() => FiniteOperation.CancelSafely(workCancellation, QueueCancellationFault));
        Task<T>? workTask = null;
        (bool Completed, T? Value, IReadOnlyList<RunEvent> Events)? readyResult = null;
        async ValueTask<(bool Completed, T? Value, IReadOnlyList<RunEvent> Events)> ReadyWork()
        {
            DrainCancellationFaults();
            if (readyResult is { } cached) return cached;
            if (workTask is null || !workTask.IsCompleted) return (false, default, []);
            try
            {
                var value = await FiniteOperation.AwaitProviderAsync(workTask, workCancellation.Token, cancellationToken).ConfigureAwait(false);
                var mapped = resultEvents(value)?.Take(run.Limits.MaxEvidenceItems + 1).ToArray()
                    ?? throw new ArgumentException("RunWorkEventsInvalid");
                if (mapped.Any(x => x is null || !Enum.IsDefined(x.Reason) || !Enum.IsDefined(x.Phase) ||
                    !Enum.IsDefined(x.Origin) || x.Reason == RunReason.GoalSatisfied))
                    throw new ArgumentException("RunWorkEventsInvalid");
                readyResult = (true, value, mapped);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken) { readyResult = (true, default, []); }
            catch (Exception exception)
            {
                run.RecordException(exception);
                readyResult = (true, default, [exception is RunFailureException failure ? failure.Cause : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)]);
            }
            return readyResult.Value;
        }
        try
        {
            // Registration can synchronously propagate cancellation that arrived after entry.
            var launchAt = run.ReadAuthoritativeReal();
            if (cancellationToken.IsCancellationRequested || run.HasPendingTerminalEvidence || launchAt >= run.NextRealEvaluationAt)
            {
                run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                return new(false, default);
            }
            workTask = work(workCancellation.Token).AsTask();
            while (run.Primary is null)
            {
                using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remaining = run.NextRealEvaluationAt - run.ReadAuthoritativeReal();
                if (remaining <= TimeSpan.Zero)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    run.Evaluate(candidates: ready.Events, cancelled: cancellationToken.IsCancellationRequested); break;
                }
                RunObservation? observation;
                Task<RunObservation>? captureTask = null;
                bool supersededCapture = false;
                void EvaluateInterruptedCapture(IReadOnlyList<RunEvent> readyEvents, bool cancelled)
                {
                    if (captureTask?.IsCompletedSuccessfully == true)
                    {
                        var captured = captureTask.GetAwaiter().GetResult();
                        EvaluateCapture(captured, readyEvents, cancelled);
                    }
                    else run.Evaluate(candidates: readyEvents, cancelled: cancelled);
                }
                try
                {
                    var capturing = FiniteOperation.RunUntilAsync(realClock, run.NextRealEvaluationAt,
                        token => { captureTask = feed.CaptureAsync(token).AsTask(); return new ValueTask<RunObservation>(captureTask); },
                        captureCancellation.Token, run.RecordException).AsTask();
                    if (await Task.WhenAny(capturing, workTask).ConfigureAwait(false) == workTask && !capturing.IsCompleted)
                    {
                        supersededCapture = true;
                        FiniteOperation.CancelSafely(captureCancellation, run.RecordException);
                    }
                    observation = await capturing.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (supersededCapture && !cancellationToken.IsCancellationRequested)
                {
                    // Stop waiting for old delivery immediately. Keep any unit already completed;
                    // join the finite owner before requesting the new post-work synchronization.
                    if (captureTask?.IsFaulted == true) captureTask.GetAwaiter().GetResult();
                    observation = captureTask?.IsCompletedSuccessfully == true ? captureTask.GetAwaiter().GetResult() : null;
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == captureCancellation.Token)
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    EvaluateInterruptedCapture(ready.Events, true); break;
                }
                catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception))
                {
                    var ready = await ReadyWork().ConfigureAwait(false);
                    EvaluateInterruptedCapture(ready.Events, cancellationToken.IsCancellationRequested); break;
                }
                var result = await ReadyWork().ConfigureAwait(false);
                if (result.Completed)
                {
                    // Delivery can lag the first capture boundary. Synchronize once more after
                    // observing completion, retaining both units until the single arbiter runs.
                    var observations = new List<RunObservation?>();
                    if (observation is not null || !supersededCapture) observations.Add(observation);
                    var finalEvents = result.Events.ToList(); Task<RunObservation>? finalCapture = null;
                    try
                    {
                        var final = await FiniteOperation.RunUntilAsync(realClock, run.NextRealEvaluationAt, token =>
                        { finalCapture = feed.CaptureAsync(token).AsTask(); return new ValueTask<RunObservation>(finalCapture); }, cancellationToken, run.RecordException).ConfigureAwait(false);
                        observations.Add(final);
                    }
                    catch (Exception exception)
                    {
                        if (finalCapture?.IsCompletedSuccessfully == true) observations.Add(finalCapture.GetAwaiter().GetResult());
                        var callerInterrupted = exception is OperationCanceledException interrupted && cancellationToken.IsCancellationRequested &&
                            interrupted.CancellationToken == cancellationToken;
                        if (!callerInterrupted && !FiniteOperation.IsDeadline(exception))
                        {
                            run.RecordException(exception);
                            finalEvents.Add(exception is RunFailureException failure ? failure.Cause
                                : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner));
                        }
                    }
                    run.EvaluateCapturedUnits(observations, finalEvents, cancellationToken.IsCancellationRequested);
                    return new(run.Primary is null, result.Value);
                }
                // All ready observations/events/cancellation/current deadlines go through one arbiter.
                EvaluateCapture(observation, result.Events, cancellationToken.IsCancellationRequested);
                if (run.Primary is not null) break;
                using var wakeCancellation = new CancellationTokenSource();
                using var wakeRegistration = cancellationToken.Register(() => FiniteOperation.CancelSafely(wakeCancellation, QueueCancellationFault));
                var wakes = new List<Task>();
                var wakeFaults = new List<Exception>();
                bool wakeCancelled = false;
                try
                {
                    wakes.Add(feed.WaitForChangeAsync(wakeCancellation.Token).AsTask());
                    wakes.Add(realClock.DelayAsync(Positive(run.NextRealEvaluationAt - run.ReadAuthoritativeReal()), wakeCancellation.Token).AsTask());
                    if (run.NextConditionEvaluationAt is { } conditionWake)
                        wakes.Add(conditionClock.DelayAsync(Positive(conditionWake - run.ReadAuthoritativeCondition()), wakeCancellation.Token).AsTask());
                    var winner = await Task.WhenAny(wakes.Append(workTask)).ConfigureAwait(false);
                    if (winner != workTask) await winner.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested &&
                    (exception.CancellationToken == wakeCancellation.Token || exception.CancellationToken == cancellationToken))
                {
                    wakeCancelled = true;
                }
                catch (Exception exception)
                {
                    wakeFaults.Add(exception is OperationCanceledException cancelled
                        ? FiniteOperation.NormalizeCancellation(cancelled, wakeCancellation.Token, cancellationToken) : exception);
                }
                finally
                {
                    // Snapshot independent failures before cancelling obsolete waits. A losing
                    // notification fault is still authoritative monitoring failure evidence.
                    foreach (var task in wakes.Where(x => x.IsFaulted))
                        foreach (var exception in task.Exception!.InnerExceptions)
                            if (!wakeFaults.Contains(exception)) wakeFaults.Add(exception);
                    foreach (var task in wakes.Where(x => x.IsCanceled))
                        try { task.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException exception)
                        {
                            var cause = FiniteOperation.NormalizeCancellation(exception, wakeCancellation.Token, cancellationToken);
                            if (cause is ProviderCancellationException) wakeFaults.Add(cause);
                        }
                    FiniteOperation.CancelSafely(wakeCancellation, run.RecordException);
                    foreach (var task in wakes) ObserveFault(task);
                }
                if (wakeFaults.Count != 0 || wakeCancelled || cancellationToken.IsCancellationRequested)
                {
                    foreach (var exception in wakeFaults) run.RecordException(exception);
                    var ready = await ReadyWork().ConfigureAwait(false);
                    run.Evaluate(candidates: ready.Events.Concat(wakeFaults.Select(exception => exception is RunFailureException failure
                        ? failure.Cause : new RunEvent(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner))),
                        cancelled: wakeCancelled || cancellationToken.IsCancellationRequested);
                }
            }
            return new(false, default);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException cancelled)
                exception = FiniteOperation.NormalizeCancellation(cancelled, workCancellation.Token, cancellationToken);
            run.RecordException(exception);
            var ready = await ReadyWork().ConfigureAwait(false);
            var faultEvents = exception is OperationCanceledException interrupted && cancellationToken.IsCancellationRequested && interrupted.CancellationToken == cancellationToken
                ? Array.Empty<RunEvent>() : [exception is RunFailureException failure ? failure.Cause : new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner)];
            run.Evaluate(candidates: ready.Events.Concat(faultEvents),
                cancelled: cancellationToken.IsCancellationRequested);
            return new(false, default);
        }
        finally
        {
            // Join an in-flight caller registration before the owner freezes its exception evidence.
            workRegistration.Dispose();
            FiniteOperation.CancelSafely(workCancellation, run.RecordException);
            DrainCancellationFaults();
            if (workTask is not null) ObserveFault(workTask);
        }
    }
    private static TimeSpan Positive(TimeSpan duration) => duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    private static void ObserveFault(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
