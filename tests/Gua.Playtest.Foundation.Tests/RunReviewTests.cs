using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    [Fact]
    public async Task SynchronousMonitorWorkFaultIsArbitratedAndPreserved()
    {
        var clock = new Clock(); var run = Running(clock); var feed = new Feed(() => new(TimeSpan.Zero, Unit(), Unit()));
        var result = await RunMonitor.AwaitAsync<int>(run, clock, clock, feed, _ => throw new IOException("private"), _ => []);
        Assert.False(result.Completed); Assert.Equal(RunReason.ExecutionError, run.Primary!.Cause.Reason);
        Assert.Equal("System.IO.IOException", run.Exceptions.Single().Type); Assert.Equal(ExecutionState.Completing, run.State);
    }
    [Theory]
    [InlineData(true, RunReason.PlannerTimeout)]
    [InlineData(false, RunReason.WaitExpired)]
    public void OverdueCompletionCannotRemoveDeadlineEvidence(bool planner, RunReason expected)
    {
        var clock = new Clock(); var run = Running(clock);
        var permit = planner ? run.RequestPlanner() : null;
        var wait = planner ? null : run.ApproveOperation(0, TimeSpan.FromSeconds(1));
        clock.At(1000);
        if (planner) permit!.CompleteWithoutOperation(); else wait!.Complete();
        Assert.Equal(expected, run.Evaluate(Unit(), clock.Elapsed)!.Cause.Reason);
    }
    [Fact]
    public void OversizedEvidenceFailsClosedAndRetainsPendingActionFailure()
    {
        var clock = new Clock(); var limits = new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 3, 3, 2, 32);
        var run = Running(clock, limits: limits); var operation = run.ApproveOperation(1, TimeSpan.FromSeconds(1))!;
        operation.BeginDispatch(0); operation.Complete();
        run.Evaluate(candidates: Enumerable.Repeat(Event(RunReason.ActionsExhausted), 100));
        Assert.Equal(RunReason.ActionUnconfirmed, run.Primary!.Cause.Reason); Assert.Equal(ExecutionState.Completing, run.State);
        Assert.InRange(run.Events.Count, 1, 32); Assert.Contains(run.Events, x => x.Reason == RunReason.ExecutionError);
    }
    [Fact]
    public async Task ReadyFailureWinsCancellationDuringBlockedCapture()
    {
        var clock = new Clock(); var run = Running(clock); using var cancel = new CancellationTokenSource();
        var monitoring = RunMonitor.AwaitAsync(run, clock, clock, new BlockedFeed(), _ => ValueTask.FromResult(1),
            _ => [Event(RunReason.ActionFailed)], cancel.Token).AsTask();
        cancel.Cancel(); Assert.False((await monitoring).Completed);
        Assert.Equal(RunReason.ActionFailed, run.Primary!.Cause.Reason); Assert.Contains(run.Events, x => x.Reason == RunReason.Cancelled);
    }
    [Fact]
    public async Task InternalCleanupCancellationIsFaultWithOriginalTypeAndStack()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup();
        cleanup.Register(CleanupStage.InputRelease, _ => throw new OperationCanceledException("provider internal"));
        var outcome = await cleanup.CompleteAsync(run, clock);
        Assert.Equal(11, outcome.ExitCode); Assert.DoesNotContain(outcome.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
        var issue = Assert.Single(outcome.PostProcessing);
        Assert.Equal(PostProcessingReason.InputReleaseUnconfirmed, issue.Reason);
        Assert.Equal("System.OperationCanceledException", issue.Exception!.Type); Assert.NotNull(issue.Exception.StackTrace);
    }
    [Fact]
    public async Task ConcurrentRegistrationIsEitherIncludedOrReliablyRejected()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero); var cleanup = new OwnedCleanup();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var released = false;
            var registration = Task.Run(async () =>
            {
                await start.Task;
                try { cleanup.Register(CleanupStage.ResourceRelease, _ => { released = true; return ValueTask.FromResult(true); }); return true; }
                catch (InvalidOperationException exception) { Assert.Equal("CleanupRegistrationClosed", exception.Message); return false; }
            });
            start.SetResult(); var outcome = await cleanup.CompleteAsync(run, clock); var accepted = await registration;
            Assert.Equal(accepted, released); Assert.True(outcome.PostProcessingComplete);
        }
    }
    [Fact]
    public async Task SynchronousProviderSetupDoesNotResetItsSafetyDeadline()
    {
        var clock = new Clock(); var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = FiniteOperation.RunAsync(clock, TimeSpan.FromSeconds(1), _ =>
        { clock.At(900); return new ValueTask<bool>(never.Task); }).AsTask();
        clock.At(1000); await Assert.ThrowsAsync<TimeoutException>(() => operation); never.SetResult(true);
    }
    private static RunStartBoundary Certify(RunSession run, Clock real, Clock condition,
        RunObservation? observation = null)
    {
        var request = run.ArmRunningBoundary();
        return request.Certify(request.RequestId, real.Elapsed, observation ?? new(condition.Elapsed, Unit(), Unit("false")), "subscription/epoch/cursor", true);
    }
    [Fact]
    public void CertifiedBoundaryRejectsPreparationHistoryWrongRequestAndUnreadyPrerequisites()
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        run.BeginPreparation(); real.At(200); condition.At(200); var request = run.ArmRunningBoundary();
        Assert.Throws<InvalidOperationException>(() => request.Certify(request.RequestId, TimeSpan.Zero,
            new(TimeSpan.Zero, Unit(), Unit()), "subscription", true));
        Assert.Throws<InvalidOperationException>(() => request.Certify("other-request", real.Elapsed,
            new(condition.Elapsed, Unit(), Unit()), "subscription", true));
        Assert.Throws<InvalidOperationException>(() => request.Certify(request.RequestId, real.Elapsed,
            new(condition.Elapsed, Unit(), Unit()), "subscription", false));
        Assert.Throws<InvalidOperationException>(() => run.BeginRunning());
    }
    [Fact]
    public void CertifiedBoundaryCannotCrossSessionsOrClockRegression()
    {
        var real = new Clock(); var condition = new Clock(); var run = new RunSession(Limits(), real, condition);
        run.BeginPreparation(); real.At(100); condition.At(100); var boundary = Certify(run, real, condition);
        var other = new RunSession(Limits(), real, condition); other.BeginPreparation();
        Assert.Throws<InvalidOperationException>(() => other.BeginRunning(boundary));
        condition.At(50); Assert.Throws<InvalidOperationException>(() => run.BeginRunning(boundary));
    }
    [Fact]
    public void CertifiedBoundaryKeepsOriginalRealBudgetAndExactWithinZeroCapture()
    {
        var real = new Clock(); var condition = new Clock();
        var node = JsonNode.Parse("""{"kind":"time","withinMilliseconds":0,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject();
        var goal = PreparedCondition.Create(node, new(10, 1000)); var run = new RunSession(Limits(), real, condition, goal);
        run.BeginPreparation(); real.At(200); condition.At(200);
        var boundary = Certify(run, real, condition, new(condition.Elapsed, Unit(time: true), Unit("false")));
        real.At(5200); condition.At(5200); run.BeginRunning(boundary);
        Assert.Equal(TimeSpan.FromMilliseconds(200), run.RunningOrigin);
        Assert.Equal(ResultStatus.TimedOut, run.Evaluate(boundary.InitialObservation.Success, boundary.InitialObservation.CapturedAt)!.Status);
        Assert.True(run.GoalVerified); Assert.Contains(run.Events, x => x.Reason == RunReason.GoalSatisfied);
    }
    [Fact]
    public void PreparationTimeCannotContributeToCertifiedForHold()
    {
        var real = new Clock(); var condition = new Clock();
        var node = JsonNode.Parse("""{"kind":"time","forMilliseconds":200,"withinMilliseconds":0,"condition":{"kind":"targets","target":{"source":"world"},"operator":"exists"}}""")!.AsObject();
        var goal = PreparedCondition.Create(node, new(10, 1000)); var run = new RunSession(Limits(), real, condition, goal);
        run.BeginPreparation(); real.At(500); condition.At(500);
        var boundary = Certify(run, real, condition, new(condition.Elapsed, Unit(time: true), Unit("false")));
        condition.At(600); real.At(600); run.BeginRunning(boundary);
        Assert.Null(run.Evaluate(boundary.InitialObservation.Success, boundary.InitialObservation.CapturedAt)); Assert.False(run.GoalVerified);
        var continuous = new ConditionObservationUnit([KeyValuePair.Create("$/condition", new ConditionLeafObservation("scope", true,
            [new ConditionTargetObservation("target", "{\"type\":\"bool\",\"value\":true}")], continuousFromPrevious: true))]);
        condition.At(699); real.At(699); Assert.Null(run.Evaluate(continuous, condition.Elapsed));
        condition.At(700); real.At(700); Assert.Equal(ResultStatus.Passed, run.Evaluate(continuous, condition.Elapsed)!.Status);
    }
    [Fact]
    public async Task BoundaryExecutorEvaluatesInitialFailureBeforeAnyPlannerWork()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock, Condition(), Condition()); var executed = false;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (session, _, _) =>
            ValueTask.FromResult(Certify(session, clock, clock, new(clock.Elapsed, Unit(), Unit()))), (_, _) =>
            { executed = true; return ValueTask.FromResult(true); });
        Assert.False(executed); Assert.Equal(RunReason.FailureCondition, outcome.Primary.Cause.Reason); Assert.Equal(ExecutionState.Finished, run.State);
    }
    [Fact]
    public async Task PrimarySnapshotRunsUnderCancelBeforeReleaseAndFailurePreservesPrimary()
    {
        var clock = new Clock(); var run = Running(clock); var primary = run.Evaluate(Unit(), TimeSpan.Zero)!;
        var cleanup = new OwnedCleanup(); var order = new List<string>(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        cleanup.Register(CleanupStage.ResourceRelease, token => { Assert.False(token.IsCancellationRequested); order.Add("release"); return ValueTask.FromResult(true); });
        var outcome = await cleanup.CompleteAsync(run, clock, cancel.Token, (snapshot, token) =>
        { Assert.Same(primary, snapshot.Primary); Assert.False(token.IsCancellationRequested); order.Add("primary"); return ValueTask.FromResult(false); });
        Assert.Equal(new[] { "primary", "release" }, order); Assert.Same(primary, outcome.Primary); Assert.Equal(11, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.PrimarySnapshotFailed);
    }
    [Fact]
    public async Task NoncooperativePrimarySnapshotIsBoundedAndLaterReleaseStillRuns()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(candidates: [Event(RunReason.ActionFailed)]);
        var cleanup = new OwnedCleanup(); var released = false; var never = new TaskCompletionSource<bool>();
        cleanup.Register(CleanupStage.InputRelease, _ => { released = true; return ValueTask.FromResult(true); });
        var finishing = cleanup.CompleteAsync(run, clock, confirmPrimary: (_, _) => new(never.Task)).AsTask();
        clock.At(500); var outcome = await finishing; Assert.True(released); Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(outcome.PostProcessing, x => x.Reason == PostProcessingReason.PrimarySnapshotFailed); never.SetResult(true);
    }
    [Theory]
    [InlineData(RunReason.PlannerOutputInvalid)]
    [InlineData(RunReason.PlannerUsageLimit)]
    [InlineData(RunReason.PlannerConnectionFailure)]
    public void NoncontinuablePlannerReasonsAreFailuresAndOutrankCancelDeadlineAndSuccess(RunReason reason)
    {
        var clock = new Clock(); var run = Running(clock); clock.At(5000);
        var result = run.Evaluate(Unit(), TimeSpan.Zero, [new(reason, RunPhase.Execution, RunOrigin.Planner)], cancelled: true);
        Assert.Equal(ResultStatus.Failed, result!.Status); Assert.Equal(reason, result.Cause.Reason); Assert.Equal(1, result.ExitCode);
    }
    [Fact]
    public async Task PostConfirmationCancellationAloneDoesNotInventIncompleteMandatoryWork()
    {
        var clock = new Clock(); var run = Running(clock); run.Evaluate(Unit(), TimeSpan.Zero);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await new OwnedCleanup().CompleteAsync(run, clock, cancel.Token);
        Assert.Contains(result.PostProcessing, x => x.Reason == PostProcessingReason.Cancelled);
        Assert.True(result.PostProcessingComplete); Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task TypedHostPreparationFailurePreservesClassificationAndOriginalEvidence()
    {
        var clock = new Clock(); var run = new RunSession(Limits(), clock, clock);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) =>
        {
            try { throw new IOException("private host detail"); }
            catch (IOException exception) { throw new RunFailureException(new(RunReason.ExecutionError, RunPhase.Preparation, RunOrigin.Host), exception); }
        }, (_, _) => throw new InvalidOperationException("execute must not run"));
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Equal(1, outcome.ExitCode); Assert.Contains(outcome.Exceptions, x => x.Type == "System.IO.IOException" && x.StackTrace is not null);
    }
}
