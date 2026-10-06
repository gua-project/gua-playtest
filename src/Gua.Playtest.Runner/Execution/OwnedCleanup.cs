using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

public enum CleanupStage { PrimarySnapshot, Diagnostics, Artifacts, InputRelease, ResourceRelease }

/// <summary>Register only acquired owner-scoped resources, immediately after acquisition.
/// Diagnostics precede release; each resource release gets an attempt even after artifact/cancellation failure.</summary>
public sealed class OwnedCleanup
{
    private sealed class CleanupClock : IClock
    {
        private readonly RunSession run;
        private readonly Action<Exception> reject;
        private readonly Stopwatch safety = Stopwatch.StartNew();
        private TimeSpan last, lastSafety;
        private long fallbackOffset;
        private bool fallback;
        private long hardWakeAt;
        private readonly ConcurrentQueue<Exception> timerFaults = new();
        private readonly List<Task> timers = [];
        public CleanupClock(RunSession run, Action<Exception> reject)
        {
            this.run = run; this.reject = reject;
            last = run.LastValidatedReal;
            _ = Elapsed;
        }
        public TimeSpan Elapsed
        {
            get
            {
                if (!fallback && timerFaults.TryDequeue(out var fault)) Reject(fault);
                if (!fallback)
                    try
                    {
                        var now = run.ReadAuthoritativeReal();
                        var due = Interlocked.Exchange(ref hardWakeAt, 0);
                        if (due == 0 || now.Ticks >= due) { lastSafety = safety.Elapsed; return last = now; }
                        Reject(new InvalidOperationException("CleanupClockDidNotAdvance"));
                    }
                    catch (Exception exception) { Reject(exception); }
                // The independent safety clock began at cleanup entry. A rejected epoch cannot
                // rebase its duration or move behind the last good cleanup reading.
                return last = new TimeSpan(Math.Max(last.Ticks, fallbackOffset + safety.Elapsed.Ticks));
            }
        }
        private void Reject(Exception exception)
        {
            fallback = true;
            fallbackOffset = last.Ticks - lastSafety.Ticks;
            reject(exception);
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            var timer = DelayCoreAsync(duration, token).AsTask(); timers.Add(timer); return new(timer);
        }
        public async ValueTask JoinTimersAsync()
        {
            foreach (var timer in timers)
                try { await timer.ConfigureAwait(false); } catch (Exception) { /* provider evidence was queued; obsolete cancellation is expected */ }
        }
        private async ValueTask DelayCoreAsync(TimeSpan duration, CancellationToken token)
        {
            var due = Elapsed + duration;
            var hardWake = Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(duration.TotalMilliseconds)), token);
            if (fallback) { await hardWake.ConfigureAwait(false); return; }
            Task? providerWake = null;
            try
            {
                try
                {
                    providerWake = run.AuthoritativeRealClock.DelayAsync(duration, token).AsTask();
                    var winner = await Task.WhenAny(providerWake, hardWake).ConfigureAwait(false);
                    if (providerWake.IsFaulted) await providerWake.ConfigureAwait(false);
                    await winner.ConfigureAwait(false);
                    if (winner == hardWake && !token.IsCancellationRequested) Interlocked.Exchange(ref hardWakeAt, due.Ticks);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    timerFaults.Enqueue(exception);
                    await hardWake.ConfigureAwait(false);
                }
                // The timer never mutates session/issue evidence. The owner validates this hint.
            }
            finally
            {
                foreach (var task in new[] { providerWake, hardWake }.OfType<Task>())
                    _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }
    private sealed record Step(CleanupStage Stage, Func<CancellationToken, ValueTask<bool>> Action);
    private readonly List<Step> steps = [];
    private readonly object registrationGate = new();
    private bool closed;
    public void Register(CleanupStage stage, Func<CancellationToken, ValueTask<bool>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (registrationGate)
        {
            if (closed || !Enum.IsDefined(stage)) throw new InvalidOperationException("CleanupRegistrationClosed");
            if (steps.Count >= 1000) throw new InvalidOperationException("CleanupResourceLimit");
            steps.Add(new(stage, action));
        }
    }
    public async ValueTask<RunOutcome> CompleteAsync(RunSession run, IClock realClock, CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
    {
        realClock = run.AuthoritativeRealClock;
        Step[] ordered;
        lock (registrationGate)
        {
            if (closed || run.State != ExecutionState.Completing) throw new InvalidOperationException("CleanupStateInvalid");
            closed = true;
            ordered = steps.OrderBy(x => x.Stage).ToArray();
        }
        if (confirmPrimary is not null)
        {
            var snapshot = run.CapturePrimary();
            ordered = [new(CleanupStage.PrimarySnapshot, token => confirmPrimary(snapshot, token)), .. ordered];
        }
        var issues = new List<PostProcessingIssue>();
        bool AddIssue(PostProcessingIssue issue)
        {
            if (issues.Count < run.Limits.MaxEvidenceItems - 1) { issues.Add(issue); return true; }
            if (issues.Count == run.Limits.MaxEvidenceItems - 1) issues.Add(new(PostProcessingReason.EvidenceLimitExceeded));
            return false;
        }
        var cleanupClock = new CleanupClock(run, exception => AddIssue(new(PostProcessingReason.CleanupClockInvalid,
            new(exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace))));
        realClock = cleanupClock;
        var origin = realClock.Elapsed;
        var deadline = origin + run.Limits.CleanupTimeout;
        if (cancellationToken.IsCancellationRequested) AddIssue(new(PostProcessingReason.Cancelled));
        for (var i = 0; i < ordered.Length; i++)
        {
            var step = ordered[i];
            // Share remaining time fairly: a noncooperative early task cannot consume all later release attempts.
            var sampledNow = realClock.Elapsed;
            var remaining = deadline - sampledNow;
            if (remaining <= TimeSpan.Zero)
            {
                AddIssue(new(PostProcessingReason.CleanupTimeout)); AddIssue(new(Failure(step.Stage))); continue;
            }
            var share = TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / (ordered.Length - i)));
            // Caller cancellation skips diagnostics/artifacts but never skips owned input/resource cleanup.
            var token = step.Stage is CleanupStage.Diagnostics or CleanupStage.Artifacts ? cancellationToken : CancellationToken.None;
            try
            {
                var result = await FiniteOperation.RunUntilAsync(realClock, sampledNow + share, step.Action, token,
                    exception =>
                    {
                        if (issues.Count >= run.Limits.MaxEvidenceItems - 1) { AddIssue(new(PostProcessingReason.EvidenceLimitExceeded)); return; }
                        var allowance = run.Limits.MaxEvidenceItems - issues.Count;
                        var faults = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.Take(allowance) : [exception];
                        foreach (var fault in faults)
                            if (!AddIssue(new(Failure(step.Stage), new(fault.GetType().FullName ?? fault.GetType().Name, fault.StackTrace)))) break;
                    }).ConfigureAwait(false);
                if (!result) AddIssue(new(Failure(step.Stage)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AddIssue(new(PostProcessingReason.Cancelled)); AddIssue(new(Failure(step.Stage)));
            }
            catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception))
            {
                AddIssue(new(PostProcessingReason.CleanupTimeout)); AddIssue(new(Failure(step.Stage)));
            }
            catch (Exception exception)
            {
                var original = exception is ProviderCancellationException ? exception.InnerException! : exception;
                AddIssue(new(Failure(step.Stage), new(original.GetType().FullName ?? original.GetType().Name, original.StackTrace)));
            }
        }
        if (cancellationToken.IsCancellationRequested && !issues.Any(x => x.Reason == PostProcessingReason.Cancelled))
            AddIssue(new(PostProcessingReason.Cancelled));
        await cleanupClock.JoinTimersAsync().ConfigureAwait(false);
        _ = realClock.Elapsed; // drain any final safety-wake hint on the owner before freezing issues
        return run.Finish(issues.AsReadOnly());
    }
    private static PostProcessingReason Failure(CleanupStage stage) => stage switch
    {
        CleanupStage.PrimarySnapshot => PostProcessingReason.PrimarySnapshotFailed,
        CleanupStage.Diagnostics => PostProcessingReason.DiagnosticsFailed,
        CleanupStage.Artifacts => PostProcessingReason.ArtifactFailed,
        CleanupStage.InputRelease => PostProcessingReason.InputReleaseUnconfirmed,
        CleanupStage.ResourceRelease => PostProcessingReason.ResourceReleaseUnconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}

/// <summary>Finite real-time wait, including cancellation of a provider that ignores cancellation.
/// Late results never acquire Run authority. A real clock must advance independently of simulation.</summary>
internal sealed class ProviderCancellationException(OperationCanceledException original)
    : Exception("ProviderCancellation", original);

public static class FiniteOperation
{
    private static readonly object deadlineProvenance = new();
    internal static TimeoutException DeadlineReached(string code)
    {
        var exception = new TimeoutException(code);
        exception.Data[deadlineProvenance] = true;
        return exception;
    }
    internal static bool IsDeadline(Exception exception) => exception is TimeoutException && exception.Data.Contains(deadlineProvenance);
    public static ValueTask<T> RunAsync<T>(IClock realClock, TimeSpan timeout,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default, Action<Exception>? recordException = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return RunUntilAsync(realClock, realClock.Elapsed + timeout, action, cancellationToken, recordException);
    }
    /// <summary>Uses the owner's absolute deadline without rebasing a previously computed remainder.</summary>
    public static async ValueTask<T> RunUntilAsync<T>(IClock realClock, TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default, Action<Exception>? recordException = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startDeadline = deadline;
        if (realClock.Elapsed >= startDeadline) throw DeadlineReached("OperationDeadlineReached");
        using var operationCancellation = new CancellationTokenSource();
        using var timerCancellation = new CancellationTokenSource();
        Task<T>? operation = null;
        Task? timer = null;
        var cancelled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFaults = new ConcurrentQueue<Exception>();
        using var registration = cancellationToken.Register(() =>
        { CancelSafely(operationCancellation, callbackFaults.Enqueue); cancelled.TrySetCanceled(cancellationToken); });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operation = action(operationCancellation.Token).AsTask(); }
            catch (OperationCanceledException exception) { throw NormalizeCancellation(exception, operationCancellation.Token, cancellationToken); }
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken).ConfigureAwait(false);
            var remaining = startDeadline - realClock.Elapsed;
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken).ConfigureAwait(false);
            if (remaining <= TimeSpan.Zero) throw DeadlineReached("OperationDeadlineReached");
            timer = realClock.DelayAsync(remaining, timerCancellation.Token).AsTask();
            var winner = await Task.WhenAny(operation, timer, cancelled.Task).ConfigureAwait(false);
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken).ConfigureAwait(false);
            if (winner == cancelled.Task)
            {
                // Cancellation cannot erase a completed successful stage, but cannot extend its deadline.
                if (operation.IsCompletedSuccessfully)
                {
                    if (realClock.Elapsed >= startDeadline) throw DeadlineReached("OperationDeadlineReached");
                    return operation.GetAwaiter().GetResult();
                }
                await cancelled.Task.ConfigureAwait(false);
            }
            if (winner == timer) await timer.ConfigureAwait(false);
            var expired = winner == timer || realClock.Elapsed >= startDeadline;
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken).ConfigureAwait(false);
            if (expired) throw DeadlineReached("OperationDeadlineReached");
            return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose(); // join caller callbacks before draining on the owner continuation
            CancelSafely(operationCancellation, callbackFaults.Enqueue); CancelSafely(timerCancellation, callbackFaults.Enqueue);
            if (operation is not null) _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (timer is not null) _ = timer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (recordException is not null)
                while (callbackFaults.TryDequeue(out var fault)) recordException(fault);
        }
    }
    internal static Exception NormalizeCancellation(OperationCanceledException exception,
        CancellationToken providerToken, CancellationToken callerToken)
        => callerToken.IsCancellationRequested && (exception.CancellationToken == callerToken ||
            providerToken.IsCancellationRequested && exception.CancellationToken == providerToken)
            ? new OperationCanceledException("CallerInterruptedOperation", exception, callerToken)
            : new ProviderCancellationException(exception);
    internal static async ValueTask<T> AwaitProviderAsync<T>(Task<T> task, CancellationToken providerToken, CancellationToken callerToken)
    {
        try { return await task.ConfigureAwait(false); }
        catch (OperationCanceledException exception) { throw NormalizeCancellation(exception, providerToken, callerToken); }
    }
    internal static void CancelSafely(CancellationTokenSource source, Action<Exception>? record = null)
    {
        try { source.Cancel(); }
        catch (AggregateException exception) { record?.Invoke(exception); }
    }
}
