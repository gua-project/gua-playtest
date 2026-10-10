using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Runner.Exploration;

/// <summary>Running-only Explore orchestration, composed inside RunExecutor's execute callback.
/// Preparation, primary arbitration, budgets, approved dispatch and final cleanup remain their existing owners.</summary>
public static class ExploreDriver
{
    private sealed record State(ProgressDefinitions Definitions, ResourceLimits Limits, ProgressTracker Tracker);
    private static readonly ConditionalWeakTable<RunSession, State> States = new();

    private sealed class Feed(IExploreObservationFeed source, RunSession run, int capacity,
        Action<ProgressObservation> validateProgress, Func<TimeSpan?> nextProgressEvaluation) : IRunObservationFeed, IRunConditionSchedule
    {
        private readonly Queue<ExploreObservation> pending = new();
        private readonly object sync = new();
        public ExploreObservation? Latest;
        public ExploreCalls? Calls;
        public TimeSpan? NextConditionEvaluationAt => nextProgressEvaluation();
        public async ValueTask<RunObservation> CaptureAsync(CancellationToken token)
        {
            Calls?.Pump();
            ExploreObservation frame;
            try { frame = await source.CaptureAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token) { throw; }
            catch (Exception exception) { throw HostFailure(exception); }
            if (frame is null || frame.Run is null || frame.Progress is null || frame.Public is null ||
                frame.Progress.CapturedAt != frame.Run.CapturedAt)
                throw new RunFailureException(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract));
            // Validate the private part before the same capture can freeze Goal success.
            // Recovery still starts only after the complete approved work boundary.
            validateProgress(frame.Progress);
            lock (sync)
            {
                if (pending.Count >= capacity) throw new RunFailureException(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract));
                pending.Enqueue(frame); Latest = frame;
            }
            return frame.Run;
        }
        public ExploreObservation[] Drain()
        { lock (sync) { var result = pending.ToArray(); pending.Clear(); return result; } }
        public async ValueTask WaitForChangeAsync(CancellationToken token)
        {
            if (Calls is not { } calls) { await SourceChangeAsync(token).ConfigureAwait(false); return; }
            using var obsolete = CancellationTokenSource.CreateLinkedTokenSource(token);
            var change = SourceChangeAsync(obsolete.Token).AsTask();
            var pending = calls.WaitAsync(obsolete.Token).AsTask();
            try { await await Task.WhenAny(change, pending).ConfigureAwait(false); }
            finally
            {
                FiniteOperation.CancelSafely(obsolete, run.RecordException);
                // A losing notification's real failure remains causal evidence; it cannot be hidden by queue readiness.
                _ = change.ContinueWith(t =>
                {
                    foreach (var exception in t.Exception!.InnerExceptions) run.PostProviderException(exception);
                }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }
        private async ValueTask SourceChangeAsync(CancellationToken token)
        {
            try { await source.WaitForChangeAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token) { throw; }
            catch (Exception exception) { throw HostFailure(exception); }
        }
        private static RunFailureException HostFailure(Exception exception) => exception as RunFailureException ??
            new(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host), exception);
    }

    public static async ValueTask<bool> ExecuteRunningAsync(RunSession run, PlannerGate gate,
        IPlanner<PlannerInputDocument, PlannerReply> planner, IExploreObservationFeed observations,
        IExploreWork work, ProgressDefinitions definitions, ResourceLimits limits, ExploreObservation initial,
        Func<CancellationToken, ValueTask<bool>> releaseOwnedInputs, OwnedCleanup cleanup, CancellationToken cancellationToken = default,
        Action<ProgressSummary>? recordProgress = null)
    {
        if (run.State != ExecutionState.Running) throw new InvalidOperationException("RunStateInvalid");
        if (limits.RecoveryDecisionLimit != run.Limits.RecoveryDecisions) throw new ArgumentException("EffectiveLimitsMismatch");
        if (initial.Run.CapturedAt != initial.Progress.CapturedAt) throw new ArgumentException("ProgressCaptureInvalid");
        var state = States.GetValue(run, _ => new(definitions, limits,
            new ProgressTracker(definitions, limits, run.AuthoritativeConditionClock, initial.Progress)));
        if (!ReferenceEquals(state.Definitions, definitions) || state.Limits != limits)
            throw new ArgumentException("ExploreHistoryCannotBeReplaced");
        var tracker = state.Tracker;
        var feed = new Feed(observations, run, run.Limits.MaxEvidenceItems, progress =>
        {
            ProgressSummary sample;
            try { sample = tracker.ObserveSample(progress); }
            catch (ArgumentException exception) when (exception.Message == "ProgressCaptureInvalid")
            { throw new RunFailureException(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract), exception); }
            recordProgress?.Invoke(sample);
            if (sample.ObservationViolation)
                run.PostProviderException(new RunFailureException(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)));
        }, () => tracker.NextEvaluationAt) { Latest = initial };
        var summary = tracker.Initial;
        recordProgress?.Invoke(summary);
        if (summary.ObservationViolation)
            run.Evaluate(candidates: [new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)]);
        while (run.Primary is null)
        {
            run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
            if (run.Primary is not null) break;
            tracker.BeginRecovery();
            var request = gate.Begin(feed.Latest!.Public, tracker.Phase == ExplorePhase.Recovering);
            if (request is null)
            {
                run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
                if (run.Primary is null) run.Evaluate(candidates: [new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host)]);
                break;
            }
            var turn = await PlannerTurn.AwaitAsync(gate, request, run, run.AuthoritativeRealClock,
                run.AuthoritativeConditionClock, feed, planner, releaseOwnedInputs, cancellationToken).ConfigureAwait(false);
            if (turn.Interrupted)
            {
                if (!turn.OwnedInputsReleased)
                    cleanup.Register(CleanupStage.InputRelease, _ => ValueTask.FromResult(false));
                return false;
            }
            UpdateProgress();
            if (run.Primary is not null) return false;
            if (turn.Adoption?.Approved is not { } approved)
            {
                if (turn.Adoption?.RetryAllowed == true) continue;
                if (turn.Adoption?.TerminalEvent is { } terminal) run.Evaluate(candidates: [terminal]);
                return false;
            }
            var decision = approved.CopyDecision();
            var kind = decision["kind"]!.GetValue<string>();
            using var calls = new ExploreCalls(approved);
            feed.Calls = calls;
            ExploreReceipt? receipt = null;
            try
            {
                var result = await RunMonitor.AwaitAsync(run, run.AuthoritativeRealClock,
                    run.AuthoritativeConditionClock, feed,
                    token => ExecuteWorkAsync(decision, calls, token), Events, cancellationToken,
                    completed =>
                    {
                        if (completed.OriginalException is { } exception) run.RecordException(exception);
                        if (completed.Status == ExploreWorkStatus.Completed && completed.InputsNeutral)
                            approved.ConfirmResult();
                    }).ConfigureAwait(false);
                if (result.Completed) receipt = result.Value;
            }
            finally { feed.Calls = null; }
            var completion = approved.Complete();
            if (run.Primary is not null) return false;
            if (receipt is null) return false;
            if (!receipt.InputsNeutral || completion is PlannerFeedbackCode.SentUnconfirmed or PlannerFeedbackCode.PartialExecution)
            {
                run.Evaluate(candidates: [new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host)]); return false;
            }
            if (receipt.Status == ExploreWorkStatus.NotSent || completion == PlannerFeedbackCode.NotSent) continue;
            bool reported = kind == "finish" && decision["report"]?.GetValue<string>() is "stuck" or "cannotProceed";
            UpdateProgress(kind == "execute" ? decision : null, reported);
            // Only the machine arbiter establishes success; stuck is an entry to finite recovery.
            run.Evaluate(cancelled: cancellationToken.IsCancellationRequested);
            if (kind == "finish" && !reported) return true;
        }
        return false;

        void UpdateProgress(JsonObject? action = null, bool reported = false)
        {
            var frames = feed.Drain();
            if (frames.Length != 0)
            {
                summary = tracker.ObserveBoundary(frames[^1].Progress, action, reported);
                recordProgress?.Invoke(summary);
                if (summary.ObservationViolation)
                    run.Evaluate(candidates: [new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)]);
            }
        }

        async ValueTask<ExploreReceipt> ExecuteWorkAsync(JsonObject proposal, IExploreCalls ownerCalls, CancellationToken token)
        {
            var kind = proposal["kind"]!.GetValue<string>();
            if (kind == "finish") return new(ExploreWorkStatus.Completed, true);
            if (kind == "wait" && proposal["durationMilliseconds"] is { } duration)
            {
                var span = TimeSpan.FromMilliseconds(duration.GetValue<long>());
                var boundary = run.AuthoritativeConditionClock.Elapsed + span;
                await run.AuthoritativeConditionClock.DelayAsync(span, token).ConfigureAwait(false);
                if (run.AuthoritativeConditionClock.Elapsed < boundary)
                    throw new RunFailureException(new(RunReason.InvalidContract, RunPhase.Execution, RunOrigin.Clock));
                return new(ExploreWorkStatus.Completed, true);
            }
            try { return await work.ExecuteAsync((JsonObject)proposal.DeepClone(), ownerCalls, token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token) { throw; }
            catch (Exception exception) { throw exception as RunFailureException ?? new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host), exception); }
        }
    }

    private static IReadOnlyList<RunEvent> Events(ExploreReceipt receipt)
    {
        if (!Enum.IsDefined(receipt.Status)) return [new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract)];
        if (receipt.Status == ExploreWorkStatus.Failed) return [new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host)];
        if (receipt.Status == ExploreWorkStatus.Unconfirmed || !receipt.InputsNeutral)
            return [new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host)];
        return [];
    }
}
