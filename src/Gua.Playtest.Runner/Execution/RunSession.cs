using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;

namespace Gua.Playtest.Runner.Execution;

/// <summary>Single execution owner. Callbacks enqueue evidence; they never write primary results.
/// Evaluate receives a complete same-cycle event set and reads real time once.</summary>
public sealed class RunSession
{
    // Only data publication holds this gate. Provider reads never own finalization.
    private object arbitrationGate => postedExceptionGate;
    private sealed class EvaluationFrame
    {
        public RunEvent[] Evidence = [];
        public Exception[] Diagnostics = [];
        public bool ExecutionComplete;
        public volatile bool Abandoned;
    }
    private EvaluationFrame? activeEvaluation;
    private readonly AsyncLocal<EvaluationFrame?> currentEvaluation = new();
    private readonly AsyncLocal<bool> executorStage = new();
    private readonly AsyncLocal<TimeSpan?> certifiedConditionSnapshot = new();
    private CancellationToken callerCancellation;
    private bool frozenGoalVerified;
    internal void BindCancellation(CancellationToken token)
    { Require(ExecutionState.Created); callerCancellation = token; }
    private bool AbandonedEvaluation => currentEvaluation.Value?.Abandoned == true;
    private bool ExecutionDiagnosticsRevoked => AbandonedEvaluation ||
        executorStage.Value && State is ExecutionState.Completing or ExecutionState.Finished;
    internal ValueTask<T> RunExecutorStage<T>(Func<ValueTask<T>> action)
    {
        // Flow the marker into hooks and provider continuations, then restore the
        // finalizer context without wrapping or delaying the provider's task.
        var previous = executorStage.Value; executorStage.Value = true;
        try { return action(); }
        finally { executorStage.Value = previous; }
    }
    private sealed class ValidatedClock(RunSession owner, bool condition) : IClock
    {
        public TimeSpan Elapsed => condition ? owner.ReadCondition() : owner.ReadReal();
        public async ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            try { await (condition ? owner.conditionClock : owner.realClock).DelayAsync(duration, token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token) { throw; }
            catch (Exception exception) { throw new ClockProviderException(exception); }
        }
    }
    private sealed record CapturedUnit(ConditionObservationUnit? Success, TimeSpan At, ConditionObservationUnit? Failure);
    private readonly IClock realClock, conditionClock;
    private readonly IClock validatedRealClock, validatedConditionClock;
    private readonly PreparedCondition? success, failure;
    private readonly CompletionPolicy policy;
    private ConditionSession? successSession, failureSession;
    private readonly List<RunEvent> events = [];
    private readonly List<RunEvent> pendingEvents = [];
    private readonly List<ExceptionEvidence> exceptions = [];
    private readonly object postedExceptionGate = new();
    private readonly Queue<(Exception Exception, RunEvent? Cause)> postedExceptions = new();
    private bool postedExceptionsClosed;
    private bool postedExceptionsOverflow;
    private bool postedCausalEvidence;
    private Func<RunFailureException?>? initialLifecycleFailure;
    private bool lifecycleSampling;
    private readonly List<ApprovedOperation> operations = [];
    private TimeSpan lastReal, lastCondition, lastCapture, preparationOrigin;
    private bool goalVerified;
    private bool approvalsClosing;
    private readonly HashSet<RunReason> closingExhaustions = [];
    private bool startCaptureArmed;
    internal bool IsConfirmingWork { get; private set; }
    internal void ConfirmMonitoredWork<T>(T value, Action<T> confirm)
    {
        if (IsConfirmingWork) throw new InvalidOperationException("WorkConfirmationReentry");
        IsConfirmingWork = true;
        try { confirm(value); } finally { IsConfirmingWork = false; }
    }
    public RunLimits Limits { get; }
    internal IClock AuthoritativeRealClock => validatedRealClock;
    internal IClock AuthoritativeConditionClock => validatedConditionClock;
    internal TimeSpan ReadAuthoritativeReal() => ReadReal();
    internal TimeSpan ReadAuthoritativeCondition() => ReadCondition();
    internal TimeSpan LastValidatedReal => lastReal;
    public RunBudget Budget { get; }
    public ExecutionState State { get; private set; } = ExecutionState.Created;
    public PrimaryResult? Primary { get; private set; }
    public bool GoalVerified => State is ExecutionState.Completing or ExecutionState.Finished ? frozenGoalVerified : goalVerified;
    public TimeSpan? RunningOrigin { get; private set; }
    public TimeSpan NextRealEvaluationAt => State == ExecutionState.Preparing ? preparationOrigin + Limits.PreparationTimeout :
        operations.Where(x => x.IsOpen).Select(x => x.NextDeadline).Append(RunningOrigin!.Value + Limits.MaxDuration).Min();
    public IReadOnlyList<RunEvent> Events => events.AsReadOnly();
    public IReadOnlyList<ExceptionEvidence> Exceptions => exceptions.AsReadOnly();
    internal bool HasPendingTerminalEvidence
    {
        get
        {
            SampleLifecycle();
            return HasQueuedTerminalEvidence;
        }
    }
    private bool HasQueuedTerminalEvidence
    { get { lock (postedExceptionGate) return pendingEvents.Count != 0 || postedCausalEvidence || postedExceptionsOverflow; } }
    private bool ActionsClosingWithoutProbe => State != ExecutionState.Running || IsConfirmingWork ||
        HasQueuedTerminalEvidence || approvalsClosing || Budget.Exhaustion.HasValue;
    private void SampleLifecycle(bool closing = false)
    {
        Func<RunFailureException?>? probe;
        lock (postedExceptionGate)
        {
            probe = State == ExecutionState.Running && !lifecycleSampling ? initialLifecycleFailure : null;
            if (probe is not null) lifecycleSampling = true;
        }
        if (probe is null) return;
        // Provider clocks and diagnostic transport can be synchronous dependencies.
        // They must not hold authority's data gate while the executor finalizes.
        // An interruption does not launch a second call behind a stalled probe.
        try
        {
            if (probe() is not { } failure) return;
            lock (postedExceptionGate)
            {
                if (State != ExecutionState.Running || postedExceptionsClosed && !closing || !ReferenceEquals(initialLifecycleFailure, probe)) return;
                initialLifecycleFailure = null; QueuePending(failure.Cause);
                RecordEmergencyException(failure);
            }
        }
        finally { lock (postedExceptionGate) lifecycleSampling = false; }
    }
    public bool ActionsClosing => State != ExecutionState.Running || IsConfirmingWork || HasPendingTerminalEvidence || approvalsClosing || Budget.Exhaustion.HasValue;
    public RunSession(RunLimits limits, IClock realClock, IClock conditionClock,
        PreparedCondition? success = null, PreparedCondition? failure = null,
        CompletionPolicy policy = CompletionPolicy.OnGoal)
    {
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(realClock);
        ArgumentNullException.ThrowIfNull(conditionClock);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Limits = limits; Budget = new(limits); this.realClock = realClock; this.conditionClock = conditionClock;
        validatedRealClock = new ValidatedClock(this, false); validatedConditionClock = new ValidatedClock(this, true);
        this.success = success; this.failure = failure; this.policy = policy;
        lastReal = ReadReal();
        lastCondition = ReadCondition();
    }
    private TimeSpan ReadCondition()
    {
        if (certifiedConditionSnapshot.Value is { } capturedClock) return capturedClock;
        TimeSpan baseline;
        lock (arbitrationGate) baseline = lastCondition;
        try
        {
            var now = conditionClock.Elapsed;
            lock (arbitrationGate)
            {
                if (AbandonedEvaluation) return lastCondition;
                // A concurrent later sample can publish before this read returns.
                // Compare with the epoch known before this invocation, not that
                // later sample, and never move the authoritative watermark back.
                if (now < baseline || now < TimeSpan.Zero || now > TimeSpan.MaxValue - TimeSpan.FromDays(2))
                    throw new InvalidOperationException("ConditionClockInvalid");
                if (now > lastCondition) lastCondition = now;
                return lastCondition;
            }
        }
        catch (Exception) { QueueClockRejection(); throw; }
    }
    private void QueueClockRejection()
    {
        QueuePending(new(RunReason.InvalidContract, Phase, RunOrigin.Clock));
    }
    private void QueuePending(RunEvent cause)
    {
        lock (arbitrationGate)
        {
            if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return;
            if (!pendingEvents.Contains(cause)) pendingEvents.Add(cause);
        }
    }
    private bool DrainPending(List<RunEvent> cycle)
    {
        lock (arbitrationGate)
        {
            if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return false;
            cycle.AddRange(pendingEvents);
            if (currentEvaluation.Value is { } frame) frame.Evidence = cycle.ToArray();
            pendingEvents.Clear(); return true;
        }
    }
    private bool PublishActiveEvidence(List<RunEvent> cycle, Action? update = null, Exception? diagnostic = null)
    {
        lock (arbitrationGate)
        {
            if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return false;
            update?.Invoke();
            if (currentEvaluation.Value is { } frame)
            {
                frame.Evidence = cycle.ToArray();
                if (diagnostic is not null) frame.Diagnostics = frame.Diagnostics.Append(diagnostic).Take(Limits.MaxEvidenceItems).ToArray();
            }
        }
        if (diagnostic is not null)
        {
            RecordException(diagnostic);
            lock (arbitrationGate)
                if (currentEvaluation.Value is { Abandoned: false } frame)
                    frame.Diagnostics = frame.Diagnostics.Where(item => !ReferenceEquals(item, diagnostic)).ToArray();
        }
        return !AbandonedEvaluation;
    }
    internal bool HasPendingClockRejection => pendingEvents.Any(item => item.Reason == RunReason.InvalidContract && item.Origin == RunOrigin.Clock);
    private ConditionSession? StartCondition(PreparedCondition? prepared, TimeSpan origin)
    {
        try { return prepared?.Start(validatedConditionClock, origin); }
        catch (ArgumentOutOfRangeException)
        {
            // A clock can reset between the shared-origin read and either condition's start.
            // Keep typed contract evidence before rethrowing the unchanged provider exception.
            QueueClockRejection();
            throw;
        }
    }
    private TimeSpan ReadReal()
    {
        TimeSpan baseline;
        lock (arbitrationGate) baseline = lastReal;
        try
        {
            var now = realClock.Elapsed;
            lock (arbitrationGate)
            {
                if (AbandonedEvaluation) return lastReal;
                if (now < TimeSpan.Zero || now < baseline || now > TimeSpan.MaxValue - TimeSpan.FromDays(2))
                    throw new InvalidOperationException("RunClockInvalid");
                if (now > lastReal) lastReal = now;
                return lastReal;
            }
        }
        catch (Exception) { QueueClockRejection(); throw; }
    }
    public void BeginPreparation()
    {
        Require(ExecutionState.Created); preparationOrigin = ReadReal(); State = ExecutionState.Preparing;
    }
    public void BeginRunning()
    {
        Require(ExecutionState.Preparing);
        if (startCaptureArmed) throw new InvalidOperationException("RunningBoundaryCertificateRequired");
        DrainPostedExceptions(close: false);
        if (ReadReal() - preparationOrigin >= Limits.PreparationTimeout)
            throw FiniteOperation.DeadlineReached("PreparationDeadlineReached");
        var sharedConditionOrigin = ReadCondition();
        lastCapture = sharedConditionOrigin;
        successSession = StartCondition(success, sharedConditionOrigin);
        failureSession = StartCondition(failure, sharedConditionOrigin);
        RunningOrigin = lastReal; State = ExecutionState.Running;
    }
    public RunStartCapture ArmRunningBoundary()
    {
        Require(ExecutionState.Preparing);
        if (startCaptureArmed) throw new InvalidOperationException("RunningBoundaryAlreadyArmed");
        var real = ReadReal();
        if (real - preparationOrigin >= Limits.PreparationTimeout) throw FiniteOperation.DeadlineReached("PreparationDeadlineReached");
        var condition = ReadCondition();
        startCaptureArmed = true;
        return new(this, real, condition);
    }
    internal void ValidateStartTimes(TimeSpan real, TimeSpan condition)
    {
        var now = ReadReal();
        var conditionNow = ReadCondition();
        if (now - preparationOrigin >= Limits.PreparationTimeout) throw FiniteOperation.DeadlineReached("PreparationDeadlineReached");
        if (real < preparationOrigin || real > now || condition < TimeSpan.Zero || condition > conditionNow ||
            real - preparationOrigin >= Limits.PreparationTimeout)
            RejectStartObservation("RunningBoundaryTimeInvalid");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectStartObservation(string code, Exception? rejection = null)
    {
        var cause = new RunEvent(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract);
        QueuePending(cause);
        throw rejection ?? new InvalidOperationException(code);
    }
    public void BeginRunning(RunStartBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary); Require(ExecutionState.Preparing);
        if (boundary.Owner != this || boundary.Used) throw new InvalidOperationException("RunningBoundaryOwnerInvalid");
        ValidateStartTimes(boundary.RealCapturedAt, boundary.InitialObservation.CapturedAt);
        lastCapture = boundary.InitialObservation.CapturedAt;
        successSession = StartCondition(success, boundary.InitialObservation.CapturedAt);
        failureSession = StartCondition(failure, boundary.InitialObservation.CapturedAt);
        DrainPostedExceptions(close: false);
        initialLifecycleFailure = boundary.InitialLifecycleFailure;
        boundary.Used = true;
        RunningOrigin = boundary.RealCapturedAt; State = ExecutionState.Running;
    }
    public PlannerPermit? RequestPlanner(bool recovering = false)
    {
        lock (arbitrationGate)
        {
            Require(ExecutionState.Running);
            operations.RemoveAll(x => !x.IsOpen);
            if (operations.Count != 0) return null;
        }
        var now = ReadReal();
        if (ActionsClosing) return null;
        lock (postedExceptionGate)
        {
            if (AbandonedEvaluation || operations.Any(x => x.IsOpen) || ActionsClosingWithoutProbe ||
                now >= RunningOrigin!.Value + Limits.MaxDuration || !Budget.RequestDecision(recovering)) return null;
            var operation = new ApprovedOperation(this, Min(now + Limits.PlannerTimeout, RunningOrigin.Value + Limits.MaxDuration), null, planner: true);
            operations.Add(operation);
            if (Budget.Exhaustion.HasValue) { approvalsClosing = true; closingExhaustions.UnionWith(Budget.Exhaustions); }
            return new PlannerPermit(this, operation);
        }
    }
    // BeginRunning has already validated this certificate's clocks and copied
    // immutable observation maps. Classify those ready data before cancellation
    // can win the bounded provider-validation/lifecycle stage. No provider calls.
    internal void RetainCertifiedInitialFacts(RunObservation initial)
    {
        Require(ExecutionState.Running);
        var previous = certifiedConditionSnapshot.Value; certifiedConditionSnapshot.Value = lastCondition;
        ConditionEvaluation? failed = null, passed = null;
        try
        {
            failed = failureSession?.EvaluateAt(initial.Failure ?? new ConditionObservationUnit([]), initial.CapturedAt);
            passed = successSession?.EvaluateAt(initial.Success ?? new ConditionObservationUnit([]), initial.CapturedAt);
        }
        catch (Exception exception)
        { QueuePending(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract)); RecordException(exception); }
        finally { certifiedConditionSnapshot.Value = previous; }
        lock (arbitrationGate)
        {
            var facts = new List<RunEvent>(); AddError(facts, failed); AddError(facts, passed);
            if (IsTrue(failed)) facts.Add(new(RunReason.FailureCondition, RunPhase.Execution, RunOrigin.Condition));
            foreach (var fact in facts) QueuePending(fact);
            if (IsTrue(passed)) goalVerified = true;
            var wakes = new[] { failed?.NextEvaluationAt, passed?.NextEvaluationAt }.Where(value => value.HasValue).ToArray();
            nextConditionEvaluationAt = wakes.Length == 0 ? null : wakes.Min();
        }
    }
    internal IReadOnlyList<RunEvent> InterruptedBoundaryFailureEvents(RunStartBoundary boundary)
    {
        var retained = new List<RunEvent>();
        var lifecycleAccepted = false;
        try
        {
            Require(ExecutionState.Preparing);
            if (boundary.Owner != this || boundary.Used) throw new InvalidOperationException("RunningBoundaryOwnerInvalid");
            lifecycleAccepted = true;
            if (boundary.RealCapturedAt < preparationOrigin ||
                boundary.RealCapturedAt >= preparationOrigin + Limits.PreparationTimeout || boundary.RealCapturedAt > ReadReal())
                throw new InvalidOperationException("RunningBoundaryOwnerInvalid");
            boundary.Used = true;
            _ = ReadCondition();
            // Inspect only failure/contract evidence from the certified initial unit. An expired
            // preparation never enters Running, establishes a goal, or authorizes a driver.
            foreach (var pair in new[] { (failure, boundary.InitialObservation.Failure, true), (success, boundary.InitialObservation.Success, false) })
            {
                var value = StartCondition(pair.Item1, boundary.InitialObservation.CapturedAt)
                    ?.EvaluateAt(pair.Item2, boundary.InitialObservation.CapturedAt);
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
            if (!pendingEvents.Any(x => x.Reason == RunReason.InvalidContract && x.Origin == RunOrigin.Clock))
                retained.Add(new(RunReason.ObservationContractViolation, RunPhase.Preparation, RunOrigin.Contract));
        }
        if (lifecycleAccepted && boundary.InitialLifecycleFailure is { } lifecycle)
        {
            boundary.InitialLifecycleFailure = null;
            if (lifecycle() is { } failure)
            { RecordException(failure); retained.Add(failure.Cause with { Phase = RunPhase.Preparation }); }
        }
        return retained;
    }
    /// <summary>Replay/approved non-Planner work. The complete segment reserves before any transport.</summary>
    public ApprovedOperation? ApproveOperation(int actionCount, TimeSpan observationWindow)
        => Approve(actionCount, observationWindow, false);
    /// <summary>Owner-controlled finite authority for an already reached observation boundary.
    /// Charges no action or decision, never grants dispatch, and retains settlement through Goal.
    /// Complete the preceding operation first; every window stays within the original Run deadline.</summary>
    public ApprovedOperation? ApproveObservation(TimeSpan observationWindow)
        => Approve(0, observationWindow, true, requiresSettlement: true);
    private ApprovedOperation? Approve(int count, TimeSpan window, bool finalPlannerPermit, TimeSpan? authorityDeadline = null, RunEvent? expiryCause = null,
        TimeSpan? elapsedWait = null, bool requiresSettlement = false)
    {
        lock (arbitrationGate)
        {
            Require(ExecutionState.Running);
            operations.RemoveAll(x => !x.IsOpen);
            if (operations.Count != 0) return null;
        }
        if (window <= TimeSpan.Zero || window > Limits.WaitTimeout || count < 0) throw new ArgumentOutOfRangeException(nameof(window));
        if (elapsedWait is { } wait && (count != 0 || wait <= TimeSpan.Zero || wait > Limits.WaitTimeout))
            throw new ArgumentOutOfRangeException(nameof(elapsedWait));
        var waitCompletesAt = elapsedWait is { } duration ? ReadCondition() + duration : (TimeSpan?)null;
        var now = ReadReal();
        if (authorityDeadline is { } boundary && now >= boundary)
        {
            if (expiryCause is not null) QueuePending(expiryCause);
            return null;
        }
        if (HasPendingTerminalEvidence) return null;
        lock (postedExceptionGate)
        {
            if (State != ExecutionState.Running || AbandonedEvaluation || operations.Any(x => x.IsOpen) ||
                IsConfirmingWork || HasQueuedTerminalEvidence || now >= RunningOrigin!.Value + Limits.MaxDuration || Budget.Closed ||
                (!finalPlannerPermit && ActionsClosingWithoutProbe)) return null;
            var reservation = count == 0 ? null : Budget.Reserve(count);
            if (count > 0 && reservation is null) return null;
            var operation = new ApprovedOperation(this, Min(now + window + (elapsedWait ?? TimeSpan.Zero), RunningOrigin.Value + Limits.MaxDuration), reservation,
                count > 0 ? Min(now + Limits.ActionTimeout, RunningOrigin.Value + Limits.MaxDuration) : null,
                waitCompletesAt: waitCompletesAt, observationWindow: elapsedWait.HasValue ? window : null, requiresSettlement: requiresSettlement);
            operations.Add(operation);
            if (Budget.Exhaustion.HasValue) { approvalsClosing = true; closingExhaustions.UnionWith(Budget.Exhaustions); }
            return operation;
        }
    }
    public sealed class PlannerPermit
    {
        private readonly RunSession owner;
        private readonly ApprovedOperation request;
        private bool consumed;
        internal PlannerPermit(RunSession owner, ApprovedOperation request) { this.owner = owner; this.request = request; }
        public TimeSpan Deadline => request.Deadline;
        public bool ConfirmResponse() => !consumed && request.ConfirmResult();
        public ApprovedOperation? Approve(int actionCount, TimeSpan observationWindow)
            => ApproveCore(actionCount, observationWindow, null);
        /// <summary>One finite elapsed wait followed by a separately bounded observation allowance.
        /// Both ceilings and the original global deadline remain enforced; no new decision is charged.</summary>
        public ApprovedOperation? ApproveElapsedWait(TimeSpan duration, TimeSpan observationWindow)
            => ApproveCore(0, observationWindow, duration);
        private ApprovedOperation? ApproveCore(int actionCount, TimeSpan observationWindow, TimeSpan? elapsedWait)
        {
            var approvalDeadline = request.NextDeadline;
            if (owner.IsConfirmingWork || consumed || !request.IsOpen || owner.ReadReal() >= approvalDeadline || owner.State != ExecutionState.Running) return null;
            consumed = true; request.Complete();
            if (owner.lastReal >= approvalDeadline) return null;
            return owner.Approve(actionCount, observationWindow, true, approvalDeadline,
                new(request.ResultConfirmed ? RunReason.WaitExpired : RunReason.PlannerTimeout, RunPhase.Execution,
                    request.ResultConfirmed ? RunOrigin.Host : RunOrigin.Planner), elapsedWait);
        }
        public void CompleteWithoutOperation() { consumed = true; request.Complete(); }
    }
    public sealed class ApprovedOperation
    {
        private readonly RunSession owner;
        public TimeSpan Deadline { get; }
        public TimeSpan ResultDeadline { get; }
        private TimeSpan resultConfirmedAt;
        private readonly TimeSpan? waitCompletesAt, observationWindow;
        private readonly bool requiresSettlement;
        public bool DispatchClosedByGoal { get; private set; }
        public TimeSpan NextDeadline => ResultConfirmed ? IsPlanner
            ? Min(resultConfirmedAt + owner.Limits.WaitTimeout, owner.RunningOrigin!.Value + owner.Limits.MaxDuration)
            : observationWindow is { } window ? Min(resultConfirmedAt + window, Deadline) : Deadline : ResultDeadline;
        public ActionReservation? Actions { get; }
        public bool IsOpen { get; private set; } = true;
        public bool ResultConfirmed { get; private set; }
        internal bool IsPlanner { get; }
        internal bool AwaitsSettlement => IsOpen && !ResultConfirmed && (requiresSettlement || waitCompletesAt.HasValue ||
            Actions?.Deliveries.Any(d => d is DeliveryState.Sent or DeliveryState.Uncertain) == true);
        internal ApprovedOperation(RunSession owner, TimeSpan deadline, ActionReservation? actions, TimeSpan? resultDeadline = null, bool planner = false,
            TimeSpan? waitCompletesAt = null, TimeSpan? observationWindow = null, bool requiresSettlement = false)
        { this.owner = owner; Deadline = deadline; ResultDeadline = Min(deadline, resultDeadline ?? deadline); Actions = actions; IsPlanner = planner;
            this.waitCompletesAt = waitCompletesAt; this.observationWindow = observationWindow; this.requiresSettlement = requiresSettlement; }
        internal void CloseDispatchForGoal()
        {
            if (!IsOpen || Actions is null) return;
            DispatchClosedByGoal = true; Actions.CancelUnsent();
        }
        public void Complete()
        {
            if (requiresSettlement && !ResultConfirmed && IsOpen && owner.State == ExecutionState.Running)
                owner.QueuePending(new(RunReason.WaitExpired, RunPhase.Execution, RunOrigin.Host));
            CompleteAt(owner.State == ExecutionState.Running ? owner.ReadReal() : TimeSpan.Zero);
        }
        internal void CompleteAt(TimeSpan now)
        {
            lock (owner.arbitrationGate)
            {
                if (IsOpen && owner.State == ExecutionState.Running)
                {
                    var unconfirmed = !ResultConfirmed && Actions?.Deliveries.Any(x => x is DeliveryState.Sent or DeliveryState.Uncertain) == true;
                    if (unconfirmed || now >= NextDeadline)
                        owner.QueuePending(new(IsPlanner && !ResultConfirmed ? RunReason.PlannerTimeout : unconfirmed ? RunReason.ActionUnconfirmed : RunReason.WaitExpired,
                            RunPhase.Execution, IsPlanner && !ResultConfirmed ? RunOrigin.Planner : RunOrigin.Host));
                }
                IsOpen = false; Actions?.CancelUnsent();
            }
        }
        public bool ConfirmResult()
        {
            lock (owner.arbitrationGate)
            {
                if (!IsOpen || owner.State != ExecutionState.Running) return false;
                if (ResultConfirmed) return true;
            }
            var now = owner.ReadReal();
            if (now >= ResultDeadline) return false;
            if (waitCompletesAt is { } waitBoundary && owner.ReadCondition() < waitBoundary) return false;
            lock (owner.arbitrationGate)
            {
                if (!IsOpen || owner.State != ExecutionState.Running || owner.AbandonedEvaluation || owner.lastReal >= ResultDeadline) return false;
                if (ResultConfirmed) return true;
                resultConfirmedAt = now; ResultConfirmed = true; return true;
            }
        }
        /// <summary>Owner-only accounting for reported side effects that bypassed the dispatch guard.
        /// Consumes an uncertain attempt without granting dispatch authority or confirming its result.
        /// The caller must retain the contract failure; closed operations cannot accept late evidence.</summary>
        public void RecordUnconfirmedSend(int index)
        {
            lock (owner.postedExceptionGate)
            {
                if (!IsOpen || ResultConfirmed || owner.State != ExecutionState.Running || Actions is null)
                    throw new InvalidOperationException("OperationClosed");
                Actions.RecordUnconfirmedSend(index);
            }
        }
        public void BeginDispatch(int index)
        {
            if (owner.IsConfirmingWork || owner.HasPendingTerminalEvidence || !IsOpen || ResultConfirmed || DispatchClosedByGoal || owner.State != ExecutionState.Running)
                throw new InvalidOperationException("OperationClosed");
            var now = owner.ReadReal();
            if (owner.HasPendingTerminalEvidence) throw new InvalidOperationException("OperationClosed");
            lock (owner.postedExceptionGate)
            {
                if (owner.IsConfirmingWork || owner.HasQueuedTerminalEvidence || !IsOpen || ResultConfirmed || DispatchClosedByGoal || owner.State != ExecutionState.Running || now >= ResultDeadline)
                    throw new InvalidOperationException("OperationClosed");
                Actions!.BeginDispatch(index);
            }
        }
    }
    /// <summary>Both monitors consume the same trusted capture boundary. Real deadlines use delivery/current time.
    /// Null units are timer/cancel/budget cycles and never manufacture or reuse observations.</summary>
    public PrimaryResult? Evaluate(ConditionObservationUnit? unit = null, TimeSpan? capturedAt = null,
        IEnumerable<RunEvent>? candidates = null, bool cancelled = false, bool executionComplete = false,
        ConditionObservationUnit? failureUnit = null)
    {
        if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
        CapturedUnit[] units = unit is not null || failureUnit is not null
            ? [new(unit, capturedAt ?? throw new ArgumentException("CaptureTimeRequired", nameof(capturedAt)), failureUnit)] : [];
        return EvaluateCore(units, candidates, cancelled, executionComplete);
    }
    internal PrimaryResult? EvaluateCapturedUnits(IReadOnlyList<RunObservation?> observations, IEnumerable<RunEvent> candidates, bool cancelled)
    {
        var units = new List<CapturedUnit>(); var invalid = new List<RunEvent>();
        foreach (var observation in observations)
        {
            if (observation is null || observation.Success is null || observation.Failure is null)
                invalid.Add(new(RunReason.ObservationContractViolation, RunPhase.Execution, RunOrigin.Contract));
            else units.Add(new(observation.Success, observation.CapturedAt, observation.Failure));
        }
        return EvaluateCore(units, candidates.Concat(invalid), cancelled, false);
    }
    private PrimaryResult? EvaluateCore(IReadOnlyList<CapturedUnit> units, IEnumerable<RunEvent>? candidates,
        bool cancelled, bool executionComplete)
    {
        if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
        var incoming = candidates?.Take(Limits.MaxEvidenceItems + 1).ToList() ?? [];
        if (incoming.Any(x => !Enum.IsDefined(x.Reason) || !Enum.IsDefined(x.Phase) || !Enum.IsDefined(x.Origin)))
            throw new ArgumentException("RunEventInvalid", nameof(candidates));
        if (incoming.Any(x => x.Reason == RunReason.GoalSatisfied)) throw new ArgumentException("GoalRequiresEvidence", nameof(candidates));
        EvaluationFrame frame;
        lock (arbitrationGate)
        {
            if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
            if (activeEvaluation is { } active)
            {
                if (!cancelled && !callerCancellation.IsCancellationRequested && incoming.Count == 0)
                    throw new InvalidOperationException("RunEvaluationAlreadyActive");
                return InterruptEvaluation(active, incoming, cancelled, executionComplete);
            }
            frame = new() { Evidence = incoming.Concat(pendingEvents).ToArray(), ExecutionComplete = executionComplete };
            activeEvaluation = frame;
        }
        var previous = currentEvaluation.Value; currentEvaluation.Value = frame;
        try { return EvaluateUnit(units, incoming, cancelled, executionComplete); }
        finally
        {
            // The revoked execution context remains revoked after Evaluate returns;
            // cleanup runs in the separate executor context and may retain its issues.
            currentEvaluation.Value = frame.Abandoned ? frame : previous;
            lock (arbitrationGate) if (ReferenceEquals(activeEvaluation, frame)) activeEvaluation = null;
        }
    }
    internal PrimaryResult? FinalizeInterruptedExecution(IEnumerable<RunEvent> candidates, bool cancelled, bool executionComplete, Exception exception)
    {
        var incoming = candidates.ToList();
        lock (arbitrationGate)
        {
            if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
            // No live provider is allowed to become the finalizer's dependency.
            // Settle from the validated watermark, owned queues and active snapshot.
            RecordEmergencyException(exception);
            return InterruptEvaluation(activeEvaluation ?? new EvaluationFrame(), incoming, cancelled, executionComplete);
        }
    }
    // Caller holds the short publication gate; never sample a provider or join a task.
    private PrimaryResult? InterruptEvaluation(EvaluationFrame active, List<RunEvent> incoming, bool cancelled, bool executionComplete)
    {
        active.Abandoned = true;
        var cycle = active.Evidence.Concat(pendingEvents).Concat(incoming).ToList();
        if (cancelled || callerCancellation.IsCancellationRequested) cycle.Add(new(RunReason.Cancelled, Phase, RunOrigin.User));
        CollectLatchedInterruptions(cycle);
        foreach (var operation in operations.Where(x => x.IsOpen)) operation.CompleteAt(lastReal);
        cycle.AddRange(pendingEvents); pendingEvents.Clear();
        postedExceptionsClosed = true;
        foreach (var diagnostic in active.Diagnostics) RecordEmergencyException(diagnostic);
        foreach (var item in postedExceptions)
        {
            if (item.Cause is { } cause) cycle.Add(cause);
            RecordEmergencyException(item.Exception);
        }
        postedExceptions.Clear(); postedCausalEvidence = false;
        if (postedExceptionsOverflow) cycle.Add(new(RunReason.ExecutionError, Phase, RunOrigin.Runner));
        postedExceptionsOverflow = false;
        cycle.AddRange(pendingEvents); pendingEvents.Clear();
        // Closing the final opportunity makes budget/verified Goal/declared plan
        // completion evidence eligible even though the provider unit was revoked.
        // Collect only already-known completion; never infer it from a stalled task.
        if (State == ExecutionState.Running) CollectCompletionEvidence(cycle, active.ExecutionComplete || executionComplete);
        return PublishPrimary(cycle.Distinct().ToList(), lastReal);
    }
    private void RecordEmergencyException(Exception exception)
        => RecordExceptionCore(exception, emergency: true);
    private PrimaryResult? EvaluateUnit(IReadOnlyList<CapturedUnit> units, IEnumerable<RunEvent>? candidates,
        bool cancelled, bool executionComplete)
    {
        if (State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
        var cycle = candidates?.Take(Limits.MaxEvidenceItems + 1).ToList() ?? [];
        void CollectCompletionEvidence() => this.CollectCompletionEvidence(cycle, executionComplete);
        void AddError(ConditionEvaluation? value) => this.AddError(cycle, value);
        // Caller argument rejection must not consume already-authoritative operation evidence.
        if (cycle.Any(x => !Enum.IsDefined(x.Reason) || !Enum.IsDefined(x.Phase) || !Enum.IsDefined(x.Origin)))
            throw new ArgumentException("RunEventInvalid", nameof(candidates));
        if (cycle.Any(x => x.Reason == RunReason.GoalSatisfied)) throw new ArgumentException("GoalRequiresEvidence", nameof(candidates));
        DrainPostedExceptions(close: false);
        if (!DrainPending(cycle)) return Primary;
        TimeSpan now;
        try { now = ReadReal(); }
        catch (Exception exception)
        {
            if (!PublishActiveEvidence(cycle, () =>
            {
                if (!cycle.Any(x => x.Reason == RunReason.InvalidContract && x.Phase == Phase && x.Origin == RunOrigin.Clock))
                    cycle.Add(new(RunReason.InvalidContract, Phase, RunOrigin.Clock));
            }, exception)) return Primary;
            now = lastReal;
            // Keep any evidence queued during the read, including the rejecting clock itself.
            if (!DrainPending(cycle)) return Primary;
            if (!cycle.Any(x => x.Reason == RunReason.InvalidContract && x.Phase == Phase && x.Origin == RunOrigin.Clock))
                cycle.Add(new(RunReason.InvalidContract, Phase, RunOrigin.Clock));
        }
        if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
        if (cycle.Count > Limits.MaxEvidenceItems)
        {
            cycle = cycle.OrderBy(x => Priority(x.Reason)).ThenBy(x => x.Reason).ThenBy(x => x.Phase).ThenBy(x => x.Origin).Take(Limits.MaxEvidenceItems - 1).ToList();
            cycle.Add(new(RunReason.ExecutionError, Phase, RunOrigin.Runner));
        }
        if (cancelled) cycle.Add(new(RunReason.Cancelled, Phase, RunOrigin.User));
        if (State == ExecutionState.Preparing && now - preparationOrigin >= Limits.PreparationTimeout)
            cycle.Add(new(RunReason.PreparationTimeout, RunPhase.Preparation, RunOrigin.Clock));
        if (State == ExecutionState.Running)
        {
            foreach (var captured in units)
            {
                // Source timestamps obey the observation contract even for exploration without trees.
                bool validCapture;
                try
                {
                    if (captured.At < TimeSpan.Zero || captured.At > TimeSpan.MaxValue - TimeSpan.FromDays(2) ||
                        captured.At < lastCapture || captured.At > ReadCondition())
                        throw new InvalidOperationException("ConditionObservationTimeInvalid");
                    validCapture = true;
                }
                catch (Exception exception)
                {
                    if (!PublishActiveEvidence(cycle,
                        () => cycle.Add(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract)), exception)) return Primary;
                    validCapture = false;
                }
                if (!validCapture) continue;
                if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
                lastCapture = captured.At;
                // Mandatory failure evaluation even if success has latched or the plan is not yet complete.
                // Paths are local to each prepared tree. Missing failure evidence is Unknown, never the success tree's reads.
                ConditionEvaluation? EvaluateCondition(ConditionSession? session, ConditionObservationUnit? evidence)
                {
                    try { return session?.EvaluateAt(evidence ?? new ConditionObservationUnit([]), captured.At); }
                    catch (Exception exception)
                    {
                        if (!PublishActiveEvidence(cycle,
                            () => cycle.Add(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract)), exception)) return null;
                        return null;
                    }
                }
                var failed = EvaluateCondition(failureSession, captured.Failure);
                if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
                if (!PublishActiveEvidence(cycle, () =>
                {
                    AddError(failed);
                    if (IsTrue(failed)) cycle.Add(new(RunReason.FailureCondition, Phase, RunOrigin.Condition));
                })) return Primary;
                var passed = EvaluateCondition(successSession, captured.Success);
                if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
                lock (arbitrationGate)
                {
                    if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
                    var wakes = new[] { failed?.NextEvaluationAt, passed?.NextEvaluationAt }.Where(x => x.HasValue).ToArray();
                    nextConditionEvaluationAt = wakes.Length == 0 ? null : wakes.Min();
                    AddError(passed);
                    if (IsTrue(passed)) goalVerified = true;
                    if (passed?.Completion == ConditionCompletion.Expired && !goalVerified)
                        cycle.Add(new(RunReason.GoalImpossible, Phase, RunOrigin.Condition));
                    if (currentEvaluation.Value is { } frame) frame.Evidence = cycle.ToArray();
                }
            }
            if (now >= RunningOrigin!.Value + Limits.MaxDuration) cycle.Add(new(RunReason.MaxDuration, Phase, RunOrigin.Clock));
            foreach (var operation in operations.Where(x => x.IsOpen && now >= x.NextDeadline))
            {
                operation.CompleteAt(now);
            }
            if (!DrainPending(cycle)) return Primary;
            CollectCompletionEvidence();
        }
        if (!PublishActiveEvidence(cycle)) return Primary;
        // Sample the exact lifecycle watch after observation/clock processing,
        // before terminal operation closure collects abandoned delivery evidence.
        SampleLifecycle();
        if (!DrainPending(cycle)) return Primary;
        if (!PublishActiveEvidence(cycle)) return Primary;
        var closingSampleComplete = false;
        while (true)
        {
            lock (arbitrationGate)
            {
                if (AbandonedEvaluation || State is ExecutionState.Completing or ExecutionState.Finished) return Primary;
                // The executor timer uses the validated real-clock port. Its
                // nondecreasing watermark latches an observed absolute deadline
                // before timeout candidates can stall during materialization.
                CollectLatchedInterruptions(cycle);
                cycle.AddRange(pendingEvents); pendingEvents.Clear();
                if (cycle.Count != 0 && State == ExecutionState.Running)
                {
                    foreach (var operation in operations.Where(x => x.IsOpen)) operation.CompleteAt(lastReal);
                    cycle.AddRange(pendingEvents); pendingEvents.Clear();
                    CollectCompletionEvidence();
                }
                if (cycle.Count == 0 || closingSampleComplete) return PublishPrimary(cycle, lastReal);
                if (currentEvaluation.Value is { } frame) frame.Evidence = cycle.ToArray();
            }
            // Late token/deadline evidence takes the same closing sample as an
            // ordinary terminal cycle, outside the short data gate. Interruption
            // can revoke a stalled probe; only one closing sample is attempted.
            DrainPostedExceptions(close: true);
            if (!DrainPending(cycle)) return Primary;
            closingSampleComplete = true;
        }
    }
    // Caller holds the publication gate. No provider read or callback delivery.
    private void CollectLatchedInterruptions(List<RunEvent> cycle)
    {
        void Add(RunEvent cause) { if (!cycle.Contains(cause)) cycle.Add(cause); }
        if (callerCancellation.IsCancellationRequested) Add(new(RunReason.Cancelled, Phase, RunOrigin.User));
        if (State == ExecutionState.Running && lastReal >= RunningOrigin!.Value + Limits.MaxDuration)
            Add(new(RunReason.MaxDuration, RunPhase.Execution, RunOrigin.Clock));
        else if (State == ExecutionState.Preparing && lastReal >= preparationOrigin + Limits.PreparationTimeout)
            Add(new(RunReason.PreparationTimeout, RunPhase.Preparation, RunOrigin.Clock));
    }
    private PrimaryResult? PublishPrimary(List<RunEvent> cycle, TimeSpan now)
    {
        // Budget exhaustion is terminal only after the final approved result/observation opportunity.
        // A verified success in that final unit wins over mere exhaustion; errors/cancel/deadline still win.
        if (events.Count + cycle.Count > Limits.MaxEvidenceItems)
        {
            cycle = cycle.OrderBy(x => Priority(x.Reason)).ThenBy(x => x.Reason).ThenBy(x => x.Phase).ThenBy(x => x.Origin).Take(Limits.MaxEvidenceItems - 1).ToList();
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
            frozenGoalVerified = goalVerified;
            Primary = new(Status(chosen.Reason), chosen); State = ExecutionState.Completing;
            Budget.Settle(); foreach (var operation in operations) operation.CompleteAt(now);
        }
        return Primary;
    }
    private void CollectCompletionEvidence(List<RunEvent> cycle, bool executionComplete)
    {
        lock (arbitrationGate)
        {
            if (State != ExecutionState.Running) return;
            void Add(RunReason reason, RunOrigin origin)
            {
                if (!cycle.Any(x => x.Reason == reason && x.Phase == Phase && x.Origin == origin))
                    cycle.Add(new(reason, Phase, origin));
            }
            // Retain the whole Goal fact while a dispatched result or legitimate elapsed wait is pending.
            // Do not abandon a normal in-flight receipt just because its side effect became observable first.
            if (goalVerified && policy == CompletionPolicy.OnGoal)
            {
                approvalsClosing = true;
                foreach (var operation in operations) operation.CloseDispatchForGoal();
            }
            var settling = operations.Any(x => x.AwaitsSettlement);
            if (goalVerified && policy == CompletionPolicy.OnGoal && !settling) Add(RunReason.GoalSatisfied, RunOrigin.Condition);
            if (operations.Any(x => x.IsOpen)) return;
            foreach (var exhausted in closingExhaustions.Concat(Budget.Exhaustions).Distinct().OrderBy(x => x))
                Add(exhausted, RunOrigin.Budget);
            if (executionComplete && !goalVerified) Add(success is null ? RunReason.ExplorationFinished : RunReason.SuccessUnconfirmed, RunOrigin.Runner);
            if (goalVerified && (policy == CompletionPolicy.OnGoal || executionComplete)) Add(RunReason.GoalSatisfied, RunOrigin.Condition);
        }
    }
    private void AddError(List<RunEvent> cycle, ConditionEvaluation? value)
        {
            if (value?.Evaluation.Error is EvaluationError.InvalidConfiguration)
                cycle.Add(new(RunReason.InvalidContract, Phase, RunOrigin.Contract));
            else if (value?.Evaluation.Error is EvaluationError.ObservationContractViolation)
                cycle.Add(new(RunReason.ObservationContractViolation, Phase, RunOrigin.Contract));
    }
    private static bool IsTrue(ConditionEvaluation? value) => value?.Evaluation.Error == EvaluationError.None && value.Evaluation.Truth == TruthValue.True;
    public TimeSpan? NextConditionEvaluationAt => new[] { successSession, failureSession }.Any(x => x is not null) ? nextConditionEvaluationAt : null;
    private TimeSpan? nextConditionEvaluationAt;
    internal void PostException(Exception exception)
        => PostEvidence(exception, null);
    internal void PostProviderException(Exception exception)
        => PostEvidence(exception, (exception as RunFailureException)?.Cause);
    private void PostEvidence(Exception exception, RunEvent? cause)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (postedExceptionGate)
        {
            if (postedExceptionsClosed) return;
            if (cause is not null) postedCausalEvidence = true;
            if (postedExceptions.Count < Limits.MaxEvidenceItems) postedExceptions.Enqueue((exception, cause));
            else postedExceptionsOverflow = true;
        }
    }
    private void DrainPostedExceptions(bool close)
    {
        (Exception Exception, RunEvent? Cause)[] ready;
        lock (postedExceptionGate)
        {
            if (AbandonedEvaluation) return;
            if (close)
            {
                postedExceptionsClosed = true;
            }
            ready = postedExceptions.ToArray();
            if (currentEvaluation.Value is { } frame)
                frame.Diagnostics = frame.Diagnostics.Concat(ready.Select(item => item.Exception)).Take(Limits.MaxEvidenceItems).ToArray();
            // Publish causes before removing their queue ownership or reading diagnostics.
            foreach (var item in ready) if (item.Cause is { } cause) QueuePending(cause);
            if (postedExceptionsOverflow) QueuePending(new(RunReason.ExecutionError, Phase, RunOrigin.Runner));
            postedExceptions.Clear(); postedExceptionsOverflow = false;
            postedCausalEvidence = false;
        }
        // A synchronous watch may stall. It cannot hold the publication gate or
        // prevent executor interruption from revoking this evaluation's authority.
        if (close) SampleLifecycle(closing: true);
        foreach (var item in ready)
        {
            RecordException(item.Exception);
            lock (arbitrationGate)
                if (currentEvaluation.Value is { } frame && !frame.Abandoned)
                    frame.Diagnostics = frame.Diagnostics.Where(itemException => !ReferenceEquals(itemException, item.Exception)).ToArray();
        }
    }
    public void RecordException(Exception exception)
        => RecordExceptionCore(exception, emergency: false);
    private void RecordExceptionCore(Exception exception, bool emergency)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!emergency && (ExecutionDiagnosticsRevoked || State == ExecutionState.Finished)) return;
        if (exception is ClockProviderException) QueueClockRejection();
        var remaining = Limits.MaxEvidenceItems - exceptions.Count;
        if (remaining <= 0) return;
        // Preserve the public boundary first, then prioritize original provider leaves
        // over transport AggregateException wrappers. Traversal has its own finite
        // ceiling, so deeper/wider graphs cannot grow the evidence list or walk forever.
        var wrappers = new List<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var traversalLimit = (int)Math.Min(100_000L, Math.Max(4096L, (long)remaining * 128));
        var frames = new Stack<IEnumerator<Exception>>();
        frames.Push(((IEnumerable<Exception>)[exception]).GetEnumerator());
        var visited = 0;
        try
        {
            while (frames.Count != 0 && exceptions.Count < Limits.MaxEvidenceItems && visited++ < traversalLimit)
            {
                var frame = frames.Peek();
                if (!frame.MoveNext()) { frames.Pop().Dispose(); continue; }
                var current = frame.Current;
                if (!seen.Add(current)) continue;
                if (current is ClockProviderException) QueueClockRejection();
                IEnumerable<Exception>? children = current is AggregateException aggregate ? aggregate.InnerExceptions
                    : current.InnerException is { } inner ? [inner] : null;
                if (children is null || current is AggregateException { InnerExceptions.Count: 0 }) Record(current);
                else
                {
                    if (ReferenceEquals(current, exception)) Record(current);
                    else if (wrappers.Count < remaining) wrappers.Add(current);
                    frames.Push(children.GetEnumerator());
                }
            }
        }
        finally { while (frames.TryPop(out var frame)) frame.Dispose(); }
        foreach (var wrapper in wrappers)
        {
            if (exceptions.Count >= Limits.MaxEvidenceItems) break;
            Record(wrapper);
        }
        void Record(Exception current)
        {
            // Emergency publication must not invoke user-overridden diagnostics.
            var trace = emergency ? new System.Diagnostics.StackTrace(current, true) : null;
            var stack = emergency ? trace!.FrameCount == 0 ? null : trace.ToString().TrimEnd('\r', '\n') : current.StackTrace;
            var evidence = new ExceptionEvidence(current.GetType().FullName ?? current.GetType().Name, stack);
            lock (arbitrationGate)
                if ((emergency || !ExecutionDiagnosticsRevoked && State != ExecutionState.Finished) && exceptions.Count < Limits.MaxEvidenceItems)
                    exceptions.Add(evidence);
        }
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
    internal static int Priority(RunReason reason) => reason switch
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
