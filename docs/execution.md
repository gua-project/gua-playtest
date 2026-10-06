# Run execution contract: OPEN-06 and runtime OPEN-10

Issue #6 provides native-free `Gua.Playtest.Runner.Execution`. One trusted execution owner serializes all calls. Transport callbacks return evidence; they cannot mutate primary results. `RunSession` is a per-Run object, never a shared cross-Run controller. #8/#10 assemble Explore/Replay adapters; #11 authorizes current reads/actions. No unavailable upstream guarded API or new Gua release is inferred. Gua.Testing remains pinned to 1.1.1; capability checks and unsupported action rejection stay in the concrete adapter/gate.

## States and result boundary

| Current | Operation | Next |
| --- | --- | --- |
| Created | BeginPreparation | Preparing |
| Created | Evaluate with contract/error/cancellation evidence | Completing |
| Preparing | BeginRunning before preparation deadline | Running |
| Preparing | Evaluate terminal evidence, including deadline | Completing |
| Running | Evaluate without terminal candidate | Running |
| Running | Evaluate terminal candidate | Completing |
| Completing | late Evaluate | Completing, original result retained |
| Completing | OwnedCleanup.CompleteAsync | Finished |
| Finished | late Evaluate | Finished, original result retained |

Other transition requests fail with fixed codes. `BeginRunning` uses one real Running origin and one condition-clock origin shared by success and failure. Preparation consumes its own real deadline and cannot backdate Running. Reconnect cannot reset the origins. `RunExecutor.ExecuteAsync` bounds preparation/driver execution and finalizes on exceptions, cancellation, false preparation, ordinary completion and deadline. The driver must feed live monitoring and register only resources actually acquired. A provider acquiring a resource after cancellation must release it itself if cleanup registration is closed; a timeout cannot safely reclaim arbitrary uncooperative external work.

`PrimaryResult` separates `Status`, `Cause(Phase, Origin, Reason)` and a fixed display `Message`. `RunPhase` is Preparation/Execution/PostProcessing. `RunOrigin` is Contract/Condition/Host/Planner/Budget/Clock/User/Runner. `RunReason` declaration order is the normative stable tie-break below. Exception evidence retains original type/stack separately; no exception message or source Value enters planner feedback. #9 owns safe artifact storage/redaction and #15 maps these typed results to the existing result schema. The module does not add a second Trace format.

## Complete candidate priority and tie-break

One `Evaluate` call reads real time once and handles a complete evaluation unit, including current cancellation/global deadline. The owner must not call Evaluate separately for callbacks that belong to one unit. Both condition trees are evaluated even after Goal success, and errors remain candidates. The earliest enum in a class wins, then phase enum, then origin enum; event arrival order cannot choose the result.

| Priority | Reasons in tie-break order | Status |
| --- | --- | --- |
| 0 | InvalidContract, ObservationContractViolation | Invalid |
| 1 | FailureCondition, PreparationTimeout, ActionFailed, ActionUnconfirmed, PlannerTimeout, ExecutionError | Failed |
| 2 | Cancelled | Aborted |
| 3 | MaxDuration | TimedOut |
| 4 | GoalImpossible, ActionsExhausted, DecisionsExhausted, RecoveryExhausted, WaitExpired, SuccessUnconfirmed, ExplorationFinished | Failed, except ExplorationFinished and no-success budget completion are Unverified |
| 5 | GoalSatisfied | Passed |

Terminal budget exhaustion becomes eligible after the final approved operation/decision and finite observation opportunity have ended. If that final unit establishes verified success and the completion policy is met, exhaustion is recorded but is not a terminal predicate. This preserves the last purchase success without changing the priority of *terminal* budget failure. Contract/failure/cancellation/global deadline always retain their priorities over that success. Other unused causes remain in the event ledger. Independent literal 32-row tests cover every contract/failure/cancellation/deadline/success combination, both callback orders, and same-class tie tests.

No-success exploration cannot produce Passed: finish and ordinary budget completion are Unverified. With success configured, incomplete or Unknown success is Failed/SuccessUnconfirmed on ordinary completion, never an escape to Unverified. Actual errors, cancellation and global timeout retain their own statuses for either kind of Goal. Public candidate events cannot assert GoalSatisfied; only machine evaluation can verify it.

`OnGoal` retains whole-success facts but settles outstanding approved operations before confirmation. `AfterPlan` additionally requires the driver to report full execution completion after intermediates, normal input releases and action confirmations. It continues mandatory failure monitoring after Goal success. Completing closes dispatch and primary results permanently; later success/cancellation/invalid observations cannot rewrite them.

## Evidence, timers and clocks

`Evaluate(successUnit, capturedAt, candidates, cancelled, executionComplete, failureUnit)` takes separate tree-local path maps. Both maps use the same trusted condition-clock capture boundary. Missing maps/leaves are Unknown, never cached reads from the other tree. Capture timestamps before origin, regressing or in the future fail closed. A delayed capture can establish within at its genuine timestamp while the current real delivery time still reaches MaxDuration. Polling delivery time is not a source timestamp. `NextConditionEvaluationAt` exposes the earliest #5 timer; `NextRealEvaluationAt` includes the global deadline and active approved work deadlines.

`RunMonitor.AwaitAsync` keeps captures, change notifications, #5 condition timers, cancellation and real safety deadlines alive while Planner/action/wait work returns evidence. `IRunObservationFeed.CaptureAsync` returns `RunObservation(CapturedAt, Success, Failure)` and `WaitForChangeAsync` waits for relevant changes. Every timer wake recaptures evidence; it never evaluates a cached hold. Obsolete waits are cancelled, faults observed, and late work returns no Run authority. Source continuity/profile/epoch/cursor coverage still belong to #7. The work callback must not mutate RunSession: completed work evidence and captured conditions go through the same arbiter. The caller's resultEvents adapter must include all host failures and contract violations, not just a successful reply.

Real and condition clock instances are supplied by the trusted execution policy, never a Planner. The real clock must advance independently of simulation. `FiniteOperation.RunAsync` bounds async work and caller cancellation even when its returned task ignores cancellation. It cannot preempt a synchronous blocking delegate, so providers must return their task promptly; concrete process/transport termination is owned by their adapter. No reconnect, wait or cleanup extends the Running deadline.

## Budgets and approved work

`BudgetSnapshot(Actions, ReservedActions, Decisions, ActionDecisions, RecoveryDecisions)` keeps counts distinct. Each Planner request increments Decisions; a recovery request also increments RecoveryDecisions permanently. One operation reservation increments ActionDecisions, regardless of the individual requests in its segment. A segment reserves its whole count before dispatch; insufficient remaining capacity reserves nothing. Each individual request enters Uncertain immediately before transport invocation and consumes an attempt; authoritative sent acknowledgement changes it to Sent without counting again. Only pre-dispatch Reserved entries can become NotSent/refund. A missing response, timeout, abort or possible applied side effect cannot become NotSent or authorize automatic retry. Failure still consumes sent/uncertain attempts.

`RequestPlanner(recovering)` yields one `PlannerPermit`, with a finite deadline and one consumed proposal. The final allowed Planner request can approve one operation even when the decision limit was reached by that request. `ApproveOperation` provides the equivalent Replay/approved work boundary without an AI request. Only one active operation/decision is allowed, so recovery/Planner cannot interrupt an approved segment or wait. New approvals close when a budget limit is reached and cannot reopen after cancellation of reserved requests. Already reserved segment requests can finish under their existing authorization until that operation's deadline; no new operation is added while closing.

`ApprovedOperation` has a finite result deadline (ActionTimeout) and a separately bounded final observation/wait window (WaitTimeout). Both are clamped to the original global deadline. `BeginDispatch` checks the state and deadline. `ConfirmResult` refuses late replies; `Complete` ends the approved opportunity and refunds only entries that never crossed dispatch. At an expired unconfirmed sent/uncertain operation, ActionUnconfirmed wins over success. A confirmed result can continue observing within its already approved window. A wait cannot extend itself; after exhaustion no new wait is approved. Cleanup uses its own finite authority and consumes no play/action budget.

## Explicit finite limits and cleanup

`RunLimits` requires all durations (maxDuration, preparation, cleanup, Planner, wait, action result), action/decision/recovery counts and evidence count. There are no runtime duration or budget defaults. #15 resolves stricter Scenario/Environment duration/actions and maps the existing Environment limits, including TraceMaxEvents, into these values. Action result timeout is an explicit stricter execution ceiling within the existing wait/communication policy, not a new unbounded configuration fallback. The implementation accepts positive durations up to one day, evidence counts 32..100000, at most 1000 individual requests per segment (Gua hard ceiling), and at most 1000 registered cleanup obligations. These are hard runtime ceilings, not example fixture values or performance guarantees. Evidence overflow fails closed and preserves a bounded prefix plus final deciding unit. Closed operation/reservation histories are removed from active controller storage; caller-owned reservation evidence remains readable.

`OwnedCleanup.Register(stage, action)` registers only acquired owner-scoped obligations. Stages are Diagnostics, Artifacts, InputRelease, ResourceRelease. Diagnostics run before resources are released. Each obligation returns authoritative confirmation; lease expiry is not input release evidence. False/fault/timeout maps to DiagnosticsFailed/ArtifactFailed/InputReleaseUnconfirmed/ResourceReleaseUnconfirmed. One overall cleanup deadline is shared fairly among remaining obligations, preventing a hanging earlier artifact from consuming every later release opportunity. Caller cancellation skips cancellable diagnostic/artifact work but never skips owned input/resource release attempts. No attached process or other owner's input becomes Runner-owned by registration convention; adapters must register precisely the owned resource, never a general kill/reset command.

`RunOutcome` freezes primary, events, exceptions and postprocessing issues. Cancellation after confirmation and cleanup failures are postprocessing only. Passed plus any incomplete mandatory postprocessing maps to exit 11. Otherwise exit codes are 0 Passed, 1 Failed, 2 Invalid, 3 TimedOut, 4 Aborted, 5 Unverified; Runner-origin ExecutionError is 10. Failed and other primary codes are preserved when artifacts/cleanup fail. CLI execution wiring remains #15; a unit exit projection is not evidence of a released command.

## Acceptance boundaries: keep issue #6 open

RunTests supplies deterministic fake-clock state/priority/budget/condition integration, failure injection during preparation/report/release, noncooperative task deadlines, paused simulation and live failure monitoring during a blocked Planner. No sleeps or relaxed existing assertions are used. This resolves the controller contract portions of AT-GOAL-002, AT-TIME-004, AT-RUN-001/002/003/005/006, AT-BUDGET-001/002/003, AT-CLOCK-002 and OPEN-06/10.

Remaining real acceptance:

- #7/#8/#11: actual approved profile/read/epoch/source capture boundaries, mandatory monitoring through real host/Planner waits, atomic all-segment gate and transport sent/unsent/uncertain evidence (AT-ASSERT-009, AT-ACTION-003/005, AT-START-002/006, AT-PLANNER-007).
- #8/#10/#12: real Explore/Replay driver, afterPlan completion including necessary release/intermediate checks, preserved clock/lease semantics and recovery boundaries (AT-RUN-004, AT-REPLAY-004, AT-CLOCK-006, AT-STALL-004).
- #9/#15: actual artifact/Trace persistence, redaction and CLI/result serialization/exit11; safe preservation of original exception evidence (AT-RUN-005/006, AT-FILE-005, AT-CLI-005).
- #16: independent full bridge/engine FIX-007 transaction measurements; unit or Fake success does not satisfy this.

The draft PR and exact-head CI/Codex review gate prove this change's checks, not those real integrations. No automatic issue closure is requested.
