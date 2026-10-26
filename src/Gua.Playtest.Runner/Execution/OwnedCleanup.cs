using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

public enum CleanupStage { PrimarySnapshot, Diagnostics, Artifacts, InputRelease, ResourceRelease, OwnershipRelease }

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
        private readonly TimeSpan initial;
        private readonly TimeSpan initialSafety;
        private long fallbackOffset;
        private bool fallback;
        private long hardWakeAt;
        private readonly ConcurrentQueue<Exception> timerFaults = new();
        private readonly List<Task> timers = [];
        public CleanupClock(RunSession run, Action<Exception> reject)
        {
            this.run = run; this.reject = reject;
            initial = last = run.LastValidatedReal;
            try { initial = last = run.ReadAuthoritativeReal(); initialSafety = safety.Elapsed; }
            catch (Exception exception) { Reject(exception); }
            fallbackOffset = initial.Ticks;
            _ = Elapsed;
        }
        public TimeSpan Origin => initial;
        public TimeSpan Elapsed
        {
            get
            {
                if (!fallback && timerFaults.TryDequeue(out var fault)) Reject(fault);
                if (!fallback)
                    try
                    {
                        var elapsed = safety.Elapsed - initialSafety;
                        var now = run.ReadAuthoritativeReal();
                        var independent = initial + elapsed;
                        var due = Interlocked.Exchange(ref hardWakeAt, 0);
                        if (due != 0 && now.Ticks < due || elapsed >= run.Limits.CleanupTimeout && now < independent)
                            Reject(new InvalidOperationException("CleanupClockDidNotAdvance"));
                        else
                        {
                            lastSafety = elapsed;
                            // Count physical elapsed even between immediately completed callbacks.
                            return last = new TimeSpan(Math.Max(last.Ticks, Math.Max(now.Ticks, independent.Ticks)));
                        }
                    }
                    catch (Exception exception) { Reject(exception); }
                // The independent safety clock began at cleanup entry. A rejected epoch cannot
                // rebase its duration or move behind the last good cleanup reading.
                return last = new TimeSpan(Math.Max(last.Ticks, fallbackOffset + (safety.Elapsed - initialSafety).Ticks));
            }
        }
        private void Reject(Exception exception)
        {
            fallback = true;
            fallbackOffset = Math.Max(initial.Ticks, last.Ticks - lastSafety.Ticks);
            reject(exception is ClockProviderException { InnerException: { } original } ? original : exception);
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
            var hardWake = DelayHardUntilAsync(safety.Elapsed + duration, token);
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
                    // A provider wake is only a hint. Keep the independent finite share alive
                    // until its deadline; the owner checks clock progress before cancellation.
                    await hardWake.ConfigureAwait(false);
                    if (!token.IsCancellationRequested) Interlocked.Exchange(ref hardWakeAt, due.Ticks);
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
        private async Task DelayHardUntilAsync(TimeSpan deadline, CancellationToken token)
        {
            // Native timers may wake before their requested duration on some platforms.
            // Verify independent elapsed time too; never promote a wake hint to expiry.
            while (deadline - safety.Elapsed is var remaining && remaining > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), token).ConfigureAwait(false);
        }
    }
    private sealed record Step(CleanupStage Stage, Func<CancellationToken, ValueTask<bool>> Action, Action? CloseOwnership = null);
    private readonly List<Step> steps = [];
    private readonly object registrationGate = new();
    private bool closed;
    internal int SettlementResourceCount()
    {
        lock (registrationGate) return steps.Count;
    }
    public void Register(CleanupStage stage, Func<CancellationToken, ValueTask<bool>> action, Action? closeOwnership = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (registrationGate)
        {
            if (closed || !Enum.IsDefined(stage) || closeOwnership is not null && stage != CleanupStage.OwnershipRelease) throw new InvalidOperationException("CleanupRegistrationClosed");
            if (steps.Count >= 1000) throw new InvalidOperationException("CleanupResourceLimit");
            steps.Add(new(stage, action, closeOwnership));
        }
    }
    public async ValueTask<RunOutcome> CompleteAsync(RunSession run, IClock realClock, CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
        => await CompleteCoreAsync(run, realClock, cancellationToken, confirmPrimary, null).ConfigureAwait(false);
    internal ValueTask<RunOutcome> CompleteAsync(RunSession run, IClock realClock, CancellationToken cancellationToken,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary, ExecutionSettlement settlement)
        => CompleteCoreAsync(run, realClock, cancellationToken, confirmPrimary, settlement);
    private async ValueTask<RunOutcome> CompleteCoreAsync(RunSession run, IClock realClock, CancellationToken cancellationToken,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary, ExecutionSettlement? settlement)
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
        var resourcesConfirmed = true;
        bool AddIssue(PostProcessingIssue issue)
        {
            if (issue.Reason is PostProcessingReason.InputReleaseUnconfirmed or PostProcessingReason.ResourceReleaseUnconfirmed)
                resourcesConfirmed = false;
            if (issues.Count < run.Limits.MaxEvidenceItems - 1) { issues.Add(issue); return true; }
            if (issues.Count == run.Limits.MaxEvidenceItems - 1) issues.Add(new(PostProcessingReason.EvidenceLimitExceeded));
            return false;
        }
        void RecordStageFault(CleanupStage stage, Exception exception)
        {
            if (stage is CleanupStage.InputRelease or CleanupStage.ResourceRelease or CleanupStage.OwnershipRelease)
                resourcesConfirmed = false;
            if (issues.Count >= run.Limits.MaxEvidenceItems - 1) { AddIssue(new(PostProcessingReason.EvidenceLimitExceeded)); return; }
            var allowance = run.Limits.MaxEvidenceItems - issues.Count;
            var faults = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.Take(allowance) : [exception];
            foreach (var fault in faults)
            {
                var original = fault is ProviderCancellationException ? fault.InnerException! : fault;
                if (!AddIssue(new(Failure(stage), new(original.GetType().FullName ?? original.GetType().Name, original.StackTrace)))) break;
            }
        }
        // Revoke provider starts independently of whether earlier resource releases
        // will permit terminal exclusion removal. These trusted callbacks are pure,
        // synchronous gate closures, invoked outside the registration lock.
        foreach (var step in ordered)
            if (step.CloseOwnership is { } close)
                try { close(); } catch (Exception exception) { RecordStageFault(step.Stage, exception); }
        var interrupted = settlement?.Started == true;
        if (interrupted && settlement!.CleanupRemaining > TimeSpan.Zero)
        {
            // Retain the existing cleanup clock diagnostic without joining a stuck
            // clock getter or giving this check a second cleanup epoch.
            var probe = Task.Factory.StartNew(run.ReadAuthoritativeReal, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var allowance = TimeSpan.FromTicks(Math.Max(1, settlement.CleanupRemaining.Ticks / (ordered.Length + 1)));
            var winner = await Task.WhenAny(probe, FiniteOperation.DelayIndependentAsync(allowance, CancellationToken.None)).ConfigureAwait(false);
            if (probe.IsFaulted)
            {
                var exception = probe.Exception!.InnerException!;
                var original = exception is ClockProviderException { InnerException: { } inner } ? inner : exception;
                AddIssue(new(PostProcessingReason.CleanupClockInvalid,
                    new(original.GetType().FullName ?? original.GetType().Name,
                        new StackTrace(original, true).ToString().TrimEnd('\r', '\n'))));
            }
            else if (winner != probe || !probe.IsCompletedSuccessfully)
                AddIssue(new(PostProcessingReason.CleanupClockInvalid));
            _ = probe.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        var cleanupClock = interrupted ? null : new CleanupClock(run, exception => AddIssue(new(PostProcessingReason.CleanupClockInvalid,
            new(exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace))));
        realClock = interrupted ? new SettlementClock(settlement!) : cleanupClock!;
        var origin = interrupted ? TimeSpan.Zero : cleanupClock!.Origin;
        var deadline = interrupted ? run.Limits.CleanupTimeout : origin + run.Limits.CleanupTimeout;
        if (cancellationToken.IsCancellationRequested) AddIssue(new(PostProcessingReason.Cancelled));
        for (var i = 0; i < ordered.Length; i++)
        {
            var step = ordered[i];
            // Ownership exclusion outlives every release registered before cleanup closes,
            // including execution resources registered after preparation. Unknown releases
            // keep the exclusion even if diagnostic evidence has reached its bounded limit.
            if (step.Stage == CleanupStage.OwnershipRelease && !resourcesConfirmed)
            { AddIssue(new(PostProcessingReason.ResourceReleaseUnconfirmed)); continue; }
            // Share remaining time fairly: a noncooperative early task cannot consume all later release attempts.
            var sampledNow = realClock.Elapsed;
            var remaining = deadline - sampledNow;
            if (remaining <= TimeSpan.Zero)
            {
                AddIssue(new(PostProcessingReason.CleanupTimeout));
                if (step.Stage is not (CleanupStage.InputRelease or CleanupStage.ResourceRelease or CleanupStage.OwnershipRelease))
                { AddIssue(new(Failure(step.Stage))); continue; }
                // An expired observation budget still owes a release attempt. Invoke once,
                // accept only already-completed confirmation, and grant no additional wait.
                using var releaseCancellation = new CancellationTokenSource();
                Task<bool>? release = null;
                try
                {
                    release = step.Action(releaseCancellation.Token).AsTask();
                    if (!release.IsCompleted || !release.GetAwaiter().GetResult()) AddIssue(new(Failure(step.Stage)));
                }
                catch (Exception exception) { RecordStageFault(step.Stage, exception); }
                finally
                {
                    FiniteOperation.CancelSafely(releaseCancellation, exception => RecordStageFault(step.Stage, exception));
                    if (release is not null) _ = release.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                continue;
            }
            var share = TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / (ordered.Length - i)));
            // Caller cancellation skips diagnostics/artifacts but never skips owned input/resource cleanup.
            var token = step.Stage is CleanupStage.Diagnostics or CleanupStage.Artifacts ? cancellationToken : CancellationToken.None;
            var ownedReleaseStage = step.Stage is CleanupStage.InputRelease or CleanupStage.ResourceRelease or CleanupStage.OwnershipRelease;
            Task<bool>? ownedRelease = null;
            try
            {
                var result = await FiniteOperation.RunUntilAsync(realClock, sampledNow + share, cancellation =>
                {
                    var work = step.Action(cancellation);
                    if (!ownedReleaseStage) return work;
                    ownedRelease = work.AsTask();
                    return new(ownedRelease);
                }, token,
                    exception => RecordStageFault(step.Stage, exception)).ConfigureAwait(false);
                if (!result) AddIssue(new(Failure(step.Stage)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AddIssue(new(PostProcessingReason.Cancelled)); AddIssue(new(Failure(step.Stage)));
            }
            catch (TimeoutException exception) when (FiniteOperation.IsDeadline(exception))
            {
                AddIssue(new(PostProcessingReason.CleanupTimeout));
                // Expired cleanup still owes an immediate owned release attempt. Reuse
                // the existing invocation's already-completed confirmation, without
                // invoking twice, waiting again, or claiming that it met its deadline.
                if (ownedRelease?.IsCompletedSuccessfully != true || !ownedRelease.GetAwaiter().GetResult())
                    AddIssue(new(Failure(step.Stage)));
            }
            catch (Exception exception)
            {
                var original = exception is ProviderCancellationException ? exception.InnerException! : exception;
                AddIssue(new(Failure(step.Stage), new(original.GetType().FullName ?? original.GetType().Name, original.StackTrace)));
            }
        }
        if (cancellationToken.IsCancellationRequested && !issues.Any(x => x.Reason == PostProcessingReason.Cancelled))
            AddIssue(new(PostProcessingReason.Cancelled));
        if (cleanupClock is not null) await cleanupClock.JoinTimersAsync().ConfigureAwait(false);
        _ = realClock.Elapsed; // drain any final safety-wake hint on the owner before freezing issues
        return run.Finish(issues.AsReadOnly());
    }
    private sealed class SettlementClock(ExecutionSettlement settlement) : IClock
    {
        public TimeSpan Elapsed => settlement.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
            => new(FiniteOperation.DelayIndependentAsync(duration, token));
    }
    private static PostProcessingReason Failure(CleanupStage stage) => stage switch
    {
        CleanupStage.PrimarySnapshot => PostProcessingReason.PrimarySnapshotFailed,
        CleanupStage.Diagnostics => PostProcessingReason.DiagnosticsFailed,
        CleanupStage.Artifacts => PostProcessingReason.ArtifactFailed,
        CleanupStage.InputRelease => PostProcessingReason.InputReleaseUnconfirmed,
        CleanupStage.ResourceRelease => PostProcessingReason.ResourceReleaseUnconfirmed,
        CleanupStage.OwnershipRelease => PostProcessingReason.ResourceReleaseUnconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}

/// <summary>Finite real-time wait, including cancellation of a provider that ignores cancellation.
/// Late results never acquire Run authority. A real clock must advance independently of simulation.</summary>
internal sealed class ProviderCancellationException(OperationCanceledException original)
    : Exception("ProviderCancellation", original);
internal sealed class ClockProviderException(Exception? original = null) : Exception("ClockProviderFailure", original);

public static class FiniteOperation
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TimeoutException, object> deadlineProvenance = new();
    internal static TimeoutException DeadlineReached(string code)
    {
        var exception = new TimeoutException(code);
        deadlineProvenance.Add(exception, new());
        return exception;
    }
    internal static bool IsDeadline(Exception exception) => exception is TimeoutException timeout && deadlineProvenance.TryGetValue(timeout, out _);
    public static ValueTask<T> RunAsync<T>(IClock realClock, TimeSpan timeout,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default, Action<Exception>? recordException = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return RunUntilAsync(realClock, realClock.Elapsed + timeout, action, cancellationToken, recordException);
    }
    /// <summary>Uses the owner's absolute deadline without rebasing a previously computed remainder.</summary>
    public static ValueTask<T> RunUntilAsync<T>(IClock realClock, TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default, Action<Exception>? recordException = null)
        => RunUntilCoreAsync(realClock, deadline, action, cancellationToken, recordException, null);
    internal static ValueTask<T> RunUntilAsync<T>(IClock realClock, TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken, Action<Exception>? recordException,
        ExecutionSettlement settlement)
        => RunUntilCoreAsync(realClock, deadline, action, cancellationToken, recordException, settlement);
    private static async ValueTask<T> RunUntilCoreAsync<T>(IClock realClock, TimeSpan deadline,
        Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken, Action<Exception>? recordException,
        ExecutionSettlement? settlement)
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
        void RecordCallbackFault(Exception exception)
        {
            if (settlement is null) callbackFaults.Enqueue(exception);
            else settlement.PostDiagnostic(exception);
        }
        using var registration = cancellationToken.Register(() =>
        { CancelSafely(operationCancellation, RecordCallbackFault); cancelled.TrySetCanceled(cancellationToken); });
        using var ownerCancellation = settlement?.RegisterCancellation(operationCancellation, RecordCallbackFault);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settlement is not null) await settlement.RejectNewWorkAsync(cancellationToken).ConfigureAwait(false);
            try { operation = action(operationCancellation.Token).AsTask(); }
            catch (OperationCanceledException exception) { throw NormalizeCancellation(exception, operationCancellation.Token, cancellationToken); }
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
            var remaining = startDeadline - realClock.Elapsed;
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
            if (remaining <= TimeSpan.Zero) throw DeadlineReached("OperationDeadlineReached");
            timer = realClock.DelayAsync(remaining, timerCancellation.Token).AsTask();
            var independentWake = false;
            Task winner;
            while (true)
            {
                winner = settlement is null
                    ? await Task.WhenAny(operation, timer, cancelled.Task).ConfigureAwait(false)
                    : await Task.WhenAny(operation, timer, cancelled.Task, settlement.Stop).ConfigureAwait(false);
                if (settlement is not null && (winner == settlement.Stop || cancellationToken.IsCancellationRequested))
                    await SettleExistingAsync(operation, settlement).ConfigureAwait(false);
                if (operation.IsFaulted || operation.IsCanceled)
                {
                    if (timer.IsFaulted && recordException is not null)
                        foreach (var fault in timer.Exception!.InnerExceptions) recordException(fault);
                    return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
                }
                if (timer.IsFaulted) await timer.ConfigureAwait(false);
                if (settlement is not null && winner == settlement.Stop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (settlement.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    throw new OperationCanceledException("ExecutorOwnerClosed");
                }
                if (winner != timer) break;
                await timer.ConfigureAwait(false);
                var remainder = startDeadline - realClock.Elapsed;
                if (remainder <= TimeSpan.Zero)
                {
                    if (settlement is not null)
                    {
                        settlement.Interrupt(DeadlineReached("OperationDeadlineReached"));
                        await SettleExistingAsync(operation, settlement).ConfigureAwait(false);
                    }
                    break;
                }
                // Ordinary native timers can wake early. Reschedule without reading session
                // clocks on helper continuations, and keep work/caller cancellation in the race.
                if (independentWake) throw new ClockProviderException();
                independentWake = true;
                timer = DelayIndependentAsync(remainder, timerCancellation.Token);
            }
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
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
            var expired = realClock.Elapsed >= startDeadline;
            if (winner == timer && !expired) throw new ClockProviderException();
            if (operation.IsFaulted || operation.IsCanceled) return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
            if (expired) throw DeadlineReached("OperationDeadlineReached");
            return await AwaitProviderAsync(operation, operationCancellation.Token, cancellationToken, settlement).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Retain normalized provider evidence before cancellation can block finally.
            settlement?.RecordFailure(exception, cancellationToken);
            throw;
        }
        finally
        {
            registration.Dispose(); // join caller callbacks before draining on the owner continuation
            CancelSafely(operationCancellation, RecordCallbackFault); CancelSafely(timerCancellation, RecordCallbackFault);
            if (operation is not null) _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (timer is not null) _ = timer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (recordException is not null)
                while (callbackFaults.TryDequeue(out var fault)) recordException(fault);
        }
    }
    private static async ValueTask SettleExistingAsync(Task operation, ExecutionSettlement settlement)
    {
        settlement.Begin();
        var remaining = settlement.EvidenceRemaining;
        if (!operation.IsCompleted && remaining > TimeSpan.Zero)
            await Task.WhenAny(operation, DelayIndependentAsync(remaining, CancellationToken.None)).ConfigureAwait(false);
    }
    internal static async Task DelayIndependentAsync(TimeSpan duration, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (duration - elapsed.Elapsed is var remaining && remaining > TimeSpan.Zero)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), token).ConfigureAwait(false);
    }
    internal static Exception NormalizeCancellation(OperationCanceledException exception,
        CancellationToken providerToken, CancellationToken callerToken)
        => callerToken.IsCancellationRequested && (exception.CancellationToken == callerToken ||
            providerToken.IsCancellationRequested && exception.CancellationToken == providerToken)
            ? new OperationCanceledException("CallerInterruptedOperation", exception, callerToken)
            : new ProviderCancellationException(exception);
    internal static async ValueTask<T> AwaitProviderAsync<T>(Task<T> task, CancellationToken providerToken, CancellationToken callerToken)
        => await AwaitProviderAsync(task, providerToken, callerToken, null).ConfigureAwait(false);
    internal static async ValueTask<T> AwaitProviderAsync<T>(Task<T> task, CancellationToken providerToken, CancellationToken callerToken,
        ExecutionSettlement? settlement)
    {
        try { return await task.ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        {
            if (!callerToken.IsCancellationRequested && providerToken.IsCancellationRequested && exception.CancellationToken == providerToken &&
                settlement?.Failure is { } deadline && IsDeadline(deadline)) throw deadline;
            throw NormalizeCancellation(exception, providerToken, callerToken);
        }
    }
    internal static void CancelSafely(CancellationTokenSource source, Action<Exception>? record = null)
    {
        try { source.Cancel(); }
        catch (AggregateException exception) { record?.Invoke(exception); }
    }
}
