using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Sources;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Exploration;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Explore owner timing")]
public sealed class OwnedProviderTaskTests
{
    private sealed class ReturnedFeed(Func<CancellationToken, ValueTask> wait) : IExploreObservationFeed
    {
        public ValueTask WaitForChangeAsync(CancellationToken token) => wait(token);
        public ValueTask<ExploreObservation> CaptureAsync(CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class SingleConsumptionFailure(Exception failure) : IValueTaskSource
    {
        internal int Reads;
        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Faulted;
        public void GetResult(short token)
        {
            if (++Reads != 1) throw new InvalidOperationException("ValueTaskConsumedTwice");
            throw failure;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => throw new InvalidOperationException("AlreadyCompleted");
    }
    private static IRunObservationFeed Adapt(IExploreObservationFeed source, RunSession run)
    {
        var type = typeof(ExploreDriver).GetNestedType("Feed", System.Reflection.BindingFlags.NonPublic)!;
        return (IRunObservationFeed)Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [source, run, (Action<ProgressObservation>)(_ => { }), (Func<TimeSpan?>)(() => null)], null)!;
    }
    private sealed class Clock : IClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
    private static readonly RunEvent Host = new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host);
    private static ConditionObservationUnit Unit(bool value) => new([KeyValuePair.Create("$",
        new ConditionLeafObservation("scope", true,
            [new("target", value ? "{\"type\":\"bool\",\"value\":true}" : "{\"type\":\"bool\",\"value\":false}")]))]);
    private static RunSession Running(bool goal = false, int evidence = 32)
    {
        var clock = new Clock();
        var success = goal ? PreparedCondition.Create(JsonNode.Parse("""{"kind":"assertion","read":{"target":{"source":"ui","selector":{"id":{"value":"owned"}}},"region":"standard","field":"visible","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject(), new(100, 1024)) : null;
        var run = new RunSession(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10, 10, 2, evidence), clock, clock, success);
        run.BeginPreparation(); run.BeginRunning(); return run;
    }
    private sealed class HeldObserverScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> tasks = new();
        internal readonly TaskCompletionSource Queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();
        protected override bool TryExecuteTaskInline(Task task, bool previouslyQueued) => false;
        protected override void QueueTask(Task task) { tasks.Enqueue(task); Queued.TrySetResult(); }
        internal void Release() { while (tasks.TryDequeue(out var task)) TryExecuteTask(task); }
    }
    private static Task Observe(Task source, TaskScheduler scheduler) => source.ContinueWith(task => _ = task.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, scheduler);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownOwnedFaultCompetesWithGoalWithoutNotificationDelivery(bool delivered)
    {
        var run = Running(goal: true); var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new HeldObserverScheduler(); var observer = Observe(source.Task, scheduler);
        var original = new InvalidDataException("private-provider-detail");
        run.TrackOwnedProviderTask(source.Task); source.SetException(new RunFailureException(Host, original));
        try
        {
            await scheduler.Queued.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (delivered) { scheduler.Release(); await observer.WaitAsync(TimeSpan.FromSeconds(1)); }
            Assert.True(source.Task.IsFaulted); Assert.True(run.ActionsClosing);
            run.Evaluate(Unit(true), TimeSpan.Zero);
            Assert.Equal(Host, run.Primary!.Cause);
            Assert.Contains(run.Events, item => item.Reason == RunReason.GoalSatisfied);
            Assert.Single(run.Exceptions, item => item.Type == typeof(InvalidDataException).FullName);
            var snapshot = run.CapturePrimary();
            // Duplicate registration/delivery, including after the drain, cannot
            // append the same terminal cause or alter the frozen snapshot.
            run.TrackOwnedProviderTask(source.Task); run.TrackOwnedProviderTask(source.Task);
            scheduler.Release(); await observer.WaitAsync(TimeSpan.FromSeconds(1)); run.Evaluate();
            Assert.Single(run.Events, item => item == Host);
            Assert.Equal(snapshot.Events, run.Events); Assert.Equal(snapshot.Exceptions, run.Exceptions);
        }
        finally { scheduler.Release(); }
    }
    [Fact]
    public void KnownOwnedFaultClosesInputAdmissionBeforeOwnerDrain()
    {
        var run = Running(); var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.TrackOwnedProviderTask(source.Task); source.SetException(new RunFailureException(Host));
        Assert.True(run.ActionsClosing);
        Assert.Null(run.ApproveOperation(1, TimeSpan.FromSeconds(1)));
        run.Evaluate(); Assert.Equal(Host, run.Primary!.Cause); Assert.Equal(0, run.Budget.Snapshot.Actions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulAndCancelledTasksReleaseRegistrationCapacityWithoutHostFailure(bool cancelled)
    {
        var run = Running(); var sources = Enumerable.Range(0, run.Limits.MaxEvidenceItems)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        foreach (var source in sources)
        {
            run.TrackOwnedProviderTask(source.Task);
            if (cancelled) source.SetCanceled(); else source.SetResult();
        }
        run.Evaluate(); Assert.Null(run.Primary); Assert.Empty(run.Exceptions); Assert.False(run.ActionsClosing);
        var pending = Enumerable.Range(0, run.Limits.MaxEvidenceItems)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        foreach (var source in pending) run.TrackOwnedProviderTask(source.Task);
        run.Evaluate(); Assert.Null(run.Primary); Assert.False(run.ActionsClosing);
        foreach (var source in pending) source.SetCanceled();
        run.Evaluate(executionComplete: true); Assert.Equal(RunReason.ExplorationFinished, run.Primary!.Cause.Reason);
    }
    [Fact]
    public void DuplicateActiveTaskDoesNotSpendCapacityOrRepeatTerminalCause()
    {
        var run = Running(); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i <= run.Limits.MaxEvidenceItems; i++) run.TrackOwnedProviderTask(first.Task);
        var rest = Enumerable.Range(1, run.Limits.MaxEvidenceItems - 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        foreach (var source in rest) run.TrackOwnedProviderTask(source.Task);
        run.Evaluate(); Assert.Null(run.Primary); Assert.False(run.ActionsClosing);
        first.SetException(new RunFailureException(Host)); run.Evaluate();
        run.TrackOwnedProviderTask(first.Task); run.Evaluate();
        Assert.Equal(Host, run.Primary!.Cause); Assert.Single(run.Events, item => item == Host);
        Assert.Single(run.Exceptions, item => item.Type == typeof(RunFailureException).FullName);
        foreach (var source in rest) source.SetCanceled();
    }
    [Fact]
    public async Task PendingAtCloseAndFaultAfterCloseRemainObservationOnly()
    {
        var run = Running(); var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.TrackOwnedProviderTask(source.Task); var observer = Observe(source.Task, TaskScheduler.Default);
        run.Evaluate(executionComplete: true); var snapshot = run.CapturePrimary();
        source.SetException(new RunFailureException(Host, new InvalidDataException()));
        await observer.WaitAsync(TimeSpan.FromSeconds(1));
        run.TrackOwnedProviderTask(source.Task); run.Evaluate();
        Assert.Equal(snapshot.Primary, run.Primary); Assert.Equal(snapshot.Events, run.Events); Assert.Equal(snapshot.Exceptions, run.Exceptions);
    }
    [Fact]
    public void UnownedFaultDoesNotBecomeRunEvidence()
    {
        var run = Running(goal: true); var unowned = Task.FromException(new RunFailureException(Host));
        _ = unowned.Exception;
        run.Evaluate(Unit(true), TimeSpan.Zero);
        Assert.Equal(RunReason.GoalSatisfied, run.Primary!.Cause.Reason); Assert.Empty(run.Exceptions);
    }
    [Fact]
    public void RegistrationCapacityOverflowFailsClosed()
    {
        var run = Running(goal: true); var sources = Enumerable.Range(0, run.Limits.MaxEvidenceItems + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        foreach (var source in sources) run.TrackOwnedProviderTask(source.Task);
        Assert.True(run.ActionsClosing);
        run.Evaluate(Unit(true), TimeSpan.Zero);
        Assert.Equal(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), run.Primary!.Cause);
        Assert.Contains(run.Events, item => item.Reason == RunReason.GoalSatisfied);
        foreach (var source in sources) source.SetCanceled();
    }
    [Fact]
    public void OwnedFaultQueueOverflowUsesExistingFailClosedEvidence()
    {
        var run = Running(goal: true);
        for (int i = 0; i < run.Limits.MaxEvidenceItems; i++) run.PostException(new IOException());
        var fault = Task.FromException(new RunFailureException(Host)); run.TrackOwnedProviderTask(fault);
        run.Evaluate(Unit(true), TimeSpan.Zero);
        Assert.Equal(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Runner), run.Primary!.Cause);
        Assert.True(run.Exceptions.Count <= run.Limits.MaxEvidenceItems); Assert.True(run.Events.Count <= run.Limits.MaxEvidenceItems);
    }
    [Fact]
    public void InterruptedClosingSampleCollectsKnownOwnedFaultBeforeFreeze()
    {
        var run = Running(); var original = new InvalidDataException();
        var fault = Task.FromException(new RunFailureException(Host, original)); run.TrackOwnedProviderTask(fault);
        run.FinalizeInterruptedExecution([], cancelled: true, executionComplete: false, new IOException("interruption"));
        Assert.Equal(Host, run.Primary!.Cause);
        Assert.Contains(run.Events, item => item.Reason == RunReason.Cancelled);
        Assert.Single(run.Exceptions, item => item.Type == typeof(InvalidDataException).FullName);
        var snapshot = run.CapturePrimary(); run.TrackOwnedProviderTask(fault); run.Evaluate();
        Assert.Single(run.Events, item => item == Host); Assert.Equal(snapshot.Exceptions, run.Exceptions);
    }
    [Fact]
    public async Task RawReturnedFaultIsCollectedWhileClassificationWrapperIsHeld()
    {
        var run = Running(goal: true); var source = new TaskCompletionSource();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidDataException();
        async Task Classified()
        {
            try { await source.Task; }
            catch { reached.SetResult(); await hold.Task; throw; }
        }
        var wrapper = Classified();
        // source.Task is the actual provider return, distinct from its wrapper.
        run.TrackOwnedProviderTask(source.Task, Host, default);
        source.SetException(original); await reached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Assert.True(source.Task.IsFaulted); Assert.False(wrapper.IsCompleted);
            Assert.True(run.ActionsClosing); run.Evaluate(Unit(true), TimeSpan.Zero);
            Assert.Equal(Host, run.Primary!.Cause);
            Assert.Single(run.Exceptions, e => e.Type == typeof(InvalidDataException).FullName);
        }
        finally { hold.SetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => wrapper);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RawCancellationUsesActualExceptionTokenAndRequestedState(bool cancelledTask, bool matching)
    {
        using var expected = new CancellationTokenSource(); using var foreign = new CancellationTokenSource();
        expected.Cancel(); foreign.Cancel();
        var actual = matching ? expected.Token : foreign.Token;
        Task task = cancelledTask ? Task.FromCanceled(actual) : Task.FromException(new OperationCanceledException(actual));
        var run = Running(goal: true); run.TrackOwnedProviderTask(task, Host, expected.Token);
        Assert.Equal(!matching, run.ActionsClosing);
        run.Evaluate(Unit(true), TimeSpan.Zero);
        Assert.Equal(matching ? RunReason.GoalSatisfied : RunReason.ExecutionError, run.Primary!.Cause.Reason);
        if (!matching) Assert.Equal(Host, run.Primary.Cause);
    }
    [Fact]
    public void CancellationClassificationIsResampledAfterExceptionRead()
    {
        using var token = new CancellationTokenSource(); var source = new TaskCompletionSource();
        source.SetCanceled(token.Token); var run = Running(goal: true);
        run.TrackOwnedProviderTask(source.Task, Host, token.Token);
        Assert.True(run.ActionsClosing); token.Cancel(); Assert.False(run.ActionsClosing);
        run.Evaluate(Unit(true), TimeSpan.Zero); Assert.Equal(RunReason.GoalSatisfied, run.Primary!.Cause.Reason);
        Assert.Empty(run.Exceptions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawAwaitPreservesFirstExceptionOrOriginalAggregate(bool aggregate)
    {
        var run = Running(); var first = new InvalidDataException(); var second = new FormatException();
        var source = new TaskCompletionSource();
        if (aggregate) source.SetException(new AggregateException(first, second)); else source.SetException([first, second]);
        run.TrackOwnedProviderTask(source.Task, Host, default); run.Evaluate();
        Assert.Equal(Host, run.Primary!.Cause);
        Assert.Single(run.Exceptions, e => e.Type == typeof(InvalidDataException).FullName);
        Assert.Equal(aggregate, run.Exceptions.Any(e => e.Type == typeof(FormatException).FullName));
        Assert.Equal(aggregate, run.Exceptions.Any(e => e.Type == typeof(AggregateException).FullName));
    }
    [Fact]
    public void SameReturnedTaskRetainsDistinctCancellationScopes()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        var task = Task.FromCanceled(token.Token); var run = Running();
        run.TrackOwnedProviderTask(task, Host, token.Token);
        run.TrackOwnedProviderTask(task, Host, default);
        Assert.True(run.ActionsClosing); run.Evaluate(); Assert.Equal(Host, run.Primary!.Cause);
    }
    [Fact]
    public void ExplicitCandidateAndOwnedPendingCauseMergeWithoutFalseOverflow()
    {
        var run = Running(); var task = Task.FromException(new InvalidDataException());
        run.TrackOwnedProviderTask(task, Host, default);
        // Keep the existing minimum32 and fill exactly that capacity with explicit
        // candidates. Only the duplicate owned contribution must be suppressed;
        // caller-supplied ordering/multiplicity is not rewritten by this merge.
        var candidates = Enumerable.Repeat(Host, run.Limits.MaxEvidenceItems).ToArray();
        run.Evaluate(candidates: candidates);
        Assert.Equal(Host, run.Primary!.Cause); Assert.Equal(candidates, run.Events);
        Assert.DoesNotContain(run.Events, e => e.Origin == RunOrigin.Runner);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualFeedRegistersReturnedTaskAndPreservesSourceWinningCause(bool synchronousThrow)
    {
        var run = Running(); var original = new InvalidDataException();
        var classified = new RunFailureException(Host, original); var returned = Task.FromException(classified);
        var calls = 0;
        var feed = Adapt(new ReturnedFeed(_ => { calls++; if (synchronousThrow) throw classified; return new(returned); }), run);
        var thrown = await Assert.ThrowsAsync<RunFailureException>(() => feed.WaitForChangeAsync(default).AsTask());
        Assert.Same(classified, thrown); Assert.Equal(1, calls); Assert.True(run.ActionsClosing);
        run.Evaluate(candidates: [thrown.Cause]);
        Assert.Equal(Host, run.Primary!.Cause); Assert.Single(run.Events, e => e == Host);
        Assert.Single(run.Exceptions, e => e.Type == typeof(InvalidDataException).FullName);
    }
    [Fact]
    public async Task ActualFeedConsumesProviderValueTaskOnlyOnce()
    {
        var run = Running(); var original = new InvalidDataException();
        var value = new SingleConsumptionFailure(original);
        var feed = Adapt(new ReturnedFeed(_ => new ValueTask(value, 0)), run);
        var thrown = await Assert.ThrowsAsync<RunFailureException>(() => feed.WaitForChangeAsync(default).AsTask());
        Assert.Same(original, thrown.InnerException); Assert.Equal(1, value.Reads);
        run.Evaluate(candidates: [thrown.Cause]); Assert.Equal(Host, run.Primary!.Cause);
        Assert.Equal(1, value.Reads);
        Assert.Single(run.Exceptions, e => e.Type == typeof(InvalidDataException).FullName);
    }
}
