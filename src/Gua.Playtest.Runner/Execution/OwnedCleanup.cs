using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

public enum CleanupStage { Diagnostics, Artifacts, InputRelease, ResourceRelease }

/// <summary>Register only acquired owner-scoped resources, immediately after acquisition.
/// Diagnostics precede release; each resource release gets an attempt even after artifact/cancellation failure.</summary>
public sealed class OwnedCleanup
{
    private sealed record Step(CleanupStage Stage, Func<CancellationToken, ValueTask<bool>> Action);
    private readonly List<Step> steps = [];
    private bool closed;
    public void Register(CleanupStage stage, Func<CancellationToken, ValueTask<bool>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (closed || !Enum.IsDefined(stage)) throw new InvalidOperationException("CleanupRegistrationClosed");
        if (steps.Count >= 1000) throw new InvalidOperationException("CleanupResourceLimit");
        steps.Add(new(stage, action));
    }
    public async ValueTask<RunOutcome> CompleteAsync(RunSession run, IClock realClock, CancellationToken cancellationToken = default)
    {
        if (closed || run.State != ExecutionState.Completing) throw new InvalidOperationException("CleanupStateInvalid");
        closed = true;
        var issues = new List<PostProcessingIssue>();
        var origin = realClock.Elapsed;
        var deadline = origin + run.Limits.CleanupTimeout;
        var ordered = steps.OrderBy(x => x.Stage).ToArray();
        if (cancellationToken.IsCancellationRequested) issues.Add(new(PostProcessingReason.Cancelled));
        for (var i = 0; i < ordered.Length; i++)
        {
            var step = ordered[i];
            // Share remaining time fairly: a noncooperative early task cannot consume all later release attempts.
            var remaining = deadline - realClock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                issues.Add(new(PostProcessingReason.CleanupTimeout)); issues.Add(new(Failure(step.Stage))); continue;
            }
            var share = TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / (ordered.Length - i)));
            // Caller cancellation skips diagnostics/artifacts but never skips owned input/resource cleanup.
            var token = step.Stage < CleanupStage.InputRelease ? cancellationToken : CancellationToken.None;
            try
            {
                var result = await FiniteOperation.RunAsync(realClock, share, step.Action, token).ConfigureAwait(false);
                if (!result) issues.Add(new(Failure(step.Stage)));
            }
            catch (OperationCanceledException)
            {
                issues.Add(new(PostProcessingReason.Cancelled)); issues.Add(new(Failure(step.Stage)));
            }
            catch (TimeoutException)
            {
                issues.Add(new(PostProcessingReason.CleanupTimeout)); issues.Add(new(Failure(step.Stage)));
            }
            catch (Exception exception)
            {
                issues.Add(new(Failure(step.Stage), new(exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace)));
            }
        }
        if (cancellationToken.IsCancellationRequested && !issues.Any(x => x.Reason == PostProcessingReason.Cancelled))
            issues.Add(new(PostProcessingReason.Cancelled));
        return run.Finish(issues.AsReadOnly());
    }
    private static PostProcessingReason Failure(CleanupStage stage) => stage switch
    {
        CleanupStage.Diagnostics => PostProcessingReason.DiagnosticsFailed,
        CleanupStage.Artifacts => PostProcessingReason.ArtifactFailed,
        CleanupStage.InputRelease => PostProcessingReason.InputReleaseUnconfirmed,
        CleanupStage.ResourceRelease => PostProcessingReason.ResourceReleaseUnconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}

/// <summary>Finite real-time wait, including cancellation of a provider that ignores cancellation.
/// Late results never acquire Run authority. A real clock must advance independently of simulation.</summary>
public static class FiniteOperation
{
    public static async ValueTask<T> RunAsync<T>(IClock realClock, TimeSpan timeout,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var startDeadline = realClock.Elapsed + timeout;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timerCancellation = new CancellationTokenSource();
        var operation = action(operationCancellation.Token).AsTask();
        var timer = realClock.DelayAsync(timeout, timerCancellation.Token).AsTask();
        var cancelled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
        try
        {
            var winner = await Task.WhenAny(operation, timer, cancelled.Task).ConfigureAwait(false);
            if (winner == cancelled.Task) await cancelled.Task.ConfigureAwait(false);
            if (winner == timer) await timer.ConfigureAwait(false);
            if (winner == timer || realClock.Elapsed >= startDeadline) throw new TimeoutException("OperationDeadlineReached");
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            operationCancellation.Cancel(); timerCancellation.Cancel();
            _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _ = timer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
