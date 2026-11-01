using System.Diagnostics;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

/// <summary>One cleanup epoch shared by interrupted evidence settlement and releases.</summary>
internal sealed class ExecutionSettlement(RunSession run, OwnedCleanup cleanup)
{
    private readonly object gate = new();
    private Stopwatch? elapsed;
    private TimeSpan realOrigin, settlementShare, lastElapsed, lastPhysical;
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private Exception? providerFailure;
    private bool signalled;
    private readonly long timingOperation = RuntimeTiming.NextOperation();
    private readonly List<CancellationLease> cancellations = [];
    public Task Stop => stop.Task;
    public Exception? Failure => Volatile.Read(ref failure);
    public Exception? ProviderFailure => Volatile.Read(ref providerFailure);
    public void Interrupt(Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); Begin(); }
    public void RecordFailure(Exception exception, CancellationToken callerToken)
    {
        var phase = run.State == ExecutionState.Running ? RunPhase.Execution : RunPhase.Preparation;
        var reason = exception is OperationCanceledException canceled && callerToken.IsCancellationRequested && canceled.CancellationToken == callerToken
            ? RunReason.Cancelled : FiniteOperation.IsDeadline(exception)
                ? phase == RunPhase.Preparation ? RunReason.PreparationTimeout : RunReason.MaxDuration : RunReason.ExecutionError;
        Interlocked.CompareExchange(ref providerFailure, exception, null);
        run.RetainExecutorFailure(exception is RunFailureException typed ? typed.Cause : new(reason, phase, RunOrigin.Runner));
        Interrupt(exception);
    }
    public void PostDiagnostic(Exception exception) => run.PostException(exception);
    public IDisposable RegisterCancellation(CancellationTokenSource source, Action<Exception> record)
    {
        var lease = new CancellationLease(this, source, record);
        lock (gate) { cancellations.Add(lease); if (signalled) lease.Start(); }
        return lease;
    }
    private sealed class CancellationLease(ExecutionSettlement owner, CancellationTokenSource source, Action<Exception> record) : IDisposable
    {
        private int started, disposed;
        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            _ = Task.Factory.StartNew(() =>
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try { FiniteOperation.CancelSafely(source, record); }
                catch (ObjectDisposedException) { /* obsolete owner cancellation */ }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            lock (owner.gate) owner.cancellations.Remove(this);
        }
    }
    public async ValueTask RejectNewWorkAsync(CancellationToken token)
    {
        if (!Started) return;
        // The controller resolves a pending clock sample within this evidence share.
        // New owner stages await classification asynchronously; no provider starts.
        await stop.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (Failure is { } exception) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        throw new OperationCanceledException("ExecutorOwnerClosed");
    }
    public bool Started { get { lock (gate) return elapsed is not null; } }
    public void ReserveClockValidation()
    {
        run.CloseExecutorAdmission();
        long? share = null;
        lock (gate)
        {
            if (elapsed is null)
            {
                realOrigin = run.LastValidatedReal;
                elapsed = Stopwatch.StartNew();
                // One evidence share; keep at least one share for actual cleanup.
                var releases = cleanup.SettlementResourceCount();
                settlementShare = TimeSpan.FromTicks(Math.Max(1, run.Limits.CleanupTimeout.Ticks / (releases + 2)));
                share = settlementShare.Ticks;
            }
        }
        if (share is { } ticks)
            RuntimeTiming.Mark(run, "settlement.epoch.reserved", timingOperation,
                budgetTicks: run.Limits.CleanupTimeout.Ticks, remainingTicks: ticks);
    }
    public void Begin()
    {
        ReserveClockValidation();
        lock (gate)
        {
            signalled = true;
            foreach (var lease in cancellations) lease.Start();
        }
        stop.TrySetResult();
        RuntimeTiming.Mark(run, "settlement.stop.signalled", timingOperation);
    }
    public TimeSpan Elapsed
    {
        get
        {
            lock (gate)
            {
                if (elapsed is null) return TimeSpan.Zero;
                var physical = elapsed.Elapsed;
                lastElapsed = new(Math.Max((lastElapsed + physical - lastPhysical).Ticks,
                    (run.LastValidatedReal - realOrigin).Ticks));
                lastPhysical = physical;
                return lastElapsed;
            }
        }
    }
    public TimeSpan EvidenceRemaining
    {
        get
        {
            RuntimeTiming.Mark(run, "settlement.evidence-share.enter", timingOperation);
            var used = Elapsed;
            TimeSpan remaining;
            lock (gate) remaining = settlementShare - used;
            RuntimeTiming.Mark(run, "settlement.evidence-share.return", timingOperation, remainingTicks: remaining.Ticks);
            return remaining;
        }
    }
    public TimeSpan CleanupRemaining => run.Limits.CleanupTimeout - Elapsed;
}
