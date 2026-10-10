using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Runner.Replay;

/// <summary>Composes Replay with the existing execution owner, preparation certificate,
/// arbitration and cleanup. No Planner, route repair, snapshot restore or input scheduler.</summary>
public sealed class ReplayDriver
{
    private int used, dispatched, completed, checkpoints;
    private bool planCompleted;
    public async ValueTask<ReplayOutcome> ExecuteAsync(ResolvedReplay replay, RunLimits limits,
        IClock realClock, IClock conditionClock, OwnedCleanup cleanup, IReplayPlayback playback,
        Func<RunSession, OwnedCleanup, CancellationToken, ValueTask<ReplayPreparedHost>> prepare,
        CancellationToken cancellationToken = default,
        Func<RunSnapshot, CancellationToken, ValueTask<bool>>? confirmPrimary = null)
    {
        ArgumentNullException.ThrowIfNull(replay); ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(prepare);
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("ReplayAlreadyUsed");
        var scenario = replay.CopyScenario();
        // RunLimits retains its positive action-limit contract; a zero Scenario ceiling
        // is enforced below before any action approval, including an omitted onGoal plan.
        if ((scenario.Constraints.MaxActions != 0 && limits.MaxActions > scenario.Constraints.MaxActions) || limits.MaxDuration.TotalMilliseconds > scenario.Constraints.MaxDurationMilliseconds)
            throw new ArgumentException("ReplayLimitsMismatch");
        var run = new RunSession(limits, realClock, conditionClock, replay.Success, replay.Failure, replay.Completion);
        IReplayObservationFeed? feed = null;
        var outcome = await RunExecutor.ExecuteAsync(run, realClock, cleanup, async (owner, owned, token) =>
        {
            var prepared = await prepare(owner, owned, token).ConfigureAwait(false);
            feed = prepared.Host.Feed as IReplayObservationFeed
                ?? throw Failure(RunReason.InvalidContract, RunOrigin.Contract, RunPhase.Preparation);
            if (replay.Initial is { } initial)
            {
                if (prepared.Initial is null) throw Failure(RunReason.ObservationContractViolation, RunOrigin.Contract, RunPhase.Preparation);
                var result = initial.Start(owner.AuthoritativeConditionClock, prepared.Host.Boundary.InitialObservation.CapturedAt)
                    .EvaluateAt(prepared.Initial, prepared.Host.Boundary.InitialObservation.CapturedAt);
                if (result.Evaluation.Error != EvaluationError.None) throw Failure(RunReason.ObservationContractViolation, RunOrigin.Contract, RunPhase.Preparation);
                if (result.Evaluation.Truth != TruthValue.True) throw Failure(RunReason.GoalImpossible, RunOrigin.Condition, RunPhase.Preparation);
            }
            return prepared.Host.Boundary;
        }, async (owner, token) =>
        {
            var cursor = 0;
            RunSession.ApprovedOperation? active = null;
            try
            {
                while (owner.Primary is null)
                {
                    foreach (var point in replay.Checkpoints.Where(p => p.BeforeStep == cursor))
                    {
                        active?.Complete();
                        active = owner.ApproveObservation(Min(point.Timeout, limits.WaitTimeout));
                        if (active is null) return false;
                        if (!await CheckpointAsync(owner, feed!, point.Condition, active, point.Timeout, token).ConfigureAwait(false)) return false;
                        checkpoints++;
                        active.Complete(); active = null;
                        if (owner.Primary is not null) return false;
                    }
                    if (cursor == replay.StepCount)
                    {
                        // Keep the final approved opportunity open through final checkpoints and
                        // finite Goal observation, even when the last action closed new approvals.
                        planCompleted = true;
                        if (!owner.GoalVerified && replay.Success is not null)
                        {
                            active?.Complete(); active = owner.ApproveObservation(limits.WaitTimeout);
                            if (active is not null)
                            {
                                var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                await RunMonitor.AwaitAsync(owner, realClock, conditionClock, new GoalFeed(feed!, owner, reached),
                                    waitToken => new ValueTask<bool>(reached.Task.WaitAsync(waitToken)), _ => [], token,
                                    _ => active.ConfirmResult()).ConfigureAwait(false);
                            }
                        }
                        active?.Complete();
                        return true;
                    }
                    active?.Complete(); active = null;
                    if (scenario.Constraints.MaxActions == 0)
                        throw Failure(RunReason.ActionsExhausted, RunOrigin.Budget);
                    // At most 1000 requests per Gua segment. Coverage remains contiguous and ordered.
                    var end = Math.Min(cursor + 1000, replay.Checkpoints.Where(p => p.BeforeStep > cursor)
                        .Select(p => p.BeforeStep).Append(replay.StepCount).Min());
                    var batch = replay.Batch(cursor, end, Min(limits.ActionTimeout, limits.WaitTimeout));
                    if (playback.Check(batch) != ReplayCheck.Approved)
                        throw Failure(RunReason.ActionFailed, RunOrigin.Host);
                    active = owner.ApproveOperation(batch.Count, limits.WaitTimeout);
                    if (active is null) return false;
                    using var calls = new ReplayCalls(owner, active, () => playback.Check(batch, starting: false), () => dispatched++);
                    var pump = new PumpFeed(feed!, calls, owner);
                    var result = await RunMonitor.AwaitAsync(owner, realClock, conditionClock, pump,
                        workToken => playback.PlayAsync(batch, calls, workToken), receipt => ReceiptEvents(receipt, batch.Count,
                            active.Actions!.Deliveries.Count(x => x is DeliveryState.Sent or DeliveryState.Uncertain), active.DispatchClosedByGoal), token,
                        receipt =>
                        {
                            if (receipt?.OriginalException is { } exception) owner.RecordException(exception);
                            if (receipt is not null && receipt.CompletedSteps == active.Actions!.Deliveries.Count(x => x is DeliveryState.Sent or DeliveryState.Uncertain)
                                && receipt.Status != ReplayReceiptStatus.Unconfirmed)
                                active.ConfirmResult();
                        }).ConfigureAwait(false);
                    if (result.Value is { } received && received.CompletedSteps >= 0 && received.CompletedSteps <= batch.Count &&
                        received.CompletedSteps <= active.Actions!.Deliveries.Count(x => x is DeliveryState.Sent or DeliveryState.Uncertain))
                        completed += received.CompletedSteps;
                    if (result.Value is { Status: ReplayReceiptStatus.Succeeded, NeutralConfirmed: true } settled && active.ResultConfirmed)
                    {
                        cursor += settled.CompletedSteps;
                        if (cursor == replay.StepCount && !replay.Checkpoints.Any(p => p.BeforeStep == cursor)) planCompleted = true;
                    }
                    if (!result.Completed || result.Value?.Status != ReplayReceiptStatus.Succeeded || !active.ResultConfirmed) return false;
                    cursor = end;
                    if (cursor < replay.StepCount && !replay.Checkpoints.Any(p => p.BeforeStep == cursor))
                    { active.Complete(); active = null; }
                }
                return false;
            }
            finally { active?.Complete(); }
        }, cancellationToken, confirmPrimary).ConfigureAwait(false);
        return new(outcome, new(replay.StepCount, dispatched, completed, checkpoints, planCompleted,
            outcome.Primary.Status == ResultStatus.Passed && !planCompleted ? dispatched : null, run.GoalVerified));
    }

    private static IReadOnlyList<RunEvent> ReceiptEvents(ReplayReceipt? receipt, int count, int actual, bool goalClosed)
    {
        if (receipt is null || !Enum.IsDefined(receipt.Status) || receipt.CompletedSteps < 0 || receipt.CompletedSteps > count || receipt.CompletedSteps > actual ||
            receipt.Status == ReplayReceiptStatus.Succeeded && receipt.CompletedSteps != (goalClosed ? actual : count))
            return [new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)];
        if (receipt.Status == ReplayReceiptStatus.Unconfirmed) return [new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host)];
        if (receipt.Status == ReplayReceiptStatus.Failed || !receipt.NeutralConfirmed) return [new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host)];
        return [];
    }

    private static async ValueTask<bool> CheckpointAsync(RunSession run, IReplayObservationFeed feed,
        PreparedCondition condition, RunSession.ApprovedOperation operation, TimeSpan timeout, CancellationToken token)
    {
        var session = condition.Start(run.AuthoritativeConditionClock);
        var completion = new TaskCompletionSource<IReadOnlyList<RunEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = Min(run.AuthoritativeRealClock.Elapsed + timeout, operation.Deadline);
        var checkpointFeed = new CheckpointFeed(feed, run, condition, session, completion, boundary);
        var result = await RunMonitor.AwaitAsync(run, run.AuthoritativeRealClock, run.AuthoritativeConditionClock, checkpointFeed,
            waitToken => new ValueTask<IReadOnlyList<RunEvent>>(completion.Task.WaitAsync(waitToken)), events => events, token,
            events => { if (events.Count == 0) operation.ConfirmResult(); }).ConfigureAwait(false);
        return operation.ResultConfirmed && completion.Task.IsCompletedSuccessfully && completion.Task.Result.Count == 0 &&
            (result.Completed || run.Primary?.Status == ResultStatus.Passed);
    }
    private sealed class CheckpointFeed(IReplayObservationFeed feed, RunSession run, PreparedCondition condition, ConditionSession session,
        TaskCompletionSource<IReadOnlyList<RunEvent>> completion, TimeSpan deadline) : IRunObservationFeed, IRunConditionSchedule
    {
        private TimeSpan? next;
        public TimeSpan? NextConditionEvaluationAt => next;
        public async ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        {
            ReplayObservation unit;
            try
            {
                unit = await FiniteOperation.RunUntilAsync(run.AuthoritativeRealClock, deadline,
                    captureToken => feed.CaptureAsync(condition, captureToken), token, run.PostException).ConfigureAwait(false);
            }
            catch (Exception exception) when (FiniteOperation.IsDeadline(exception))
            {
                throw new RunFailureException(new(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Condition), exception);
            }
            ConditionEvaluation evaluated;
            try
            {
                if (unit?.Run is null || unit.Checkpoint is null) throw new ArgumentException("ReplayObservationMissing");
                evaluated = session.EvaluateAt(unit.Checkpoint, unit.Run.CapturedAt);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new RunFailureException(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract), exception);
            }
            next = evaluated.NextEvaluationAt;
            IReadOnlyList<RunEvent>? events = evaluated.Evaluation.Error != EvaluationError.None
                ? [new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)]
                : evaluated.Evaluation.Truth == TruthValue.True ? []
                : evaluated.Completion == ConditionCompletion.Expired || run.AuthoritativeRealClock.Elapsed >= deadline
                    ? [new(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Condition)] : null;
            if (events is not null)
            {
                // Private checkpoint validation belongs to this same captured unit. Post its
                // terminal evidence before OnGoal can arbitrate, independent of task delivery.
                foreach (var evidence in events) run.PostProviderException(new RunFailureException(evidence));
                completion.TrySetResult(events);
            }
            return unit.Run;
        }
        public ValueTask WaitForChangeAsync(CancellationToken token) => WaitAnyAsync(run, token,
            feed.WaitForChangeAsync,
            cancellation => DelayValidatedAsync(run.AuthoritativeRealClock, deadline, cancellation));
    }
    private static async ValueTask DelayValidatedAsync(IClock clock, TimeSpan target, CancellationToken token)
    {
        await clock.DelayAsync(Positive(target - clock.Elapsed), token).ConfigureAwait(false);
        if (clock.Elapsed >= target) return;
        await FiniteOperation.DelayIndependentAsync(target - clock.Elapsed, token).ConfigureAwait(false);
        if (clock.Elapsed < target) throw new ClockProviderException();
    }
    private sealed class GoalFeed(IRunObservationFeed feed, RunSession run, TaskCompletionSource<bool> reached) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => feed.CaptureAsync(token);
        public ValueTask WaitForChangeAsync(CancellationToken token)
        {
            if (!run.GoalVerified) return feed.WaitForChangeAsync(token);
            reached.TrySetResult(true); return ValueTask.CompletedTask;
        }
    }
    private sealed class PumpFeed(IRunObservationFeed feed, ReplayCalls calls, RunSession run) : IRunObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) { calls.Pump(token); return feed.CaptureAsync(token); }
        public ValueTask WaitForChangeAsync(CancellationToken token) => WaitAnyAsync(run, token, feed.WaitForChangeAsync, calls.WaitAsync);
    }
    private static async ValueTask WaitAnyAsync(RunSession run, CancellationToken token, params Func<CancellationToken, ValueTask>[] waits)
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>(waits.Length);
        try
        {
            foreach (var wait in waits) tasks.Add(wait(cancelled.Token).AsTask());
            await (await Task.WhenAny(tasks).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            // Faults already present at the wake boundary are authoritative monitoring
            // failures. Faults caused only by cancellation of obsolete waits stay diagnostics.
            var failed = tasks.Where(task => task.IsFaulted).ToHashSet();
            foreach (var task in failed)
                foreach (var exception in task.Exception!.InnerExceptions)
                    run.PostProviderException(exception is RunFailureException ? exception :
                        new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host), exception));
            FiniteOperation.CancelSafely(cancelled, run.PostException);
            foreach (var task in tasks.Where(task => !failed.Contains(task)))
                _ = task.ContinueWith(fault =>
                {
                    foreach (var exception in fault.Exception!.InnerExceptions) run.PostProviderException(exception);
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static RunFailureException Failure(RunReason reason, RunOrigin origin, RunPhase phase = RunPhase.Execution)
        => new(new(reason, phase, origin));
}
