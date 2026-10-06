using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Single execution owner. Callbacks enqueue evidence; they never write primary results.
/// Evaluate receives a complete same-cycle event set and reads real time once.</summary>
public sealed class RunSession
{
    private readonly IClock realClock, conditionClock;
    private readonly PreparedCondition? success, failure;
    private readonly CompletionPolicy policy;
    private ConditionSession? successSession, failureSession;
    private readonly List<RunEvent> events = [];
    private readonly List<RunEvent> pendingEvents = [];
    private readonly List<ExceptionEvidence> exceptions = [];
    private readonly List<ApprovedOperation> operations = [];
    private TimeSpan lastReal, lastCondition, preparationOrigin;
    private bool goalVerified;
    private bool approvalsClosing;
    private RunReason? closingExhaustion;
    private bool startCaptureArmed;
    public RunLimits Limits { get; }
    public RunBudget Budget { get; }
    public ExecutionState State { get; private set; } = ExecutionState.Created;
    public PrimaryResult? Primary { get; private set; }
    public bool GoalVerified => goalVerified;
    public TimeSpan? RunningOrigin { get; private set; }
    public TimeSpan NextRealEvaluationAt => State == ExecutionState.Preparing ? preparationOrigin + Limits.PreparationTimeout :
        operations.Where(x => x.IsOpen).Select(x => x.NextDeadline).Append(RunningOrigin!.Value + Limits.MaxDuration).Min();
    public IReadOnlyList<RunEvent> Events => events.AsReadOnly();
    public IReadOnlyList<ExceptionEvidence> Exceptions => exceptions.AsReadOnly();
    public bool ActionsClosing => State != ExecutionState.Running || approvalsClosing || Budget.Exhaustion.HasValue;
    public RunSession(RunLimits limits, IClock realClock, IClock conditionClock,
        PreparedCondition? success = null, PreparedCondition? failure = null,
        CompletionPolicy policy = CompletionPolicy.OnGoal)
    {
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(conditionClock);
        Limits = limits; Budget = new(limits); this.realClock = realClock; this.conditionClock = conditionClock;
        this.success = success; this.failure = failure; this.policy = policy;
        lastReal = ReadReal();
        lastCondition = ReadCondition();
    }
    private TimeSpan ReadCondition()
    {
        var now = conditionClock.Elapsed;
        if (now < lastCondition || now < TimeSpan.Zero || now > TimeSpan.MaxValue - TimeSpan.FromDays(2))
            throw new InvalidOperationException("ConditionClockInvalid");
        return lastCondition = now;
    }
    private TimeSpan ReadReal()
    {
        var now = realClock.Elapsed;
        if (now < TimeSpan.Zero || now < lastReal || now > TimeSpan.MaxValue - TimeSpan.FromDays(2))
            throw new InvalidOperationException("RunClockInvalid");
        return lastReal = now;
    }
    public void BeginPreparation()
    {
        Require(ExecutionState.Created); preparationOrigin = ReadReal(); State = ExecutionState.Preparing;
    }
    public void BeginRunning()
    {
        Require(ExecutionState.Preparing);
        if (startCaptureArmed) throw new InvalidOperationException("RunningBoundaryCertificateRequired");
        if (ReadReal() - preparationOrigin >= Limits.PreparationTimeout)
            throw new TimeoutException("PreparationDeadlineReached");
        var sharedConditionOrigin = ReadCondition();
        successSession = success?.Start(conditionClock, sharedConditionOrigin);
        failureSession = failure?.Start(conditionClock, sharedConditionOrigin);
        RunningOrigin = lastReal; State = ExecutionState.Running;
    }
    public RunStartCapture ArmRunningBoundary()
    {
        Require(ExecutionState.Preparing);
        if (startCaptureArmed) throw new InvalidOperationException("RunningBoundaryAlreadyArmed");
        var real = ReadReal();
        if (real - preparationOrigin >= Limits.PreparationTimeout) throw new TimeoutException("PreparationDeadlineReached");
        var condition = ReadCondition();
        startCaptureArmed = true;
        return new(this, real, condition);
    }
    internal void ValidateStartTimes(TimeSpan real, TimeSpan condition)
    {
        var now = ReadReal();
        var conditionNow = ReadCondition();
        if (now - preparationOrigin >= Limits.PreparationTimeout) throw new TimeoutException("PreparationDeadlineReached");
        if (real < preparationOrigin || real > now || condition < TimeSpan.Zero || condition > conditionNow ||
            real - preparationOrigin >= Limits.PreparationTimeout)
            throw new InvalidOperationException("RunningBoundaryTimeInvalid");
    }
    public void BeginRunning(RunStartBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary); Require(ExecutionState.Preparing);
        if (boundary.Owner != this || boundary.Used) throw new InvalidOperationException("RunningBoundaryOwnerInvalid");
        ValidateStartTimes(boundary.RealCapturedAt, boundary.InitialObservation.CapturedAt);
        successSession = success?.Start(conditionClock, boundary.InitialObservation.CapturedAt);
        failureSession = failure?.Start(conditionClock, boundary.InitialObservation.CapturedAt);
        boundary.Used = true;
        RunningOrigin = boundary.RealCapturedAt; State = ExecutionState.Running;
    }
    public PlannerPermit? RequestPlanner(bool recovering = false)
    {
        Require(ExecutionState.Running);
        operations.RemoveAll(x => !x.IsOpen);
        if (operations.Count != 0) return null;
        var now = ReadReal();
        if (ActionsClosing || now >= RunningOrigin!.Value + Limits.MaxDuration || !Budget.RequestDecision(recovering)) return null;
        var operation = new ApprovedOperation(this, Min(now + Limits.PlannerTimeout, RunningOrigin.Value + Limits.MaxDuration), null, planner: true);
        operations.Add(operation);
        if (Budget.Exhaustion is { } exhausted) { approvalsClosing = true; closingExhaustion ??= exhausted; }
        return new PlannerPermit(this, operation);
    }
    internal IReadOnlyList<RunEvent> InterruptedBoundaryFailureEvents(RunStartBoundary boundary)
    {
        var retained = new List<RunEvent>();
        try
        {
            Require(ExecutionState.Preparing);
            if (boundary.Owner != this || boundary.Used || boundary.RealCapturedAt < preparationOrigin ||
                boundary.RealCapturedAt >= preparationOrigin + Limits.PreparationTimeout || boundary.RealCapturedAt > ReadReal())
                throw new InvalidOperationException("RunningBoundaryOwnerInvalid");
            boundary.Used = true;
            // Inspect only failure/contract evidence from the certified initial unit. An expired
            // preparation never enters Running, establishes a goal, or authorizes a driver.
            foreach (var pair in new[] { (failure, boundary.InitialObservation.Failure, true), (success, boundary.InitialObservation.Success, false) })
            {
                var value = pair.Item1?.Start(conditionClock, boundary.InitialObservation.CapturedAt)
                    .EvaluateAt(pair.Item2, boundary.InitialObservation.CapturedAt);
                if (value?.Evaluation.Error == EvaluationError.InvalidConfiguration)
                    retained.Add(new(RunReason.InvalidContract, RunPhase.Preparation, RunOrigin.Contract));
                else if (value?.Evaluation.Error == EvaluationError.ObservationContractViolation)
                    retained.Add(new(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract));
                if (pair.Item3 && IsTrue(value))
                    retained.Add(new(RunReason.FailureCondition, RunPhase.Preparation, RunOrigin.Condition));
            }
        }
        catch (Exception exception)
        {
            RecordException(exception);
            retained.Add(new(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract));
        }
        return retained;
    }
    /// <summary>Replay/approved non-Planner work. The complete segment reserves before any transport.</summary>
    public ApprovedOperation? ApproveOperation(int actionCount, TimeSpan observationWindow)
        => Approve(actionCount, observationWindow, false);
    private ApprovedOperation? Approve(int count, TimeSpan window, bool finalPlannerPermit)
    {
        Require(ExecutionState.Running);
        operations.RemoveAll(x => !x.IsOpen);
        if (operations.Count != 0) return null;
        if (window <= TimeSpan.Zero || window > Limits.WaitTimeout || count < 0) throw new ArgumentOutOfRangeException(nameof(window));
        var now = ReadReal();
        if (now >= RunningOrigin!.Value + Limits.MaxDuration || Budget.Closed ||
            (!finalPlannerPermit && ActionsClosing)) return null;
        var reservation = count == 0 ? null : Budget.Reserve(count);
        if (count > 0 && reservation is null) return null;
        var operation = new ApprovedOperation(this, Min(now + window, RunningOrigin.Value + Limits.MaxDuration), reservation,
            count > 0 ? Min(now + Limits.ActionTimeout, RunningOrigin.Value + Limits.MaxDuration) : null);
        operations.Add(operation);
        if (Budget.Exhaustion is { } exhausted) { approvalsClosing = true; closingExhaustion ??= exhausted; }
        return operation;
    }
    public sealed class PlannerPermit
    {
        private readonly RunSession owner;
        private readonly ApprovedOperation request;
        private bool consumed;
        internal PlannerPermit(RunSession owner, ApprovedOperation request) { this.owner = owner; this.request = request; }
        public TimeSpan Deadline => request.Deadline;
        public ApprovedOperation? Approve(int actionCount, TimeSpan observationWindow)
        {
            if (consumed || !request.IsOpen || owner.ReadReal() >= Deadline || owner.State != ExecutionState.Running) return null;
            consumed = true; request.Complete();
            if (owner.lastReal >= Deadline) return null;
            return owner.Approve(actionCount, observationWindow, true);
        }
        public void CompleteWithoutOperation() { consumed = true; request.Complete(); }
    }
    public sealed class ApprovedOperation
    {
        private readonly RunSession owner;
        public TimeSpan Deadline { get; }
        public TimeSpan ResultDeadline { get; }
        public TimeSpan NextDeadline => ResultConfirmed ? Deadline : ResultDeadline;
        public ActionReservation? Actions { get; }
        public bool IsOpen { get; private set; } = true;
        public bool ResultConfirmed { get; private set; }
        internal bool IsPlanner { get; }
        internal ApprovedOperation(RunSession owner, TimeSpan deadline, ActionReservation? actions, TimeSpan? resultDeadline = null, bool planner = false)
        { this.owner = owner; Deadline = deadline; ResultDeadline = Min(deadline, resultDeadline ?? deadline); Actions = actions; IsPlanner = planner; }
        public void Complete()
            => CompleteAt(owner.State == ExecutionState.Running ? owner.ReadReal() : TimeSpan.Zero);
        internal void CompleteAt(TimeSpan now)
        {
            if (IsOpen && owner.State == ExecutionState.Running)
            {
                var unconfirmed = !ResultConfirmed && Actions?.Deliveries.Any(x => x is DeliveryState.Sent or DeliveryState.Uncertain) == true;
                if (unconfirmed || now >= NextDeadline)
                    owner.pendingEvents.Add(new(IsPlanner ? RunReason.PlannerTimeout : unconfirmed ? RunReason.ActionUnconfirmed : RunReason.WaitExpired,
                        RunPhase.Execution, IsPlanner ? RunOrigin.Planner : RunOrigin.Host));
            }
            IsOpen = false; Actions?.CancelUnsent();
        }
        public bool ConfirmResult()
        {
            if (!IsOpen || owner.State != ExecutionState.Running || owner.ReadReal() >= ResultDeadline) return false;
            ResultConfirmed = true; return true;
        }
        public void BeginDispatch(int index)
        {
            if (!IsOpen || ResultConfirmed || owner.State != ExecutionState.Running || owner.ReadReal() >= ResultDeadline)
                throw new InvalidOperationException("OperationClosed");
            Actions!.BeginDispatch(index);
        }
    }
    /// <summary>Both monitors consume the same trusted capture boundary. Real deadlines use delivery/current time.
    /// Null units are timer/cancel/budget cycles and never manufacture or reuse observations.</summary>
    public PrimaryResult? Evaluate(ConditionObservationUnit? unit = null, TimeSpan? capturedAt = null,
        IEnumerable<RunEvent>? candidates = null, bool cancelled = false, bool executionComplete = false,
        ConditionObservationUnit? failureUnit = null)
    {
        if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
        var cycle = candidates?.Take(Limits.MaxEvidenceItems + 1).ToList() ?? [];
        // Caller argument rejection must not consume already-authoritative operation evidence.
        if (cycle.Any(x => !Enum.IsDefined(x.Reason) || !Enum.IsDefined(x.Phase) || !Enum.IsDefined(x.Origin)))
            throw new ArgumentException("RunEventInvalid", nameof(candidates));
        if (cycle.Any(x => x.Reason == RunReason.GoalSatisfied)) throw new ArgumentException("GoalRequiresEvidence", nameof(candidates));
        cycle.AddRange(pendingEvents); pendingEvents.Clear();
        TimeSpan now;
        try { now = ReadReal(); }
        catch (InvalidOperationException exception)
        {
            RecordException(exception); now = lastReal;
            cycle.Add(new(RunReason.InvalidContract, Phase, RunOrigin.Clock));
        }
        if (cycle.Count > Limits.MaxEvidenceItems)
        {
            cycle = cycle.OrderBy(x => Priority(x.Reason)).ThenBy(x => x.Reason).Take(Limits.MaxEvidenceItems - 1).ToList();
            cycle.Add(new(RunReason.ExecutionError, Phase, RunOrigin.Runner));
        }
        if (cancelled) cycle.Add(new(RunReason.Cancelled, Phase, RunOrigin.User));
        if (State == ExecutionState.Preparing && now - preparationOrigin >= Limits.PreparationTimeout)
            cycle.Add(new(RunReason.PreparationTimeout, RunPhase.Preparation, RunOrigin.Clock));
        if (State == ExecutionState.Running)
        {
            if (unit is not null || failureUnit is not null)
            {
                if (capturedAt is null) throw new ArgumentException("CaptureTimeRequired", nameof(capturedAt));
                // Mandatory failure evaluation even if success has latched or the plan is not yet complete.
                // Paths are local to each prepared tree. Missing failure evidence is Unknown, never the success tree's reads.
                ConditionEvaluation? EvaluateCondition(ConditionSession? session, ConditionObservationUnit? evidence)
                {
                    try { return session?.EvaluateAt(evidence ?? new ConditionObservationUnit([]), capturedAt.Value); }
                    catch (InvalidOperationException exception)
                    {
                        RecordException(exception);
                        cycle.Add(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract));
                        return null;
                    }
                }
                var failed = EvaluateCondition(failureSession, failureUnit);
                var passed = EvaluateCondition(successSession, unit);
                var wakes = new[] { failed?.NextEvaluationAt, passed?.NextEvaluationAt }.Where(x => x.HasValue).ToArray();
                nextConditionEvaluationAt = wakes.Length == 0 ? null : wakes.Min();
                AddError(failed); AddError(passed);
                if (IsTrue(failed)) cycle.Add(new(RunReason.FailureCondition, Phase, RunOrigin.Condition));
                if (IsTrue(passed)) goalVerified = true;
                if (passed?.Completion == ConditionCompletion.Expired && !goalVerified)
                    cycle.Add(new(RunReason.GoalImpossible, Phase, RunOrigin.Condition));
            }
            if (now >= RunningOrigin!.Value + Limits.MaxDuration) cycle.Add(new(RunReason.MaxDuration, Phase, RunOrigin.Clock));
            foreach (var operation in operations.Where(x => x.IsOpen && now >= x.NextDeadline))
            {
                operation.CompleteAt(now);
            }
            cycle.AddRange(pendingEvents); pendingEvents.Clear();
            CollectCompletionEvidence();
        }
        if (cycle.Count != 0 && State == ExecutionState.Running)
        {
            // A terminal unit abandons outstanding results. Include that uncertainty before choosing/fixing primary.
            foreach (var operation in operations.Where(x => x.IsOpen)) operation.CompleteAt(now);
            cycle.AddRange(pendingEvents); pendingEvents.Clear();
            CollectCompletionEvidence();
        }
        // Budget exhaustion is terminal only after the final approved result/observation opportunity.
        // A verified success in that final unit wins over mere exhaustion; errors/cancel/deadline still win.
        if (events.Count + cycle.Count > Limits.MaxEvidenceItems)
        {
            cycle = cycle.OrderBy(x => Priority(x.Reason)).ThenBy(x => x.Reason).Take(Limits.MaxEvidenceItems - 1).ToList();
            cycle.Add(new(RunReason.ExecutionError, Phase, RunOrigin.Runner));
            // Retain a bounded prefix and the final deciding unit; no silent continuation after evidence overflow.
            events.RemoveRange(Math.Max(0, Limits.MaxEvidenceItems - cycle.Count),
                Math.Max(0, events.Count - Math.Max(0, Limits.MaxEvidenceItems - cycle.Count)));
        }
        events.AddRange(cycle);
        var finalSuccess = cycle.Any(x => x.Reason == RunReason.GoalSatisfied);
        var chosen = cycle.Where(x => !finalSuccess || x.Reason is not (RunReason.ActionsExhausted or RunReason.DecisionsExhausted or RunReason.RecoveryExhausted))
            .OrderBy(x => Priority(x.Reason)).ThenBy(x => x.Reason).ThenBy(x => x.Phase).ThenBy(x => x.Origin).FirstOrDefault();
        if (chosen is not null)
        {
            Primary = new(Status(chosen.Reason), chosen); State = ExecutionState.Completing;
            Budget.Settle(); foreach (var operation in operations) operation.CompleteAt(now);
        }
        return Primary;

        void CollectCompletionEvidence()
        {
            if (operations.Any(x => x.IsOpen)) return;
            void Add(RunReason reason, RunOrigin origin)
            {
                if (!cycle.Any(x => x.Reason == reason && x.Phase == Phase && x.Origin == origin))
                    cycle.Add(new(reason, Phase, origin));
            }
            if ((closingExhaustion ?? Budget.Exhaustion) is { } exhausted) Add(exhausted, RunOrigin.Budget);
            if (executionComplete && !goalVerified) Add(success is null ? RunReason.ExplorationFinished : RunReason.SuccessUnconfirmed, RunOrigin.Runner);
            if (goalVerified && (policy == CompletionPolicy.OnGoal || executionComplete)) Add(RunReason.GoalSatisfied, RunOrigin.Condition);
        }
        void AddError(ConditionEvaluation? value)
        {
            if (value?.Evaluation.Error is EvaluationError.InvalidConfiguration)
                cycle.Add(new(RunReason.InvalidContract, Phase, RunOrigin.Contract));
            else if (value?.Evaluation.Error is EvaluationError.ObservationContractViolation)
                cycle.Add(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract));
        }
    }
    private static bool IsTrue(ConditionEvaluation? value) => value?.Evaluation.Error == EvaluationError.None && value.Evaluation.Truth == TruthValue.True;
    public TimeSpan? NextConditionEvaluationAt => new[] { successSession, failureSession }.Any(x => x is not null) ? nextConditionEvaluationAt : null;
    private TimeSpan? nextConditionEvaluationAt;
    public void RecordException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exceptions.Count < Limits.MaxEvidenceItems)
            exceptions.Add(new(exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace));
        if (exception is RunFailureException or ProviderCancellationException && exception.InnerException is { } original && exceptions.Count < Limits.MaxEvidenceItems)
            exceptions.Add(new(original.GetType().FullName ?? original.GetType().Name, original.StackTrace));
    }
    internal RunOutcome Finish(IReadOnlyList<PostProcessingIssue> postProcessing)
    {
        Require(ExecutionState.Completing); State = ExecutionState.Finished;
        return new(Primary!, Array.AsReadOnly(events.ToArray()), Array.AsReadOnly(postProcessing.ToArray()), Array.AsReadOnly(exceptions.ToArray()));
    }
    public RunSnapshot CapturePrimary()
    {
        Require(ExecutionState.Completing);
        return new(Primary!, Array.AsReadOnly(events.ToArray()), Array.AsReadOnly(exceptions.ToArray()));
    }
    private RunPhase Phase => State is ExecutionState.Created or ExecutionState.Preparing ? RunPhase.Preparation : RunPhase.Execution;
    private void Require(ExecutionState expected) { if (State != expected) throw new InvalidOperationException("RunStateInvalid"); }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static int Priority(RunReason reason) => reason switch
    {
        RunReason.InvalidContract or RunReason.ObservationContractViolation => 0,
        RunReason.FailureCondition or RunReason.PreparationTimeout or RunReason.ActionFailed or RunReason.ActionUnconfirmed or RunReason.PlannerTimeout or RunReason.ExecutionError
            or RunReason.PlannerOutputInvalid or RunReason.PlannerUsageLimit or RunReason.PlannerConnectionFailure => 1,
        RunReason.Cancelled => 2, RunReason.MaxDuration => 3,
        RunReason.GoalSatisfied => 5, _ => 4
    };
    private ResultStatus Status(RunReason reason) => reason switch
    {
        RunReason.InvalidContract or RunReason.ObservationContractViolation => ResultStatus.Invalid,
        RunReason.Cancelled => ResultStatus.Aborted, RunReason.MaxDuration => ResultStatus.TimedOut,
        RunReason.GoalSatisfied => ResultStatus.Passed, RunReason.ExplorationFinished => ResultStatus.Unverified,
        RunReason.ActionsExhausted or RunReason.DecisionsExhausted or RunReason.RecoveryExhausted when success is null => ResultStatus.Unverified,
        _ => ResultStatus.Failed
    };
}
