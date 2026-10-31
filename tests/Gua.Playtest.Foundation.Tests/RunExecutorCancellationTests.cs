using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)] [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public async Task ReadyProviderFaultSurvivesOwnerOnlyClockRead(bool preparation, int kind)
    {
        using var clock = new OwnerReadBarrierClock(); var run = new RunSession(Limits(), clock, new Clock());
        using var caller = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken supplied = default;
        ValueTask<bool> Provider(CancellationToken token)
        { supplied = token; clock.ArmOwner(); return new(pending.Task); }
        var cleanup = new OwnedCleanup(); var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup,
            (_, token) => preparation ? Provider(token) : ValueTask.FromResult(true), (_, token) => Provider(token), caller.Token).AsTask();
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Exception original;
            try
            {
                if (kind == 0) throw new RunFailureException(new(preparation ? RunReason.ExecutionError : RunReason.ActionFailed,
                    preparation ? RunPhase.Preparation : RunPhase.Execution, RunOrigin.Host), new IOException("provider fault"));
                if (kind == 4) throw new ThrowingDeadlineData();
                throw new OperationCanceledException(kind == 1 ? CancellationToken.None : supplied);
            }
            catch (Exception exception) { original = exception; }
            if (kind == 3) pending.SetCanceled(supplied); else pending.SetException(original);
            caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(kind is 2 or 3 ? RunReason.Cancelled : kind == 0 && !preparation ? RunReason.ActionFailed : RunReason.ExecutionError,
                outcome.Primary.Cause.Reason);
            Assert.Equal(preparation ? RunPhase.Preparation : RunPhase.Execution, outcome.Primary.Cause.Phase);
            Assert.Equal(1, releases); Assert.False(clock.Returned.Task.IsCompleted);
            Assert.Contains(outcome.Exceptions, item => item.Type == (kind == 3 ? typeof(TaskCanceledException) : original.GetType()).FullName && !string.IsNullOrEmpty(item.StackTrace));
            clock.Resume.Set(); await clock.Returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Exceptions, run.Exceptions);
        }
        finally { clock.Resume.Set(); await clock.Returned.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }
    private sealed class ThrowingDeadlineData : TimeoutException
    {
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("ProviderMetadataMustNotBeRead");
    }
    private sealed class OwnerReadBarrierClock : IClock, IDisposable
    {
        private readonly AsyncLocal<bool> owner = new(); private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public void ArmOwner() { owner.Value = true; Volatile.Write(ref armed, 1); }
        public TimeSpan Elapsed
        {
            get
            {
                if (owner.Value && Interlocked.Exchange(ref armed, 0) == 1)
                { Entered.TrySetResult(); try { Resume.Wait(); } finally { Returned.TrySetResult(); } }
                return TimeSpan.Zero;
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(FiniteOperation.DelayIndependentAsync(duration, token));
        public void Dispose() => Resume.Dispose();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InterruptedCleanupClockValidationCannotReportFalseSuccess(bool held)
    {
        using var clock = new CleanupProbeBarrierClock(held); var run = new RunSession(Limits(), clock, new Clock(), Condition());
        using var resumeDriver = new ManualResetEventSlim();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => ValueTask.FromResult(true),
            (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
            {
                clock.Driver.Value = true; clock.Armed = true;
                owner.Evaluate(Unit(), TimeSpan.Zero); published.TrySetResult();
                try { resumeDriver.Wait(); return true; } finally { ended.TrySetResult(); }
            }))).AsTask();
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (held) await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultStatus.Passed, outcome.Primary.Status); Assert.Equal(held ? 11 : 0, outcome.ExitCode);
            Assert.Equal(!held, outcome.PostProcessingComplete); Assert.Equal(1, releases); Assert.False(ended.Task.IsCompleted);
            if (held) Assert.Contains(outcome.PostProcessing, item => item.Reason == PostProcessingReason.CleanupClockInvalid);
            else Assert.DoesNotContain(outcome.PostProcessing, item => item.Reason == PostProcessingReason.CleanupClockInvalid);
            clock.Resume.Set(); resumeDriver.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary);
        }
        finally { clock.Resume.Set(); resumeDriver.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }
    private sealed class CleanupProbeBarrierClock(bool held) : IClock, IDisposable
    {
        public AsyncLocal<bool> Driver { get; } = new();
        public volatile bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public TimeSpan Elapsed
        {
            get { if (held && Armed && !Driver.Value) { Entered.TrySetResult(); Resume.Wait(); } return TimeSpan.Zero; }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(FiniteOperation.DelayIndependentAsync(duration, token));
        public void Dispose() => Resume.Dispose();
    }

    [Fact]
    public void RecordedWrappersCannotConsumePendingDiagnosticAllowance()
    {
        var clock = new Clock(); var limits = Limits();
        var run = new RunSession(new(limits.MaxDuration, limits.PreparationTimeout, limits.CleanupTimeout,
            limits.PlannerTimeout, limits.WaitTimeout, limits.ActionTimeout, limits.MaxActions,
            limits.MaxDecisions, limits.RecoveryDecisions, 32), clock, clock);
        run.BeginPreparation(); run.BeginRunning();
        Exception recorded = new IOException();
        for (var index = 0; index < 4; index++) recorded = new AggregateException(recorded);
        run.RecordException(recorded);
        for (var index = 0; index < 24; index++) run.RecordException(new IOException());
        Assert.Equal(29, run.Exceptions.Count);
        run.RecordException(new AggregateException(recorded, new AggregateException(new IOException())));
        Assert.Equal(32, run.Exceptions.Count);
        Assert.Equal(6, run.Exceptions.Count(item => item.Type == typeof(AggregateException).FullName));
        Assert.Equal(26, run.Exceptions.Count(item => item.Type == typeof(IOException).FullName));
    }

    [Fact]
    public void RepeatedDiagnosticIdentityDoesNotMergeDistinctEqualEvidence()
    {
        var clock = new Clock(); var run = Running(clock);
        var first = new IOException(); var second = new IOException();
        run.RecordException(first); run.RecordException(first); run.RecordException(second);
        Assert.Equal(2, run.Exceptions.Count);
        Assert.Equal(run.Exceptions[0], run.Exceptions[1]);
        try { throw first; } catch (IOException) { run.RecordException(first); }
        Assert.Equal(2, run.Exceptions.Count);
        Assert.Equal(first.StackTrace, run.Exceptions[0].StackTrace);
    }

    [Theory] [InlineData(32)] [InlineData(64)]
    public async Task InterruptedDiagnosticTraversalRetainsEveryDistinctLeaf(int evidenceLimit)
    {
        var clock = new CancellationArbitrationClock(); var limits = Limits();
        limits = new(limits.MaxDuration, limits.PreparationTimeout, limits.CleanupTimeout,
            limits.PlannerTimeout, limits.WaitTimeout, limits.ActionTimeout, limits.MaxActions,
            limits.MaxDecisions, limits.RecoveryDecisions, evidenceLimit);
        var run = new RunSession(limits, clock, clock);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaf = new InterruptedDiagnosticLeaf(entered, resume);
        try { throw leaf; } catch (InterruptedDiagnosticLeaf) { }
        var leaves = Enumerable.Range(0, 28).Select(_ => (Exception)new IOException()).Append(leaf).ToArray();
        var failure = new RunFailureException(new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host), new AggregateException(leaves));
        var cleanup = new OwnedCleanup(); var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => ValueTask.FromResult(true),
            (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
            {
                try { owner.PostProviderException(failure); owner.Evaluate(); return false; }
                finally { ended.TrySetResult(); }
            })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(InterruptedDiagnosticLeaf).FullName && !string.IsNullOrEmpty(item.StackTrace));
            Assert.Equal(failure.Cause, outcome.Primary.Cause); Assert.Equal(1, releases); Assert.False(ended.Task.IsCompleted);
            if (evidenceLimit == 32)
            {
                Assert.Equal(32, outcome.Exceptions.Count);
                Assert.Equal(28, outcome.Exceptions.Count(item => item.Type == typeof(IOException).FullName));
                Assert.Single(outcome.Exceptions, item => item.Type == typeof(RunFailureException).FullName);
                Assert.Single(outcome.Exceptions, item => item.Type == typeof(AggregateException).FullName);
            }
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(outcome.Exceptions, run.Exceptions); Assert.Same(outcome.Primary, run.Primary);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }
    private sealed class InterruptedDiagnosticLeaf(TaskCompletionSource entered, ManualResetEventSlim resume) : Exception
    {
        public override string? StackTrace { get { entered.TrySetResult(); resume.Wait(); return base.StackTrace; } }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedPreparationSurvivesBlockedTokenCancellation(bool certified)
    {
        var clock = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), clock, clock, failure: certified ? Condition() : null);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); var releases = 0; var executed = false;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        void Register(CancellationToken token)
            => token.Register(() => { entered.TrySetResult(); try { resume.Wait(); } finally { ended.TrySetResult(); } });
        ValueTask<bool> Execute(RunSession _, CancellationToken token) { executed = true; return ValueTask.FromResult(true); }
        var executing = certified
            ? RunExecutor.ExecuteAsync(run, clock, cleanup, (owner, _, token) =>
            {
                var capture = owner.ArmRunningBoundary();
                var boundary = capture.Certify(capture.RequestId, clock.Elapsed,
                    new(TimeSpan.Zero, Unit("false"), Unit()), "source/completed-preparation", true);
                Register(token); return ValueTask.FromResult(boundary);
            }, Execute, caller.Token).AsTask()
            : RunExecutor.ExecuteAsync(run, clock, cleanup, (_, token) =>
                { Register(token); return ValueTask.FromResult(false); }, Execute, caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var cancelling = RunBlockingFixture(caller.Cancel);
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(certified ? RunReason.FailureCondition : RunReason.ExecutionError, outcome.Primary.Cause.Reason);
            Assert.Equal(RunPhase.Preparation, outcome.Primary.Cause.Phase);
            Assert.Contains(outcome.Events, item => item.Reason == RunReason.Cancelled);
            Assert.Equal(1, releases); Assert.False(executed); Assert.False(ended.Task.IsCompleted);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); await cancelling.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FinalDispatchProbeCannotReopenClosedAdmission(int interruption)
    {
        var clock = new CancellationArbitrationClock(); var run = new RunSession(Limits(), clock, clock);
        using var caller = new CancellationTokenSource(); run.BindCancellation(caller.Token); run.BeginPreparation();
        var capture = run.ArmRunningBoundary();
        var boundary = capture.Certify(capture.RequestId, clock.Elapsed,
            new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/final-dispatch-probe", true);
        var armed = false; var polls = 0;
        boundary.InitialLifecycleFailure = () =>
        {
            if (armed && ++polls == 2)
            {
                if (interruption == 0) caller.Cancel();
                else if (interruption == 1) run.CloseExecutorAdmission();
                else { clock.At(TimeSpan.FromSeconds(1)); run.ReadAuthoritativeReal(); }
            }
            return null;
        };
        run.BeginRunning(boundary);
        var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!; armed = true;
        try { operation.BeginDispatch(0); } catch (InvalidOperationException) { }
        Assert.Equal(2, polls);
        Assert.Equal(DeliveryState.Reserved, operation.Actions!.Deliveries[0]);
        Assert.Equal(0, run.Budget.Snapshot.Actions); Assert.Equal(1, run.Budget.Snapshot.ReservedActions);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FaultedStageSurvivesBlockedTokenCancellation(bool preparation)
    {
        var clock = new CancellationArbitrationClock(); var run = new RunSession(Limits(), clock, clock);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception original; try { throw new InvalidDataException("original fault"); } catch (Exception exception) { original = exception; }
        var cause = new RunEvent(preparation ? RunReason.ExecutionError : RunReason.ActionFailed,
            preparation ? RunPhase.Preparation : RunPhase.Execution, RunOrigin.Host);
        var failure = new RunFailureException(cause, original);
        var cleanup = new OwnedCleanup(); var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        ValueTask<bool> Fault(CancellationToken token)
        {
            token.Register(() => { entered.TrySetResult(); try { resume.Wait(); } finally { ended.TrySetResult(); } });
            return new(Task.FromException<bool>(failure));
        }
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup,
            (_, token) => preparation ? Fault(token) : ValueTask.FromResult(true), (_, token) => Fault(token), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var cancelling = RunBlockingFixture(caller.Cancel);
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(cause, outcome.Primary.Cause);
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(InvalidDataException).FullName && !string.IsNullOrEmpty(item.StackTrace));
            Assert.Equal(1, releases); Assert.False(ended.Task.IsCompleted);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); await cancelling.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(outcome.Exceptions, run.Exceptions); Assert.Same(outcome.Primary, run.Primary);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task PhysicalDeadlineValidatesPendingClockSample(bool frozen, bool withheld)
    {
        using var clock = new DeferredControllerClock(frozen);
        var limits = Limits();
        limits = new(TimeSpan.FromMilliseconds(100), limits.PreparationTimeout, limits.CleanupTimeout,
            limits.PlannerTimeout, limits.WaitTimeout, limits.ActionTimeout, limits.MaxActions,
            limits.MaxDecisions, limits.RecoveryDecisions, limits.MaxEvidenceItems);
        var run = new RunSession(limits, clock, new Clock());
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => ValueTask.FromResult(true), (_, _) =>
        {
            clock.Arm(); entered.TrySetResult();
            try { resume.Wait(); return ValueTask.FromResult(true); }
            finally { ended.TrySetResult(); }
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (!withheld)
            {
                await FiniteOperation.DelayIndependentAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);
                clock.Release.Set();
            }
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            var invalidClock = frozen || withheld;
            Assert.Equal(invalidClock ? RunReason.InvalidContract : RunReason.MaxDuration, outcome.Primary.Cause.Reason);
            Assert.Equal(1, releases); Assert.False(ended.Task.IsCompleted);
            Assert.DoesNotContain(outcome.Events, item => item.Reason == (invalidClock ? RunReason.MaxDuration : RunReason.InvalidContract));
            if (withheld) Assert.False(clock.Release.IsSet);
            clock.Release.Set(); await clock.Returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { clock.Release.Set(); resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }
    private sealed class DeferredControllerClock(bool frozen) : IClock, IDisposable
    {
        private readonly Gua.Playtest.Runner.MonotonicClock source = new();
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Arm() => Volatile.Write(ref armed, 1);
        public TimeSpan Elapsed
        {
            get
            {
                if (Interlocked.Exchange(ref armed, 0) == 1)
                { Entered.TrySetResult(); try { Release.Wait(); } finally { Returned.TrySetResult(); } }
                return frozen ? TimeSpan.Zero : source.Elapsed;
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => source.DelayAsync(duration, token);
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task SettlementBudgetDoesNotRestartOrPauseAfterRealClockProgress()
    {
        var clock = new CancellationArbitrationClock(); var run = new RunSession(Limits(), clock, clock);
        run.BeginPreparation(); run.BeginRunning();
        var settlement = new ExecutionSettlement(run, new OwnedCleanup()); settlement.Begin();
        clock.At(TimeSpan.FromMilliseconds(200)); run.ReadAuthoritativeReal();
        var before = settlement.Elapsed;
        await FiniteOperation.DelayIndependentAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        var after = settlement.Elapsed;
        Assert.True(after - before >= TimeSpan.FromMilliseconds(20));
        var remaining = settlement.CleanupRemaining; settlement.Begin();
        Assert.True(settlement.CleanupRemaining <= remaining);
        Assert.True(settlement.EvidenceRemaining < Limits().CleanupTimeout / 2 - after);
        Assert.Null(run.RequestPlanner()); Assert.Null(run.ApproveOperation(1, TimeSpan.FromMilliseconds(100)));
        Assert.Null(run.ApproveObservation(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, run.Budget.Snapshot.Actions); Assert.Equal(0, run.Budget.Snapshot.Decisions);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task CertifiedBoundaryValidationCannotBlockCleanup(bool deadline, bool repeatedGetter, bool knownFailure)
    {
        var real = new CancellationArbitrationClock(); var condition = new LaunchReadClock();
        var run = new RunSession(Limits(), real, condition, failure: knownFailure ? Condition() : null);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); var releases = 0; var executed = false;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunBlockingFixture(() => RunExecutor.ExecuteAsync(run, real, cleanup, (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit(knownFailure ? "true" : "false")), "source/boundary-validation", true);
            void Read()
            {
                if (repeatedGetter) condition.OnRead = Read;
                entered.TrySetResult(); try { resume.Wait(); } finally { returned.TrySetResult(); }
            }
            condition.OnRead = Read; return ValueTask.FromResult(boundary);
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, caller.Token).AsTask()).Unwrap();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().PreparationTimeout); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(knownFailure ? RunReason.FailureCondition : deadline ? RunReason.PreparationTimeout : RunReason.Cancelled, outcome.Primary.Cause.Reason);
            Assert.Equal(1, releases); Assert.False(executed); Assert.False(returned.Task.IsCompleted);
            resume.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(ExecutionState.Finished, run.State);
        }
        finally { resume.Set(); await executing.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task InterruptionKeepsClosedOperationPruningAndCleanupSerialized()
    {
        var clock = new CancellationArbitrationClock(); var run = new RunSession(Limits(), clock, clock);
        using var caller = new CancellationTokenSource(); var releases = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => ValueTask.FromResult(true), async (owner, _) =>
        {
            try
            {
                await Task.Yield(); var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
                operation.Actions!.ConfirmNotSent(0); operation.Complete(); ready.TrySetResult();
                await finish.Task; return true;
            }
            finally { ended.TrySetResult(); }
        }, caller.Token).AsTask();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(1)); caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(RunReason.Cancelled, outcome.Primary.Cause.Reason); Assert.Equal(1, releases);
            Assert.Equal(0, run.Budget.Snapshot.Actions); Assert.Equal(0, run.Budget.Snapshot.ReservedActions);
        }
        finally { finish.TrySetResult(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task ExpiredPermitCannotQueueEvidenceAfterInterruptionPublication()
    {
        var real = new CancellationArbitrationClock(); var run = new RunSession(Limits(), real, new Clock());
        using var caller = new CancellationTokenSource(); var releases = 0;
        var expiryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedCleanup(); cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), async (owner, _) =>
        {
            try
            {
                await Task.Yield(); var permit = owner.RequestPlanner()!; real.Driver.Value = true; var reads = 0;
                void Read()
                {
                    if (++reads == 3) { real.At(Limits().PlannerTimeout); expiryRead.TrySetResult(); }
                    else real.OnDriverRead = Read;
                }
                real.OnDriverRead = Read;
                try { Assert.Null(permit.Approve(0, TimeSpan.FromMilliseconds(100))); }
                finally { approvalEnded.TrySetResult(); }
                await finish.Task; return true;
            }
            finally { ended.TrySetResult(); }
        }, caller.Token).AsTask();
        try
        {
            await expiryRead.Task.WaitAsync(TimeSpan.FromSeconds(1)); caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            await approvalEnded.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, releases); Assert.False(run.HasPendingTerminalEvidence);
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { finish.TrySetResult(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    private sealed class BlockingFinalizerDiagnostic(Action read) : Exception
    {
        public override string? StackTrace { get { read(); return base.StackTrace; } }
    }
    [Fact]
    public async Task ExecutorFailureFinalizationDoesNotInvokeProviderDiagnosticGetters()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        using var resume = new ManualResetEventSlim(); var reads = 0; var releases = 0;
        var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunBlockingFixture(() => RunExecutor.ExecuteAsync(run, clock, cleanup, (_, _) => ValueTask.FromResult(true), async (_, _) =>
        {
            await Task.Yield();
            throw new BlockingFinalizerDiagnostic(() => { Interlocked.Increment(ref reads); resume.Wait(); });
        }).AsTask()).Unwrap();
        try
        {
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(RunReason.ExecutionError, outcome.Primary.Cause.Reason); Assert.Equal(1, releases); Assert.Equal(0, reads);
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(BlockingFinalizerDiagnostic).FullName && !string.IsNullOrEmpty(item.StackTrace));
        }
        finally { resume.Set(); await executing.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData("envelope")] [InlineData("condition")] [InlineData("failure")] [InlineData("real")]
    public void CaptureFailurePublicationRetainsCauseAndDiagnostic(string kind)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition, null, Condition());
        run.BeginPreparation(); run.BeginRunning();
        if (kind == "real") { real.Driver.Value = true; real.OnDriverRead = () => throw new IOException("real read"); }
        if (kind == "condition")
        {
            condition.Driver.Value = true;
            condition.OnDriverRead = () => condition.OnDriverRead = () => throw new IOException("condition read");
        }
        run.Evaluate(Unit("false"), kind == "envelope" ? TimeSpan.FromTicks(-1) : TimeSpan.Zero, failureUnit: Unit());
        Assert.True(SpinWait.SpinUntil(() => run.Primary is not null, TimeSpan.FromSeconds(1)));
        var result = run.Primary!;
        Assert.Contains(run.Events, item => item.Reason == (kind == "failure" ? RunReason.FailureCondition :
            kind == "real" ? RunReason.InvalidContract : RunReason.ObservationContractViolation));
        Assert.Equal(kind == "failure" ? ResultStatus.Failed : ResultStatus.Invalid, result.Status);
        if (kind != "failure") Assert.Contains(run.Exceptions, item => item.Type ==
            (kind == "envelope" ? typeof(InvalidOperationException) : typeof(IOException)).FullName && !string.IsNullOrEmpty(item.StackTrace));
    }

    [Theory]
    [InlineData("initial", false)] [InlineData("driver", false)] [InlineData("completion", false)]
    [InlineData("initial", true)] [InlineData("driver", true)] [InlineData("completion", true)]
    [InlineData("synchronous-completion", false)] [InlineData("synchronous-completion", true)]
    [InlineData("synchronous-driver", false)] [InlineData("synchronous-driver", true)]
    public async Task ExecutorFinalizationDoesNotStartAnotherBlockingProvider(string stage, bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        using var resumeProbe = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false; var releases = 0; var probeOnPool = 0;
        ValueTask<bool> Execute()
        {
            if (stage == "synchronous-completion") { armed = true; returned.TrySetResult(); return ValueTask.FromResult(true); }
            if (stage == "synchronous-driver")
            { armed = true; entered.TrySetResult(); resume.Wait(); returned.TrySetResult(); return ValueTask.FromResult(true); }
            return ExecuteAsync();
        }
        ValueTask<bool> ExecuteAsync() => new(RunBlockingFixture(() =>
        {
            armed = true;
            if (stage == "completion") { returned.TrySetResult(); return true; }
            entered.TrySetResult();
            resume.Wait(); returned.TrySetResult(); return true;
        }));
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunBlockingFixture(() => RunExecutor.ExecuteAsync(run, real, cleanup, (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/finalizer-probe", true);
            boundary.InitialLifecycleFailure = () =>
            {
                if (armed)
                { Volatile.Write(ref probeOnPool, Thread.CurrentThread.IsThreadPoolThread ? 1 : 0); entered.TrySetResult(); resumeProbe.Wait(); }
                return null;
            };
            return ValueTask.FromResult(boundary);
        }, (_, _) => Execute(), caller.Token, null, _ =>
        {
            if (stage != "initial") return;
            armed = true; condition.Driver.Value = true;
            condition.OnDriverRead = () =>
            { entered.TrySetResult(); try { resume.Wait(); } finally { returned.TrySetResult(); } };
        }).AsTask()).Unwrap();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
            RunOutcome outcome;
            try { outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException)
            {
                ThreadPool.GetAvailableThreads(out var available, out _); ThreadPool.GetMinThreads(out var minimum, out _);
                throw new Xunit.Sdk.XunitException($"Finalization did not finish: stage={stage}, deadline={deadline}, state={run.State}, primary={run.Primary?.Cause}, releases={Volatile.Read(ref releases)}, providerReturned={returned.Task.IsCompleted}, probeOnPool={Volatile.Read(ref probeOnPool)}, validatedReal={run.LastValidatedReal}, executor={executing.Status}, workers={ThreadPool.ThreadCount}, available={available}, minimum={minimum}, processors={Environment.ProcessorCount}");
            }
            Assert.Equal(deadline ? RunReason.MaxDuration : RunReason.Cancelled, outcome.Primary.Cause.Reason);
            Assert.Equal(1, releases); Assert.Equal(stage is "completion" or "synchronous-completion", returned.Task.IsCompleted);
            resume.Set(); resumeProbe.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { resume.Set(); resumeProbe.Set(); await executing.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task RegistrationFailureSurvivesStalledFollowingClock(bool deadline, bool typed)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = false; var releases = 0;
        var cause = new RunEvent(typed ? RunReason.ActionFailed : RunReason.ExecutionError,
            RunPhase.Execution, typed ? RunOrigin.Host : RunOrigin.Runner);
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, real, cleanup, (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            return ValueTask.FromResult(capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/registration-failure", true));
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, caller.Token, null, _ =>
        {
            condition.Driver.Value = true;
            condition.OnDriverRead = () =>
            { entered.TrySetResult(); try { resume.Wait(); } finally { returned.TrySetResult(); } };
            try { throw new InvalidDataException("known registration failure"); }
            catch (Exception exception) { if (typed) throw new RunFailureException(cause, exception); throw; }
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(cause, outcome.Primary.Cause);
            Assert.Contains(outcome.Events, item => item.Reason == (deadline ? RunReason.MaxDuration : RunReason.Cancelled));
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(InvalidDataException).FullName && !string.IsNullOrEmpty(item.StackTrace));
            Assert.Equal(1, releases); Assert.False(executed); Assert.False(returned.Task.IsCompleted);
            resume.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events); Assert.Equal(outcome.Exceptions, run.Exceptions);
        }
        finally { resume.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData("initial", false)] [InlineData("initial", true)]
    [InlineData("driver", false)] [InlineData("driver", true)]
    [InlineData("preparation", false)] [InlineData("preparation", true)]
    public async Task AbandonedExecutorStageCannotAppendDiagnosticsAfterSnapshot(string stage, bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunSnapshot? snapshot = null;
        async ValueTask<bool> LateWork()
        {
            await Task.Yield(); entered.TrySetResult();
            await RunBlockingFixture(() => resume.Wait());
            run.RecordException(new InvalidDataException("abandoned stage"));
            ended.TrySetResult(); return true;
        }
        async ValueTask<bool> Confirm(RunSnapshot captured, CancellationToken token)
        { snapshot = captured; snapshotReady.TrySetResult(); await finish.Task.WaitAsync(token); return true; }
        var executing = stage == "initial"
            ? RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (owner, _, _) =>
            {
                var capture = owner.ArmRunningBoundary();
                return ValueTask.FromResult(capture.Certify(capture.RequestId, TimeSpan.Zero,
                    new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/abandoned-hook", true));
            }, (_, _) => ValueTask.FromResult(true), caller.Token, Confirm, _ =>
            {
                condition.Driver.Value = true;
                condition.OnDriverRead = () => ended.TrySetResult();
                entered.TrySetResult(); resume.Wait();
                throw new InvalidDataException("abandoned registration");
            }).AsTask()
            : RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(),
                (_, _) => stage == "preparation" ? LateWork() : ValueTask.FromResult(true),
                (_, _) => LateWork(), caller.Token, Confirm).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(stage == "preparation" ? Limits().PreparationTimeout : Limits().MaxDuration);
            else caller.Cancel();
            await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(ended.Task.IsCompleted);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); finish.TrySetResult();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(deadline ? stage == "preparation" ? RunReason.PreparationTimeout : RunReason.MaxDuration : RunReason.Cancelled,
                outcome.Primary.Cause.Reason);
            Assert.Equal(snapshot!.Exceptions, outcome.Exceptions);
            Assert.DoesNotContain(outcome.Exceptions, item => item.Type == typeof(InvalidDataException).FullName);
        }
        finally { resume.Set(); finish.TrySetResult(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData("real", false)] [InlineData("condition", false)] [InlineData("lifecycle", false)]
    [InlineData("real", true)] [InlineData("condition", true)] [InlineData("lifecycle", true)]
    public async Task CertifiedInitialArbitrationIsBoundedBeforeDriverStarts(string dependency, bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition, Condition()); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = false; var releases = 0; var armed = 0;
        void Stall() { entered.TrySetResult(); resume.Wait(); }
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunBlockingFixture(() => RunExecutor.ExecuteAsync(run, real, cleanup, (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/initial-bound", true);
            boundary.InitialLifecycleFailure = () =>
            { if (Interlocked.Exchange(ref armed, 0) == 1) { try { Stall(); } finally { returned.TrySetResult(); } } return null; };
            return ValueTask.FromResult(boundary);
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, caller.Token, null, _ =>
        {
            if (dependency == "lifecycle") armed = 1;
            else
            {
                var clock = dependency == "real" ? real : condition; clock.Driver.Value = true;
                clock.OnDriverRead = () => { try { Stall(); } finally { returned.TrySetResult(); } };
            }
        }).AsTask()).Unwrap();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(deadline ? RunReason.MaxDuration : RunReason.Cancelled, outcome.Primary.Cause.Reason);
            Assert.Equal(1, releases); Assert.False(executed); Assert.False(returned.Task.IsCompleted);
            resume.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { resume.Set(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task RevokedDriverCannotAppendDiagnosticsAfterPrimarySnapshot()
    {
        var real = new CancellationArbitrationClock(); var run = new RunSession(Limits(), real, new Clock());
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); RunSnapshot? snapshot = null;
        var executing = RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                real.Driver.Value = true;
                var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!; operation.BeginDispatch(0); operation.Complete();
                real.OnDriverRead = () => { entered.TrySetResult(); resume.Wait(); };
                owner.Evaluate(); owner.RecordException(new InvalidDataException("revoked driver")); return false;
            }
            finally { real.Driver.Value = false; ended.TrySetResult(); }
        })), caller.Token, async (captured, token) =>
        { snapshot = captured; snapshotReady.TrySetResult(); await finish.Task.WaitAsync(token); return true; }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); caller.Cancel();
            await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(1));
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); finish.TrySetResult();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(RunReason.ActionUnconfirmed, outcome.Primary.Cause.Reason);
            Assert.Equal(snapshot!.Exceptions, outcome.Exceptions);
            Assert.DoesNotContain(outcome.Exceptions, item => item.Type == typeof(InvalidDataException).FullName);
        }
        finally { resume.Set(); finish.TrySetResult(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task GoalSuffixRefundAndInterruptionConsumeEachReservationOnce()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var clock = new Clock(); var run = new RunSession(Limits(actions: 1000), clock, clock, Condition());
            run.BeginPreparation(); run.BeginRunning(); var operation = run.ApproveOperation(1000, TimeSpan.FromSeconds(1))!;
            operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
            var diagnostic = new GoalSuffixRaceDiagnostics(run.Budget.Snapshot);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? goal = null, cancel = null;
            var waitSite = "cancel-ready";
            try
            {
                cancel = RunBlockingFixture(() =>
                {
                    diagnostic.Cancel.Start();
                    try
                    {
                        ready.TrySetResult();
                        diagnostic.Cancel.Mark("waiting-goal");
                        Assert.True(SpinWait.SpinUntil(() => { Thread.MemoryBarrier(); return run.GoalVerified; }, TimeSpan.FromSeconds(1)));
                        diagnostic.Cancel.Mark("goal-observed");
                        BudgetSnapshot? sampled = null;
                        diagnostic.Cancel.Mark("waiting-refund");
                        Assert.True(SpinWait.SpinUntil(() => { Thread.MemoryBarrier(); sampled = run.Budget.Snapshot; return sampled.ReservedActions < 999; }, TimeSpan.FromSeconds(1)));
                        diagnostic.Cancel.Mark("refund-observed", sampled);
                        diagnostic.Cancel.Mark("before-cancel-evaluate");
                        run.Evaluate(cancelled: true);
                        diagnostic.Cancel.Mark("cancel-evaluation-returned");
                    }
                    catch (Exception error) { diagnostic.Cancel.Fault(error); throw; }
                    finally { diagnostic.Cancel.Exit(); }
                });
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));
                waitSite = "goal-and-cancel";
                goal = RunBlockingFixture(() =>
                {
                    diagnostic.Goal.Start();
                    try
                    {
                        diagnostic.Goal.Mark("before-goal-evaluate");
                        run.Evaluate(Unit(), TimeSpan.Zero);
                        diagnostic.Goal.Mark("goal-evaluation-returned");
                    }
                    catch (Exception error) { diagnostic.Goal.Fault(error); throw; }
                    finally { diagnostic.Goal.Exit(); }
                });
                try { await Task.WhenAll(goal, cancel).WaitAsync(TimeSpan.FromSeconds(1)); } catch (InvalidOperationException) { }
                waitSite = "reservation-assertions";
                Assert.Equal(0, run.Budget.Snapshot.ReservedActions); Assert.Equal(1, run.Budget.Snapshot.Actions);
                Assert.Equal(999, operation.Actions.Deliveries.Count(item => item == DeliveryState.NotSent));
                Assert.Equal(RunReason.ActionUnconfirmed, run.Primary!.Cause.Reason);
            }
            catch (Exception error)
            {
                diagnostic.TryWrite(raceOutput.WriteLine, attempt, waitSite, goal, cancel, error);
                throw;
            }
        }
    }

    // These helpers intentionally remain blocked while the owner must progress.
    // Do not consume the shared pool needed by provider/monitor continuations.
    private static Task RunBlockingFixture(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static Task<T> RunBlockingFixture<T>(Func<T> action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    [Theory] [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task FirstCaptureSuccessErrorSurvivesStalledSecondCapture(bool deadline, bool invalidEnvelope)
    {
        var real = new CancellationArbitrationClock(); var condition = new CancellationArbitrationClock();
        var run = new RunSession(Limits(), real, condition, Condition());
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                condition.Driver.Value = true; var reads = 0;
                void Read()
                { if (++reads == (invalidEnvelope ? 1 : 3)) { entered.TrySetResult(); resume.Wait(); } else condition.OnDriverRead = Read; }
                condition.OnDriverRead = Read;
                owner.EvaluateCapturedUnits([new(invalidEnvelope ? TimeSpan.FromTicks(-1) : TimeSpan.Zero, Unit("\"bad\""), Unit("false")), new(TimeSpan.Zero, Unit(), Unit("false"))], [], false);
                return false;
            }
            finally { condition.Driver.Value = false; ended.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultStatus.Invalid, outcome.Primary.Status);
            Assert.Equal(RunReason.ObservationContractViolation, outcome.Primary.Cause.Reason);
            Assert.Contains(outcome.Events, item => item.Reason == (deadline ? RunReason.MaxDuration : RunReason.Cancelled));
            Assert.False(ended.Task.IsCompleted); resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events); Assert.False(run.GoalVerified);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory]
    [InlineData("action")] [InlineData("planner")] [InlineData("observation")] [InlineData("elapsed")]
    public async Task InterruptedResultConfirmationCannotAcceptLateReceipt(string kind)
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunSession.ApprovedOperation? operation = null;
        var executing = RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                real.Driver.Value = true;
                if (kind == "planner")
                {
                    var permit = owner.RequestPlanner()!;
                    real.OnDriverRead = () => { entered.TrySetResult(); resume.Wait(); };
                    confirmed.TrySetResult(permit.ConfirmResponse());
                }
                else
                {
                    operation = kind == "action" ? owner.ApproveOperation(1, TimeSpan.FromSeconds(1)) :
                        kind == "elapsed" ? owner.RequestPlanner()!.ApproveElapsedWait(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)) :
                        owner.ApproveObservation(TimeSpan.FromSeconds(1));
                    if (kind == "action") { operation!.BeginDispatch(0); operation.Actions!.ConfirmSent(0); }
                    if (kind == "elapsed") condition.At(10);
                    real.OnDriverRead = () => { entered.TrySetResult(); resume.Wait(); };
                    confirmed.TrySetResult(operation!.ConfirmResult());
                }
                return true;
            }
            finally { real.Driver.Value = false; ended.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(kind == "action" ? RunReason.ActionUnconfirmed : RunReason.Cancelled, outcome.Primary.Cause.Reason);
            Assert.False(ended.Task.IsCompleted); resume.Set();
            Assert.False(await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (operation is not null) { Assert.False(operation.IsOpen); Assert.False(operation.ResultConfirmed); }
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task ValidatedDeadlineCannotLoseToGoalWhileTimeoutCandidatesAreMaterializing()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, Condition());
        run.BeginPreparation(); var capture = run.ArmRunningBoundary();
        var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
            new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/deadline", true);
        using var resumeProbe = new ManualResetEventSlim(); using var resumeCandidates = new ManualResetEventSlim();
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidatesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        boundary.InitialLifecycleFailure = () =>
        { if (Interlocked.Exchange(ref armed, 0) == 1) { probeEntered.TrySetResult(); resumeProbe.Wait(); } return null; };
        run.BeginRunning(boundary);
        IEnumerable<RunEvent> DeadlineCandidates()
        {
            candidatesEntered.TrySetResult(); resumeCandidates.Wait();
            yield return new(RunReason.MaxDuration, RunPhase.Execution, RunOrigin.Clock);
        }
        var goal = RunBlockingFixture(() => run.Evaluate(Unit(), TimeSpan.Zero)); Task<PrimaryResult?>? timeout = null;
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)); clock.At(5000);
            // FiniteOperation's actual executor timer reads this same validated port
            // before it delivers its timeout candidates to Evaluate.
            Assert.Equal(Limits().MaxDuration, run.ReadAuthoritativeReal());
            timeout = RunBlockingFixture(() => run.Evaluate(candidates: DeadlineCandidates()));
            await candidatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)); resumeProbe.Set();
            var primary = await goal.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultStatus.TimedOut, primary!.Status); Assert.Equal(RunReason.MaxDuration, primary.Cause.Reason);
            Assert.False(timeout.IsCompleted); var before = run.Events.ToArray();
            resumeCandidates.Set(); Assert.Same(primary, await timeout.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(before, run.Events);
        }
        finally
        {
            resumeProbe.Set(); resumeCandidates.Set(); await goal.WaitAsync(TimeSpan.FromSeconds(1));
            if (timeout is not null) await timeout.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(RunReason.ExecutionError)] [InlineData(RunReason.ActionsExhausted)]
    [InlineData(RunReason.DecisionsExhausted)] [InlineData(RunReason.RecoveryExhausted)]
    [InlineData(RunReason.GoalSatisfied)] [InlineData(RunReason.ExplorationFinished)]
    public async Task LateTokenConfirmationRetainsClosingLifecycleAndCompletionEvidence(RunReason expected)
    {
        var clock = new CancellationArbitrationClock();
        var limits = Limits(actions: expected == RunReason.ActionsExhausted ? 1 : 3,
            decisions: expected == RunReason.DecisionsExhausted ? 1 : 3,
            recovery: expected == RunReason.RecoveryExhausted ? 1 : 2);
        var run = new RunSession(limits, clock, new Clock(), expected == RunReason.GoalSatisfied ? Condition() : null);
        using var caller = new CancellationTokenSource(); using var resumeCallbacks = new ManualResetEventSlim();
        CancellationTokenRegistration blocker = default;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacksBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var evaluate = new ManualResetEventSlim();
        var fixtureElapsed = System.Diagnostics.Stopwatch.StartNew(); var phase = 0; long evaluatedAt = -1;
        var armed = false; var polls = 0;
        var executing = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, TimeSpan.Zero,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/late-token", true);
            boundary.InitialLifecycleFailure = () =>
                armed && ++polls > 1 && expected == RunReason.ExecutionError
                    ? new(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)) : null;
            return ValueTask.FromResult(boundary);
        }, (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            if (expected is RunReason.ActionsExhausted or RunReason.GoalSatisfied)
            {
                var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!; operation.BeginDispatch(0); operation.Actions!.ConfirmSent(0);
            }
            else if (expected is RunReason.DecisionsExhausted or RunReason.RecoveryExhausted)
                Assert.NotNull(owner.RequestPlanner(recovering: expected == RunReason.RecoveryExhausted));
            else if (expected == RunReason.ExplorationFinished) Assert.NotNull(owner.ApproveObservation(TimeSpan.FromSeconds(1)));
            blocker = caller.Token.Register(() => { callbacksBlocked.TrySetResult(); resumeCallbacks.Wait(); });
            // Keep the intentionally blocked fixture on its own worker. An async
            // pool continuation is not guaranteed to resume inside the unchanged
            // finite closing-evidence share. The provider still returns a pending Task.
            ready.TrySetResult(); Volatile.Write(ref phase, 1); evaluate.Wait(); armed = true;
            Volatile.Write(ref phase, 2); Interlocked.Exchange(ref evaluatedAt, fixtureElapsed.ElapsedTicks);
            owner.Evaluate(expected == RunReason.GoalSatisfied ? Unit() : null, expected == RunReason.GoalSatisfied ? TimeSpan.Zero : null,
                cancelled: false, executionComplete: expected == RunReason.ExplorationFinished); Volatile.Write(ref phase, 3); return true;
        })), caller.Token).AsTask();
        Task? cancelling = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(1)); cancelling = RunBlockingFixture(caller.Cancel);
            await callbacksBlocked.Task.WaitAsync(TimeSpan.FromSeconds(1)); evaluate.Set();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Contains(outcome.Events, item => item.Reason == RunReason.Cancelled);
            Assert.Contains(outcome.Events, item => item.Reason == expected);
            Assert.Equal(expected == RunReason.ExecutionError ? RunReason.ExecutionError :
                expected is RunReason.ActionsExhausted or RunReason.GoalSatisfied ? RunReason.ActionUnconfirmed : RunReason.Cancelled,
                outcome.Primary.Cause.Reason);
            if (expected == RunReason.ExecutionError) Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
            Assert.Equal(2, polls); Assert.False(cancelling.IsCompleted);
        }
        catch
        {
            try { raceOutput.WriteLine($"LateToken fixture expected={expected}; phase={Volatile.Read(ref phase)}; evaluatedAtTicks={Interlocked.Read(ref evaluatedAt)}; elapsedTicks={fixtureElapsed.ElapsedTicks}; polls={Volatile.Read(ref polls)}"); }
            catch (Exception) { /* Preserve the original assertion/stack. */ }
            throw;
        }
        finally { evaluate.Set(); resumeCallbacks.Set(); cancelling ??= RunBlockingFixture(caller.Cancel); await cancelling.WaitAsync(TimeSpan.FromSeconds(1)); blocker.Dispose(); }
    }

    [Theory]
    [InlineData(RunReason.ActionsExhausted, false)] [InlineData(RunReason.DecisionsExhausted, false)]
    [InlineData(RunReason.RecoveryExhausted, false)] [InlineData(RunReason.GoalSatisfied, false)]
    [InlineData(RunReason.ExplorationFinished, false)]
    [InlineData(RunReason.ActionsExhausted, true)] [InlineData(RunReason.DecisionsExhausted, true)]
    [InlineData(RunReason.RecoveryExhausted, true)] [InlineData(RunReason.GoalSatisfied, true)]
    [InlineData(RunReason.ExplorationFinished, true)]
    public async Task InterruptedEvaluationRetainsNewlyEligibleCompletionEvidence(RunReason expected, bool deadline)
    {
        var clock = new CancellationArbitrationClock();
        var limits = Limits(actions: expected == RunReason.ActionsExhausted ? 1 : 3,
            decisions: expected == RunReason.DecisionsExhausted ? 1 : 3,
            recovery: expected == RunReason.RecoveryExhausted ? 1 : 2);
        var run = new RunSession(limits, clock, new Clock(), expected == RunReason.GoalSatisfied ? Condition() : null);
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                clock.Driver.Value = true;
                if (expected is RunReason.ActionsExhausted or RunReason.GoalSatisfied)
                {
                    var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
                    operation.BeginDispatch(0);
                    if (expected == RunReason.GoalSatisfied) { operation.Actions!.ConfirmSent(0); Assert.Null(owner.Evaluate(Unit(), TimeSpan.Zero)); }
                    operation.Complete();
                }
                else if (expected is RunReason.DecisionsExhausted or RunReason.RecoveryExhausted)
                    owner.RequestPlanner(recovering: expected == RunReason.RecoveryExhausted)!.CompleteWithoutOperation();
                clock.OnDriverRead = () => { entered.TrySetResult(); resume.Wait(); };
                owner.Evaluate(executionComplete: expected == RunReason.ExplorationFinished); return false;
            }
            finally { clock.Driver.Value = false; ended.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) clock.At(limits.MaxDuration); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Contains(outcome.Events, item => item.Reason == (deadline ? RunReason.MaxDuration : RunReason.Cancelled));
            Assert.Contains(outcome.Events, item => item.Reason == expected);
            Assert.Equal(expected is RunReason.ActionsExhausted or RunReason.GoalSatisfied ? RunReason.ActionUnconfirmed : deadline ? RunReason.MaxDuration : RunReason.Cancelled,
                outcome.Primary.Cause.Reason);
            Assert.False(ended.Task.IsCompleted);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    private sealed class OverlappingReadClock : IClock
    {
        public long Ticks;
        public Action? BeforeReturn;
        public TimeSpan Elapsed
        {
            get
            {
                var sampled = TimeSpan.FromTicks(Interlocked.Read(ref Ticks));
                Interlocked.Exchange(ref BeforeReturn, null)?.Invoke();
                return sampled;
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OverlappingMonotonicClockReadsCannotReplaceWaitExpiryWithInvalid(bool condition)
    {
        var clock = new OverlappingReadClock();
        var run = new RunSession(Limits(), condition ? new Clock() : clock, condition ? clock : new Clock());
        run.BeginPreparation(); run.BeginRunning();
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.BeforeReturn = () => { entered.TrySetResult(); resume.Wait(); };
        var older = RunBlockingFixture(() =>
        {
            try { return condition ? run.ReadAuthoritativeCondition() : run.ReadAuthoritativeReal(); }
            catch (Exception exception) { run.RecordException(exception); return TimeSpan.Zero; }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clock.Ticks = TimeSpan.FromMilliseconds(1).Ticks;
            Assert.Equal(TimeSpan.FromMilliseconds(1), condition ? run.ReadAuthoritativeCondition() : run.ReadAuthoritativeReal());
            resume.Set(); var olderValue = await older.WaitAsync(TimeSpan.FromSeconds(1));
            run.Evaluate(candidates: [new(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Condition)]);
            Assert.Equal(ResultStatus.Failed, run.Primary!.Status);
            Assert.Equal(RunReason.WaitExpired, run.Primary.Cause.Reason);
            Assert.DoesNotContain(run.Events, item => item.Origin == RunOrigin.Clock && item.Reason == RunReason.InvalidContract);
            Assert.Equal(TimeSpan.FromMilliseconds(1), olderValue);
        }
        finally { resume.Set(); await older.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task CompletedFalseDriverRetainsFailureWhenItCancelsCaller()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        using var caller = new CancellationTokenSource();
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true),
            (_, _) => { caller.Cancel(); return ValueTask.FromResult(false); }, caller.Token);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status);
        Assert.Equal(RunReason.ExecutionError, outcome.Primary.Cause.Reason);
        Assert.Contains(outcome.Events, item => item.Reason == RunReason.Cancelled);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task StalledAuthorityProbeCannotPreventResourceRelease(bool dispatch, bool deadline)
    {
        var clock = new CancellationArbitrationClock(); var run = new RunSession(Limits(), clock, new Clock());
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0; var releases = 0; var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, clock, cleanup, (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, clock.Elapsed,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/authority", true);
            boundary.InitialLifecycleFailure = () =>
            { if (Volatile.Read(ref armed) == 1) { entered.TrySetResult(); resume.Wait(); } return null; };
            return ValueTask.FromResult(boundary);
        }, (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                var operation = dispatch ? owner.ApproveOperation(1, TimeSpan.FromSeconds(1)) : null;
                armed = 1;
                if (dispatch) operation!.BeginDispatch(0); else owner.ApproveOperation(1, TimeSpan.FromSeconds(1));
                return false;
            }
            catch (InvalidOperationException) when (owner.Primary is not null) { return false; }
            finally { ended.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) clock.At(Limits().MaxDuration); else caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, releases); Assert.False(ended.Task.IsCompleted);
            Assert.Equal(deadline ? RunReason.MaxDuration : RunReason.Cancelled, outcome.Primary.Cause.Reason);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
            Assert.Equal(0, run.Budget.Snapshot.Actions);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Theory] [InlineData(0, 1024)] [InlineData(256, 32)]
    public async Task InterruptedCloseProbeRetainsDrainedProviderDiagnostics(int depth, int evidenceLimit)
    {
        var clock = new CancellationArbitrationClock(); var limits = Limits();
        limits = new(maxDuration: limits.MaxDuration, preparationTimeout: limits.PreparationTimeout,
            cleanupTimeout: limits.CleanupTimeout, plannerTimeout: limits.PlannerTimeout, waitTimeout: limits.WaitTimeout,
            actionTimeout: limits.ActionTimeout, maxActions: limits.MaxActions, maxDecisions: limits.MaxDecisions,
            recoveryDecisions: limits.RecoveryDecisions, maxEvidenceItems: evidenceLimit);
        var run = new RunSession(limits, clock, new Clock());
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception source; try { throw new InvalidDataException("original provider"); } catch (Exception exception) { source = exception; }
        Exception wrapped = source; for (var index = 0; index < depth; index++) wrapped = new AggregateException(wrapped);
        var failure = new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host), wrapped);
        var polls = 0; var armed = false;
        var executing = RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            var boundary = capture.Certify(capture.RequestId, clock.Elapsed,
                new(TimeSpan.Zero, Unit("false"), Unit("false")), "source/diagnostics", true);
            boundary.InitialLifecycleFailure = () =>
            {
                if (armed && ++polls == 1) owner.PostProviderException(failure);
                else if (armed && polls == 2) { entered.TrySetResult(); resume.Wait(); }
                return null;
            };
            return ValueTask.FromResult(boundary);
        }, (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try { armed = true; owner.Evaluate(candidates: [new(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Condition)]); return false; }
            finally { ended.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException($"Closing probe not reached: polls={Volatile.Read(ref polls)}, state={run.State}, primary={run.Primary?.Cause}, executor={executing.Status}, driverEnded={ended.Task.IsCompleted}");
            }
            caller.Cancel();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(failure.Cause, outcome.Primary.Cause);
            Assert.Contains(outcome.Exceptions, item => item.Type == typeof(InvalidDataException).FullName && !string.IsNullOrEmpty(item.StackTrace));
            Assert.True(outcome.Exceptions.Count <= evidenceLimit);
            resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(outcome.Exceptions, run.Exceptions);
        }
        finally { resume.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    private sealed class CancellationArbitrationClock : IClock
    {
        public AsyncLocal<bool> Driver { get; } = new();
        public Action? OnDriverRead;
        public bool ActiveDriverRead;
        private long ticks;
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion)> timers = [];
        public TimeSpan Elapsed
        {
            get
            {
                if (Driver.Value && Interlocked.Exchange(ref OnDriverRead, null) is { } callback)
                { ActiveDriverRead = true; try { callback(); } finally { ActiveDriverRead = false; } }
                return TimeSpan.FromTicks(Interlocked.Read(ref ticks));
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            lock (timers) timers.Add((Elapsed + duration, completion));
            return new(completion.Task);
        }
        public void At(TimeSpan position)
        {
            Interlocked.Exchange(ref ticks, position.Ticks);
            lock (timers) foreach (var timer in timers.Where(t => t.Due <= position)) timer.Completion.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecutorInterruptionCannotFinalizeInsideDriverArbitration(bool confirmedSent, bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var cleanupStarted = new ManualResetEventSlim();
        PrimaryResult? cleanupPrimary = null;
        var driverEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanup.Register(CleanupStage.ResourceRelease, _ =>
        {
            cleanupPrimary = run.Primary;
            cleanupStarted.Set(); return ValueTask.FromResult(true);
        });
        var outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            real.Driver.Value = true;
            var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
            operation.BeginDispatch(0); if (confirmedSent) operation.Actions!.ConfirmSent(0);
            operation.Complete();
            // Evaluate has already moved ActionUnconfirmed into its local evidence unit
            // before reading the clock. Hold that owner unit while caller cancellation
            // gives the outer executor a bounded opportunity to attempt finalization.
            real.OnDriverRead = () =>
            {
                if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
                cleanupStarted.Wait(TimeSpan.FromMilliseconds(100));
            };
            owner.Evaluate(cancelled: !deadline);
            real.Driver.Value = false; driverEnded.TrySetResult();
            return false;
        })), caller.Token);
        Assert.NotNull(cleanupPrimary);
        Assert.Equal(ResultStatus.Failed, cleanupPrimary.Status);
        Assert.Equal(RunReason.ActionUnconfirmed, cleanupPrimary.Cause.Reason);
        Assert.Same(cleanupPrimary, outcome.Primary);
        Assert.Contains(outcome.Events, evidence => evidence.Reason == (deadline ? RunReason.MaxDuration : RunReason.Cancelled));
        Assert.Equal(1, run.Budget.Snapshot.Actions);
        // Non-joining finalization revokes the old unit rather than waiting for its
        // physical dependency. Returning late must not rewrite primary or the ledger.
        await driverEnded.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(outcome.Primary, run.Primary); Assert.Equal(outcome.Events, run.Events);
    }

    [Fact]
    public async Task CancellationArrivingInsideGoalUnitRetainsItsHigherPriority()
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition());
        using var caller = new CancellationTokenSource(); using var cleanupStarted = new ManualResetEventSlim();
        var cleanup = new OwnedCleanup(); PrimaryResult? observed = null;
        cleanup.Register(CleanupStage.ResourceRelease, _ =>
        { observed = run.Primary; cleanupStarted.Set(); return ValueTask.FromResult(true); });
        var outcome = await RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            real.Driver.Value = true;
            // The cancellation flag was sampled before this in-flight unit. A waiting
            // executor must post the new causal evidence before the unit freezes Goal.
            real.OnDriverRead = () => { caller.Cancel(); cleanupStarted.Wait(TimeSpan.FromMilliseconds(100)); };
            owner.Evaluate(Unit(), TimeSpan.Zero, cancelled: false);
            real.Driver.Value = false; return true;
        })), caller.Token);
        Assert.NotNull(observed); Assert.Equal(RunReason.Cancelled, observed.Cause.Reason);
        Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status); Assert.Same(observed, outcome.Primary);
        // An interrupted, unfinished Goal unit must not manufacture success evidence.
        Assert.Equal(run.GoalVerified, outcome.Events.Any(evidence => evidence.Reason == RunReason.GoalSatisfied));
    }

    [Fact]
    public async Task CertifiedInitialGoalRetainsCancellationBeforeConfirmation()
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition()); using var caller = new CancellationTokenSource();
        var executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (owner, _, _) =>
        {
            var capture = owner.ArmRunningBoundary();
            return ValueTask.FromResult(capture.Certify(capture.RequestId, real.Elapsed,
                new(condition.Elapsed, Unit(), Unit("false")), "synchronized/source/epoch", true));
        }, (_, _) => { executed = true; return ValueTask.FromResult(true); }, caller.Token, null, _ =>
        {
            real.Driver.Value = true;
            real.OnDriverRead = caller.Cancel;
        });
        Assert.False(executed); Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status);
        Assert.Equal(new(RunReason.Cancelled, RunPhase.Execution, RunOrigin.User), outcome.Primary.Cause);
        Assert.True(run.GoalVerified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledEvaluationCannotPreventInterruptionAndResourceRelease(bool deadline)
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition); var cleanup = new OwnedCleanup();
        using var caller = new CancellationTokenSource(); using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driverEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        cleanup.Register(CleanupStage.ResourceRelease, _ => { releases++; return ValueTask.FromResult(true); });
        var executing = RunExecutor.ExecuteAsync(run, real, cleanup, (_, _) => ValueTask.FromResult(true), (owner, _) => new ValueTask<bool>(RunBlockingFixture(() =>
        {
            try
            {
                real.Driver.Value = true;
                var operation = owner.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
                operation.BeginDispatch(0); operation.Complete();
                real.OnDriverRead = () => { entered.TrySetResult(); resume.Wait(); };
                owner.Evaluate(); return false;
            }
            finally { real.Driver.Value = false; driverEnded.TrySetResult(); }
        })), caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (deadline) real.At(Limits().MaxDuration); else caller.Cancel();
            // This is the intended bounded-finalization assertion: the dependency
            // remains stalled until finally, and resource release must happen first.
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, releases); Assert.False(driverEnded.Task.IsCompleted);
            Assert.Equal(RunReason.ActionUnconfirmed, outcome.Primary.Cause.Reason);
            var before = outcome.Events.ToArray();
            resume.Set(); await driverEnded.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(outcome.Primary, run.Primary); Assert.Equal(before, run.Events);
        }
        finally { resume.Set(); await driverEnded.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task DelayedCancellationCallbacksCannotPermitGoalConfirmation()
    {
        var real = new CancellationArbitrationClock(); var condition = new Clock();
        var run = new RunSession(Limits(), real, condition, Condition()); using var caller = new CancellationTokenSource();
        using var resumeCallbacks = new ManualResetEventSlim(); CancellationTokenRegistration blocker = default;
        var driverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacksBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = RunExecutor.ExecuteAsync(run, real, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true), async (owner, _) =>
        {
            // Later registration runs first, after CTS sets IsCancellationRequested.
            // Do not dispose on the driver: disposal would join the blocked callback.
            blocker = caller.Token.Register(() => { callbacksBlocked.TrySetResult(); resumeCallbacks.Wait(); });
            driverReady.TrySetResult(); await evaluate.Task;
            owner.Evaluate(Unit(), TimeSpan.Zero, cancelled: false); return true;
        }, caller.Token).AsTask();
        await driverReady.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancelling = RunBlockingFixture(caller.Cancel);
        try
        {
            await callbacksBlocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(caller.IsCancellationRequested); evaluate.TrySetResult();
            var outcome = await executing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status);
            Assert.Equal(RunReason.Cancelled, outcome.Primary.Cause.Reason);
            Assert.False(cancelling.IsCompleted);
        }
        finally { resumeCallbacks.Set(); await cancelling.WaitAsync(TimeSpan.FromSeconds(1)); blocker.Dispose(); }
    }
}
