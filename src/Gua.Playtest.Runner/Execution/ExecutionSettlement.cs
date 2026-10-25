using System.Diagnostics;

namespace Gua.Playtest.Runner.Execution;

/// <summary>One cleanup epoch shared by interrupted evidence settlement and releases.</summary>
internal sealed class ExecutionSettlement(RunSession run, OwnedCleanup cleanup)
{
    private readonly object gate = new();
    private Stopwatch? elapsed;
    private TimeSpan realOrigin, settlementShare, lastElapsed, lastPhysical;
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private readonly List<CancellationLease> cancellations = [];
    public Task Stop => stop.Task;
    public Exception? Failure => Volatile.Read(ref failure);
    public void Interrupt(Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); Begin(); }
    public void PostDiagnostic(Exception exception) => run.PostException(exception);
    public IDisposable RegisterCancellation(CancellationTokenSource source, Action<Exception> record)
    {
        var lease = new CancellationLease(this, source, record);
        lock (gate) { cancellations.Add(lease); if (elapsed is not null) lease.Start(); }
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
    public void RejectNewWork(CancellationToken token)
    {
        if (!Started) return;
        token.ThrowIfCancellationRequested();
        if (Failure is { } exception) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        throw new OperationCanceledException("ExecutorOwnerClosed");
    }
    public bool Started { get { lock (gate) return elapsed is not null; } }
    public void Begin()
    {
        run.CloseExecutorAdmission();
        lock (gate)
        {
            if (elapsed is null)
            {
                realOrigin = run.LastValidatedReal;
                elapsed = Stopwatch.StartNew();
                // One evidence share; keep at least one share for actual cleanup.
                var releases = cleanup.SettlementResourceCount();
                settlementShare = TimeSpan.FromTicks(Math.Max(1, run.Limits.CleanupTimeout.Ticks / (releases + 2)));
            }
            foreach (var lease in cancellations) lease.Start();
        }
        stop.TrySetResult();
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
    public TimeSpan EvidenceRemaining { get { var used = Elapsed; lock (gate) return settlementShare - used; } }
    public TimeSpan CleanupRemaining => run.Limits.CleanupTimeout - Elapsed;
}
