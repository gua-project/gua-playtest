# Run execution contract: OPEN-06 and runtime OPEN-10

## Run の合否と終了処理を追う

Run は一回の試行です。Created → Preparing → Running → Completing → Finished の状態と、Passed／Failedなどの判定結果を区別します。購入成功と失敗・取消が同じ評価単位に届いた場合、通知の到着順で結果を選ばず、下記の優先規則で処理します。入力の受付、完了、Goalの成立も別の証拠です。

[RunExecutor.ExecuteAsync](../src/Gua.Playtest.Runner/Execution/RunExecutor.cs)の準備・実行・終了処理を入口に、[RunSession.Evaluate](../src/Gua.Playtest.Runner/Execution/RunSession.cs)で一次結果の確定、[RunMonitor.AwaitAsync](../src/Gua.Playtest.Runner/Execution/RunMonitor.cs)で待機中の観測・時計・取消を追います。[OwnedCleanup](../src/Gua.Playtest.Runner/Execution/OwnedCleanup.cs)は確定済みの結果を保持し、自分が取得した入力や資源を解放します。

OnGoal／AfterPlanの厳密な完了条件、送信後不明、最後の操作の予算、時計の境界は下記の契約を参照してください。[RunTests](../tests/Gua.Playtest.Foundation.Tests/RunTests.cs)、[RunReviewTests](../tests/Gua.Playtest.Foundation.Tests/RunReviewTests.cs)、[RunClockBoundaryTests](../tests/Gua.Playtest.Foundation.Tests/RunClockBoundaryTests.cs)が再現例です。保存は [artifacts.md](artifacts.md)、起動前の責務は [preparation.md](preparation.md)、読む順番は [開発者ガイド](developer-guide.ja.md)にあります。

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

Only short data publication holds the evaluation arbitration gate; its clocks, lifecycle watches
and condition providers run outside it. An active unit retains a bounded immutable
snapshot of already-authoritative causes before draining pending evidence. If an
executor interruption arrives while that unit is stalled, it revokes the unit's
publication authority and finalizes from those known causes and the interruption
without joining the dependency. Resource cleanup still runs, and the abandoned unit
cannot rewrite primary, the event ledger or diagnostic snapshots when it returns.
Unfinished observations never manufacture Goal confirmation.
After interruption closes the final approved opportunity, the same completion
collector retains newly eligible budget exhaustion, previously verified Goal and
explicitly declared plan completion in the terminal ledger. Failure, cancellation
and global-deadline priorities still choose primary; unfinished work cannot supply
a new completion declaration. The executor binds its caller token to the session;
primary publication samples IsCancellationRequested
directly, so delayed cancellation callbacks cannot permit an in-flight Goal to pass.
The same gate samples the nondecreasing validated real-time watermark: the executor
timer's already-observed absolute deadline cannot be hidden by delayed timeout
candidate materialization. This needs no additional provider-clock read. Late token
or deadline evidence follows the ordinary terminal closure, completion recollection
and closing lifecycle sample before primary publication; probes remain outside the
short gate and interruption can still revoke a stalled sample.
Each processed capture publishes its success-side errors and verified Goal under
that gate before validating the next capture. An interrupted later read cannot
erase an earlier unit's contract failure. Result confirmation reads clocks outside
the gate, then rechecks the operation's open state, Run state and deadline under
the same gate as closure; a clock read that returns after closure cannot confirm
a receipt or reopen its observation opportunity. Emergency diagnostics share the
ordinary bounded traversal that prioritizes original leaves over deep wrappers,
while using CLR stack capture without invoking overridden diagnostic getters.
Certified initial data is classified once against the already-validated certificate
timestamp, retaining its copied failure/Goal facts even if cancellation was already
requested. Fresh provider clock validation, observation registration and lifecycle
arbitration run in a finite initial owner stage under the original Running deadline,
before driver work can start. Cancellation/deadline can finalize and release resources
while that stage remains stalled; the same absolute deadline then bounds driver work.
Goal-side reservation refunds and all completion collection share the closure gate.
A revoked evaluation keeps its revocation in the returning driver's execution context,
so post-return execution diagnostics cannot change a snapshot during cleanup; cleanup's
separate executor context may still retain its own bounded diagnostics.
Preparation, initial registration and driver callbacks also carry an internal
execution-stage marker across asynchronous continuations. Once primary publication
starts cleanup, diagnostics from these abandoned stages are rejected, including a
late registration exception that never entered an evaluation. The finalizer and
cleanup run outside that marker and retain their own diagnostic authority.
Initial registration failures transfer their typed cause and original exception to
the session-owned queue before the next provider read. Interruption therefore keeps
an already-known failure even while that read remains stalled; posts after terminal
closure cannot change the confirmed snapshot.
Driver-completion arbitration remains inside the original finite driver waiter.
The executor's interrupted Running catch path settles only already-owned causes,
diagnostics and the validated time watermark; it starts no new provider sample.
This permits resource release even when both work and an unstarted lifecycle probe
would block. Ordinary bounded evaluations retain their lifecycle samples.
The interrupted Running finalizer uses emergency CLR stack capture for its own
exception too, so an overridden diagnostic getter cannot become its next dependency.
Capture failure additions, active evidence snapshots and original diagnostic
ownership transfer share one publication critical section, before stack extraction
or another condition read. Interruption cannot split those updates.
Operation-list pruning and final admission checks share the same closure gate as
interruption. Expired permits and rejected startup observations publish through the
bounded pending-evidence queue; a late producer cannot append after terminal closure.
A known false driver result is included in the same unit before that sampling, even when the caller was already cancelled.
Provider callbacks still only enqueue evidence. Existing priorities, provider/driver
cancellation behavior, absolute deadlines and finite cleanup bounds remain unchanged.

| Priority | Reasons in tie-break order | Status |
| --- | --- | --- |
| 0 | InvalidContract, ObservationContractViolation | Invalid |
| 1 | FailureCondition, PreparationTimeout, ActionFailed, ActionUnconfirmed, PlannerTimeout, ExecutionError, PlannerOutputInvalid, PlannerUsageLimit, PlannerConnectionFailure | Failed |
| 2 | Cancelled | Aborted |
| 3 | MaxDuration | TimedOut |
| 4 | GoalImpossible, ActionsExhausted, DecisionsExhausted, RecoveryExhausted, WaitExpired, SuccessUnconfirmed, ExplorationFinished | Failed, except ExplorationFinished and no-success budget completion are Unverified |
| 5 | GoalSatisfied | Passed |

Terminal budget exhaustion becomes eligible after the final approved operation/decision and finite observation opportunity have ended. If that final unit establishes verified success and the completion policy is met, exhaustion is recorded but is not a terminal predicate. This preserves the last purchase success without changing the priority of *terminal* budget failure. Contract/failure/cancellation/global deadline always retain their priorities over that success. Other unused causes remain in the event ledger. Independent literal 32-row tests cover every contract/failure/cancellation/deadline/success combination, both callback orders, and same-class tie tests.

No-success exploration cannot produce Passed: finish and ordinary budget completion are Unverified. With success configured, incomplete or Unknown success is Failed/SuccessUnconfirmed on ordinary completion, never an escape to Unverified. Actual errors, cancellation and global timeout retain their own statuses for either kind of Goal. Public candidate events cannot assert GoalSatisfied; only machine evaluation can verify it.

`OnGoal` retains whole-success facts but settles outstanding approved operations before confirmation. `AfterPlan` additionally requires the driver to report full execution completion after intermediates, normal input releases and action confirmations. It continues mandatory failure monitoring after Goal success. Completing closes dispatch and primary results permanently; later success/cancellation/invalid observations cannot rewrite them.

RunExecutor closes new preparation/driver work, Planner requests, approvals and dispatch as soon as cancellation or its safety deadline is observed. Its independent controller also bounds synchronous provider invocation and startup validation before those providers return a Task. The existing owner may settle evidence from work already in flight for one finite share of the existing CleanupTimeout. This starts a single cleanup epoch at interruption: elapsed real and independent time consume that same budget, and the remaining time is reserved for snapshot/resource cleanup. Neither settlement nor a recovered clock restarts the budget or extends preparation/Running deadlines. No new source observation is started to settle interrupted work.

If the owner remains blocked when its evidence share expires, finalization uses only already-owned immutable evidence, including copied certified initial failure/contract facts, pending provider failures and sent/uncertain operations. Unfinished work cannot confirm success. Returning abandoned providers cannot reopen authority, resurrect Running, or change primary/events/diagnostic snapshots. This bounds the caller's wait; it does not preempt arbitrary synchronous provider code. Providers remain responsible for ending their own work and releasing any late acquisition rejected by cleanup registration. Standalone FiniteOperation calls retain their existing promptly-returning-delegate requirement.
A completed provider failure transfers its normalized typed cause and original diagnostics to the session before cancellation callbacks can block the finite waiter's finally path. Preparation and driver faults follow the same rule. A physical watchdog interval alone cannot establish clock failure from a stale watermark: the controller asynchronously validates the outstanding/current clock sample within the existing evidence share, with no synchronous signal join. A healthy sample reaching the original deadline retains PreparationTimeout/MaxDuration; a still-early or unresponsive clock fails closed. Clock validation, remaining evidence settlement and cleanup consume the same interruption epoch. New owner stages asynchronously await classification and cannot start providers during that validation.
Already-completed preparation results remain controller-owned before the finite waiter's cancellation cleanup: false readiness and a returned certificate's copied initial failure/contract facts survive a blocked cancellation callback. This inspection only reads an already-successful Task and the executor's own projection; it starts no provider or Goal observation. Dispatch repeats raw cancellation, executor closure and the validated result-deadline watermark checks under the final reservation gate after lifecycle sampling. A null lifecycle reply cannot reopen authority or consume an Uncertain attempt after closure.
Normal and emergency diagnostic traversal share per-Run weak exception-identity recording. An exception node is marked only when its evidence is actually appended under the publication gate, so emergency traversal can retain a node whose normal StackTrace getter is still blocked. Already-recorded nodes consume neither evidence slots nor deferred-wrapper allowance. A visited recorded node refreshes its existing diagnostic row so a rethrow retains its current stack without consuming another slot. Distinct exception objects remain distinct even when their type/stack evidence is equal. Successful identity registrations stay bounded by MaxEvidenceItems and do not strongly retain provider graphs; emergency CLR stack capture and late-publication guards remain unchanged.
Before abandoning a stalled owner, the controller collects already-faulted/cancelled preparation and driver Tasks using their captured supplied tokens and the same normalization as the finite waiter. It never awaits an unfinished provider in this collection and posts original cause/diagnostics without fresh provider or clock calls. Private deadline identity uses weak owner provenance rather than Exception.Data, so an overridden provider metadata getter cannot become the finalizer's next dependency. An interrupted cleanup clock probe that loses its finite allowance (or ends unsuccessfully) records CleanupClockInvalid and still attempts releases within the remaining shared epoch. Late clock success cannot erase that issue or turn incomplete postprocessing into exit0.
## Evidence, timers and clocks

`Evaluate(successUnit, capturedAt, candidates, cancelled, executionComplete, failureUnit)` takes separate tree-local path maps. Both maps use the same trusted condition-clock capture boundary. Missing maps/leaves are Unknown, never cached reads from the other tree. Capture timestamps before origin, regressing or in the future fail closed. A delayed capture can establish within at its genuine timestamp while the current real delivery time still reaches MaxDuration. Polling delivery time is not a source timestamp. `NextConditionEvaluationAt` exposes the earliest #5 timer; `NextRealEvaluationAt` includes the global deadline and active approved work deadlines.

`RunMonitor.AwaitAsync` keeps captures, change notifications, #5 condition timers, cancellation and real safety deadlines alive while Planner/action/wait work returns evidence. `IRunObservationFeed.CaptureAsync` returns `RunObservation(CapturedAt, Success, Failure)` and `WaitForChangeAsync` waits for relevant changes. Every timer wake recaptures evidence; it never evaluates a cached hold. Obsolete waits are cancelled, faults observed, and late work returns no Run authority. Source continuity/profile/epoch/cursor coverage still belong to #7. The work callback must not mutate RunSession: completed work evidence and captured conditions go through the same arbiter. The caller's resultEvents adapter must include all host failures and contract violations, not just a successful reply.

Real and condition clock instances are supplied by the trusted execution policy, never a Planner. The real clock must advance independently of simulation. `FiniteOperation.RunAsync` bounds async work and caller cancellation even when its returned task ignores cancellation. It cannot preempt a synchronous blocking delegate, so providers must return their task promptly; concrete process/transport termination is owned by their adapter. No reconnect, wait or cleanup extends the Running deadline.

## Budgets and approved work

`BudgetSnapshot(Actions, ReservedActions, Decisions, ActionDecisions, RecoveryDecisions)` keeps counts distinct. Each Planner request increments Decisions; a recovery request also increments RecoveryDecisions permanently. One operation reservation increments ActionDecisions, regardless of the individual requests in its segment. A segment reserves its whole count before dispatch; insufficient remaining capacity reserves nothing. Each individual request enters Uncertain immediately before transport invocation and consumes an attempt; authoritative sent acknowledgement changes it to Sent without counting again. Only pre-dispatch Reserved entries can become NotSent/refund. A missing response, timeout, abort or possible applied side effect cannot become NotSent or authorize automatic retry. Failure still consumes sent/uncertain attempts.

`RequestPlanner(recovering)` yields one `PlannerPermit`, with a finite deadline and one consumed proposal. Completing a Planner request at its deadline preserves PlannerTimeout and cannot approve transport. The final allowed Planner request can approve one operation even when the decision limit was reached by that request. `ApproveOperation` provides the equivalent Replay/approved work boundary without an AI request. Only one active operation/decision is allowed, so recovery/Planner cannot interrupt an approved segment or wait. New approvals close when a budget limit is reached and cannot reopen after cancellation of reserved requests. The closing exhaustion reason survives a refund and becomes terminal once the final approved opportunity closes, unless its final observation verifies success. Already reserved segment requests can finish under their existing authorization until that operation's deadline; no new operation is added while closing.

`ApprovedOperation` has a finite result deadline (ActionTimeout) and a separately bounded final observation/wait window (WaitTimeout). Both are clamped to the original global deadline. `BeginDispatch` checks the state and the independent action/result deadline; confirmation closes dispatch authority while the observation window may remain open. `ConfirmResult` refuses late replies; `Complete` ends the approved opportunity and refunds only entries that never crossed dispatch. At an expired unconfirmed sent/uncertain operation, ActionUnconfirmed wins over success. A confirmed result can continue observing within its already approved window. A wait cannot extend itself; after exhaustion no new wait is approved. Cleanup uses its own finite authority and consumes no play/action budget.

## Explicit finite limits and cleanup

`PlannerPermit.ApproveElapsedWait(duration, observationWindow)` authorizes one
duration wait followed by a finite observation allowance. Each argument must be
positive and no larger than the existing WaitTimeout. The absolute real deadline
is approval time plus those two allowances, clamped to the original Running
deadline. Confirmation also requires the authoritative condition clock to reach
the approved elapsed boundary; an early timer cannot manufacture completion.
After actual confirmation the next observation deadline is no later than either
that original absolute deadline or receipt time plus observationWindow. Repeated
confirmation cannot rebase it. A paused condition clock remains bounded by real
time. This additive API changes no action-result, Planner-response or conditional
wait deadline, budget counter, permission or wire field.

`RunLimits` requires all durations (maxDuration, preparation, cleanup, Planner, wait, action result), action/decision/recovery counts and evidence count. There are no runtime duration or budget defaults. #15 resolves stricter Scenario/Environment duration/actions and maps the existing Environment limits, including TraceMaxEvents, into these values. Action result timeout is an explicit stricter execution ceiling within the existing wait/communication policy, not a new unbounded configuration fallback. The implementation accepts positive durations up to one day, evidence counts 32..100000, at most 1000 individual requests per segment (Gua hard ceiling), and at most 1000 registered cleanup obligations. These are hard runtime ceilings, not example fixture values or performance guarantees. Evidence overflow fails closed and preserves a bounded prefix plus final deciding unit. Closed operation/reservation histories are removed from active controller storage; caller-owned reservation evidence remains readable.

## Certified initial Running boundary (START-005)

Endpoint exclusion uses the terminal `OwnershipRelease` cleanup stage after all
`InputRelease` and `ResourceRelease` obligations, regardless of registration time.
An optional trusted pure synchronous closeOwnership callback registered with
OwnershipRelease runs at cleanup entry, outside the registration lock, revoking
provider starts before diagnostics or release. Terminal endpoint removal remains
separate. The owner skips the release action if any preceding resource release is unconfirmed;
that flag remains fail closed even after the diagnostic evidence limit is reached.
Ownership callbacks otherwise share the original cleanup deadline and receive the
same immediate bounded attempt after expiry. Failure maps to `ResourceReleaseUnconfirmed`.

The approved ledger requires the same evaluation observation for prerequisites, Running boundary and initial success/failure. An arbitrary preparation snapshot cannot be reused by backdating only the condition clock. The production executor overload takes `prepare(RunSession, OwnedCleanup, CancellationToken) -> ValueTask<RunStartBoundary>` and evaluates the exact certificate's initial maps before the execution callback or any Planner request. A null certificate fails closed and cannot downgrade to the bool preparation path.

After Setup, identity, compatibility and mode availability are ready, #8 calls `run.ArmRunningBoundary()` once. This creates a session-owned `RunStartCapture` capability and a unique RequestId with current real/condition readiness floors. #7/#8 must correlate a **new synchronized subscription/capture** to this request. `capture.Certify(captureRequestId, capturedRealAt, initialRunObservation, synchronizationEvidence, preconditionsSatisfied)` checks matching request, true prerequisite confirmation, nonempty bounded synchronization evidence, timestamps at/after readiness and at/before current clocks, and preparation capture deadline. It retains the exact immutable initial evidence unit. False prerequisites, an earlier preparation snapshot, mismatched request, future/regressing clocks and cross-Run reuse are rejected. Arming is a trusted controller assertion of completed Setup/compatibility, not a Planner permission or a string-based proof of host capabilities. Only the actual adapter can establish source/epoch/cursor synchronization and compare prerequisites in that same unit; a fabricated evidence ID is not source continuity.

`BeginRunning(RunStartBoundary)` accepts only the opaque one-use certificate owned by that session. Both condition sessions start at its condition timestamp and the real maxDuration origin is its **paired real capture timestamp**. Delivery lag consumes the original real budget; it never resets the deadline at delivery. No hold can start before the initial observed True at this boundary. The provider must retain every intervening relevant change or invalidate continuity. The bool preparation/parameterless BeginRunning path remains for trusted controller tests and simpler adapters; once a certified capture is armed it cannot bypass certification. The production #8 START-005 path uses the certificate overload, not an unrelated second snapshot.

PlannerOutputInvalid/PlannerUsageLimit/PlannerConnectionFailure represent noncontinuable Planner failures (PLANNER-010), after bounded unsent rejection/retry policy. They are Failed/origin Planner, never Scenario Invalid. Monitor cancellation is advisory: #11 must perform bounded owner input release before actual backend interruption. Its backend cancellation source is separate from the monitor's token so caller cancellation cannot bypass that ordering.

Trusted adapters can throw `RunFailureException(RunEvent cause, Exception? innerException)` or derive a stage-specific exception. Executor/monitor preserve its guarded cause and original type/stack, including an inner provider exception, instead of changing every host failure into Runner ExecutionError/exit10. A Host-origin preparation ExecutionError remains Failed/exit1; unknown Runner bugs remain exit10. The exception cannot claim machine success and never mutates the session from a late provider callback.

`OwnedCleanup.Register(stage, action)` registers only acquired owner-scoped obligations. Stages are PrimarySnapshot, Diagnostics, Artifacts, InputRelease, ResourceRelease, OwnershipRelease. Diagnostics run before resources are released. Each obligation returns authoritative confirmation; lease expiry is not input release evidence. False/fault/timeout maps to DiagnosticsFailed/ArtifactFailed/InputReleaseUnconfirmed/ResourceReleaseUnconfirmed. One overall cleanup deadline is shared fairly among remaining obligations, preventing a hanging earlier artifact from consuming every later release opportunity. Caller cancellation skips cancellable diagnostic/artifact work but never skips owned input/resource release attempts. If the overall cleanup observation budget has expired (including scheduler overshoot), each remaining owned input/resource callback receives one immediate release attempt with no additional waiting window: already-completed confirmation can be retained, otherwise release remains unconfirmed and its token is immediately cancelled. Timeout evidence remains mandatory; late confirmation cannot repair the frozen outcome. No attached process or other owner's input becomes Runner-owned by registration convention; adapters must register precisely the owned resource, never a general kill/reset command.

`RunSnapshot` freezes Primary/Events/Exceptions immediately after confirmation. Both executor overloads and OwnedCleanup.CompleteAsync accept optional `confirmPrimary(snapshot, token)` as a final parameter after cancellationToken. This trusted callback runs before diagnostics/artifacts/release using a fresh finite token even after caller cancellation. It shares the overall cleanup deadline fairly; false/fault/timeout records PrimarySnapshotFailed without changing primary or starving later owned release. There are at most 1000 registered obligations plus this one hook.

The expired-cleanup immediate-release rule also covers an already-invoked owned
input/resource/ownership release. If its finite waiter expires, an already-completed
true result confirms the release without another invocation or any additional wait.
CleanupTimeout remains recorded: retaining this physical release fact does not assert
on-time completion or successful postprocessing. Pending, false and faulty releases
remain unconfirmed; diagnostics, artifacts and primary snapshots keep their strict
finite-wait result rules. Completion after the frozen outcome cannot repair it.

`RunOutcome` freezes primary, events, exceptions and postprocessing issues. Cancellation after confirmation and cleanup failures are postprocessing only. Passed plus incomplete mandatory postprocessing maps to exit 11. A cancellation marker alone does not fabricate unmet mandatory work; it is still recorded separately. Otherwise exit codes are 0 Passed, 1 Failed, 2 Invalid, 3 TimedOut, 4 Aborted, 5 Unverified; Runner-origin ExecutionError is 10. Failed and other primary codes are preserved when artifacts/cleanup fail. CLI execution wiring remains #15; a unit exit projection is not evidence of a released command.

## Acceptance boundaries: keep issue #6 open

Clock authority also covers the reads inside condition Start/EvaluateAt and finite operation timing: both receive session validation proxies. Arbitrary Elapsed provider exceptions queue InvalidContract/Clock before propagating their original exception evidence; repeated failure during fault-path evaluation still freezes the primary result and reaches cleanup. A successful timer wake is a hint and must be followed by a current validated Elapsed read. An early successful wake reschedules the remainder against independent real elapsed time while still racing work/caller cancellation; if the authoritative clock remains before its deadline after that finite independent interval it fails closed as a clock provider failure. Synchronous/asynchronous authoritative delay failures carry typed clock evidence to the owner before arbitration; expected supplied-token cancellation remains cancellation. Cleanup keeps the independent hard share alive after a provider wake and validates clock progress on the owner, so a premature provider timer cannot cancel a cooperative owned release early. Planner approval carries its specific response/observation deadline through the final clock read that reserves the operation; crossing that boundary creates no action reservation.

RunTests supplies deterministic fake-clock state/priority/budget/condition integration, failure injection during preparation/report/release, noncooperative task deadlines, paused simulation and live failure monitoring during a blocked Planner. No sleeps or relaxed existing assertions are used. This resolves the controller contract portions of AT-GOAL-002, AT-TIME-004, AT-RUN-001/002/003/005/006, AT-BUDGET-001/002/003, AT-CLOCK-002 and OPEN-06/10.

Remaining real acceptance:

- #7/#8/#11: actual approved profile/read/epoch/source capture boundaries, mandatory monitoring through real host/Planner waits, atomic all-segment gate and transport sent/unsent/uncertain evidence (AT-ASSERT-009, AT-ACTION-003/005, AT-START-002/006, AT-PLANNER-007).
- #8/#10/#12: real Explore/Replay driver, afterPlan completion including necessary release/intermediate checks, preserved clock/lease semantics and recovery boundaries (AT-RUN-004, AT-REPLAY-004, AT-CLOCK-006, AT-STALL-004).
- #9/#15: actual artifact/Trace persistence, redaction and CLI/result serialization/exit11; safe preservation of original exception evidence (AT-RUN-005/006, AT-FILE-005, AT-CLI-005).
- #16: independent full bridge/engine FIX-007 transaction measurements; unit or Fake success does not satisfy this.

Finite waits preserve an already-completed provider fault in the same terminal unit before choosing cancellation or a timer. Ready work also participates when capture/change notification infrastructure faults. Cancellation callback exceptions during abandoned-work cancellation cannot replace the chosen result; monitor cancellation callback evidence is retained separately. A zero remaining execution budget is MaxDuration, and false preparation still retains same-cycle cancellation evidence. Certified boundaries require both initial observation maps.
A completed capture retains its authoritative maps when delivery is interrupted by cancellation or a finite timer; those maps, ready work, cancellation and current deadlines share one Evaluate unit. This retains failure/contract evidence without extending action authority. Wake provider cancellation callbacks also use independent guarded caller-token propagation.
Certified start must also be consumed before the current real preparation deadline; an on-time capture cannot authorize a late state transition. Preparation deadline exceptions classify as PreparationTimeout. Live captures require both maps just like the initial certificate, including captures retained during interruption. Public candidate validation precedes consuming pending operation evidence. Caller-thread callback faults enter a bounded queue and only the monitor owner writes exception evidence.
A terminal unit closes active operations while the session is still Running, collecting abandoned sent/uncertain delivery evidence before choosing the immutable primary. Already-cancelled monitor entry closes the run before invoking work. Completed work event projection is validated once and cached; rejected public events become an adapter ExecutionError instead of being retried during fault finalization.

Preparation preserves already-completed provider evidence when caller cancellation wins, including false readiness and the certified initial failure unit. Owner preparation, execution, capture and cleanup shares use absolute deadlines through FiniteOperation.RunUntilAsync; an elapsed remainder is never rebased onto a later start. Independent failed or internally cancelled wake tasks already complete before obsolete-wait cancellation join ready work in arbitration.

The asynchronous boundaries follow one evidence policy:

| Boundary | Before invocation | Completed evidence when interrupted | Failure arbitration |
| --- | --- | --- | --- |
| Preparation | Absolute preparation deadline and caller token rechecked after registration | False readiness and certified initial failure/contract evidence survive cancel and timer; expired preparation never enters Running or claims a goal | Same terminal priority table, with PreparationTimeout/cancellation retained |
| Driver | Absolute Running deadline and caller token rechecked after registration | Completed driver declaration survives cancel/timer; false completion remains ExecutionError | Ready declaration, provider failure, cancellation and deadline share Evaluate |
| Live capture | Absolute next evaluation deadline, guarded cancellation | Completed maps/time survive cancel/timer and both maps are required | Ready work and captured conditions share Evaluate |
| Monitored work | Caller cancellation/current deadline rechecked after registration | Work projection is bounded, validated and cached once | Completed fault and independent losing-wake fault are retained |
| Terminal closure | No new approvals/dispatch after closure | Abandoned sent/uncertain results plus newly eligible budget/goal/completion evidence are collected | One immutable primary selection after collection |

FiniteOperation and RunMonitor use shared provider-cancellation normalization: an exception carrying the cancelled supplied token (or the caller token) is caller interruption only while that caller is cancelled; an unrelated or absent token remains provider failure, even with concurrent caller cancellation. Original internal-cancellation type/stack remain evidence. Callback threads only propagate guarded cancellation and enqueue faults; the serialized owner evaluates them. The literal 12-row preparation/driver/capture/work cancellation-provenance table and synchronous/asynchronous fault cases complement the independent priority/state tables. Standalone finite waits and monitor providers require promptly returning synchronous callbacks. RunExecutor additionally isolates its owner so cancellation/deadline finalization and cleanup do not join a blocked callback; its evidence settlement uses the single budget described above.

The RunSession's real and condition clocks are authoritative for executor, monitor and cleanup. The existing method clock arguments remain for source compatibility and cannot substitute a different epoch for an absolute session deadline. All exhausted ceilings are recorded, including retained causes whose reservations were refunded; the stable reason tie-break chooses among all eligible budgets.

Safety-deadline TimeoutExceptions carry private owner provenance; provider-thrown timeouts, including identical message text, remain infrastructure failures with original evidence. The same rule applies to preparation, execution, captures and cleanup. Pending terminal operation evidence closes new approvals immediately, before its next Evaluate.

After monitored work is observed complete, CaptureAsync performs one new synchronized capture covering through that invocation. The delivered earlier unit and final unit are retained and evaluated chronologically in one terminal cycle, with ready work, cancellation, faults and current deadlines. Earlier failures cannot be erased by a later healthy sample, and an earlier goal cannot finalize ahead of a later failure. The final capture shares the absolute remaining deadline; interruption retains any completed final unit and cannot grant success from only a stale sample. Providers must supply an actual fresh source synchronization, not return a cached unit or substitute polling-delivery time. Real provider/guard integration acceptance remains open.

Before false driver fallback confirms its primary, it samples current caller cancellation again in the same evaluation unit as ExecutionError. Clock validation during preparation records InvalidContract/Clock before propagating its unchanged exception, including a reset between the shared condition-origin read and either monitor's start. Owned finalization still runs and no driver or goal authority is established after rejected start clocks.

A rejected real-clock read retains InvalidContract/Clock immediately, including reads outside Evaluate. A later recovered reading cannot erase this cause. The catch-path arbiter retains any other evidence queued during the read and records the contract violation once. Concurrent reads compare against the validated baseline captured before each provider invocation and publish a nondecreasing maximum under the short data gate. An older valid sample returning after a newer one cannot falsely reject a monotonic clock or move its authoritative watermark backward; a sequential reset still rejects.

Monitored work launch and owner remaining-time checks use the session's validated real-clock read. Invalid clocks close authority before providers start. Every captured source timestamp is nonnegative, representable, monotonic since Running's source origin, and no later than the current condition clock, even without condition trees; rejection retains exception evidence. Evidence truncation applies the complete priority/reason/phase/origin tie-break. Undefined completion policies are rejected by the constructor before any Run authority.

An in-flight first capture races completed work. When work wins, the owner cancels the obsolete finite wrapper and retains any completed unit/fault. A default feed must also end its underlying invocation within the existing deadline before fresh post-work synchronization. Explicit independent feeds may replace immediately without waiting for old delivery because their request scopes cannot serialize or affect one another. An unending default call expires that same observation window without starting an overlapping request. Condition wake delays use the validated session clock. FiniteOperation's optional owner evidence sink drains guarded callback faults only after joining caller registration; executor/capture supply RunSession evidence and cleanup supplies stage issues, retaining inner exception type/stack. Standalone legacy calls retain their finite outcome behavior. Successful stages already complete when cancellation wins retain their result if still within the deadline; actual incomplete or faulty stages remain failures.

OnGoal retains verified success while a dispatched action result or an approved elapsed wait is pending. Normal on-time receipt or elapsed-wait confirmation settles that opportunity before success; result expiry still records ActionUnconfirmed or WaitExpired. Unsent work and pending Planner proposals can close immediately when the Goal is verified. Other terminal causes still close every operation and collect abandoned sent/uncertain evidence before one arbiter; AfterPlan retains its declaration requirement. Cleanup uses validated real-clock reads and records CleanupClockInvalid separately from the immutable primary. A rejected or stalled epoch switches permanently to a monotonic safety clock anchored at cleanup entry and clamped to the last valid time. Independent wakes enforce finite shares, owner reads collect timer hints, and an invalid epoch cannot reset elapsed cleanup time or skip finalization by overflow. Release attempts use their remaining fair share; actual exhausted time still records unconfirmed obligations.

RunMonitor's optional confirmCompletedWork hook runs once on the serialized owner after a successful result and bounded event projection, before post-work capture. Trusted adapters use it only to confirm the existing response/result; it must not authorize new work. PlannerPermit.ConfirmResponse records an on-time response without consuming the permit; its later approved action still uses the global Running deadline and ordinary budgets. Actions use ConfirmResult and retain their original observation window. Neither callback claims a goal or certifies unknown delivery. Late confirmation remains rejected. Expired preparation boundary recovery validates current condition time and preserves clock rejection separately from malformed observations.

Cleanup timer-provider faults are queued, switch the owner to its independent clock, and continue the current fair share against the hard wake. Helpers join after finite cancellation before issue freeze; no timer thread writes session evidence. All postprocessing issues share MaxEvidenceItems, reserving one EvidenceLimitExceeded marker; reaching the cap does not skip release attempts.

The confirmation hook blocks new planner requests, permit approval, transport dispatch and monitored launch while it runs. A confirmed Planner response gets one WaitTimeout observation window from its owner receipt time, clamped to MaxDuration; repeated confirmation cannot rebase it. Expiry of that window is WaitExpired, not PlannerTimeout. Confirmation records result receipt in real time and never substitutes for a source capture timestamp.


Monitor real/condition timer targets are retained separately even when providers reuse a completed Task. The owner validates each selected wake target, reschedules an early hint against independent elapsed while preserving work/change/cancellation races, and rejects a clock that remains early after that finite interval. Cleanup counts independent elapsed on every read, including immediately completed callbacks; its origin is the first validated cleanup read, and source progress cannot repeatedly reset the physical baseline or grant a fresh overall budget.


Superseding a finite capture wrapper does not prove the underlying feed call ended. Default feeds are joined within the existing owner deadline before any fresh post-work capture; requested source-token cancellation is an ended call, while other faults remain evidence. Only a trusted IIndependentRunObservationFeed may replace without joining: each invocation must own an independent request scope, synchronize fresh source state, and never queue behind the obsolete call or let its cancellation affect the replacement. This capability is not inferred for Gua1.1.1; source/guard acceptance remains open. Startup source timestamps below readiness floors or outside current validated clocks queue ObservationContractViolation/Preparation/Contract while retaining original InvalidOperationException type/stack. Missing initial observation or success/failure maps queues the same cause while retaining ArgumentNullException evidence; Runner API misuse remains separate.

Trusted lifecycle integration retains an already-armed exact process watch on the opaque start boundary. The serialized owner samples it after initial observation/clock processing in the same first Running arbitration cycle, before choosing primary; initial goal success cannot skip known host failure. This is internal adapter evidence, not a new public readiness/permission capability. Late provider callbacks use a bounded exception posting queue. Only the owner drains posts and freezes registration before primary confirmation, preventing worker mutation of pending events or snapshot lists. Drained original exceptions remain owned by the active evaluation until diagnostics are recorded; interruption retains their bounded original type/stack graph before abandoning a stalled lifecycle sample.
The same internal queue distinguishes secondary callback diagnostics from actual provider failure evidence. A typed provider failure ready before closure carries its guarded cause into owner arbitration; callback faults cannot claim that cause. Queue overflow latches a constant-size fail-closed flag under the same lock: the owner emits Runner ExecutionError rather than allowing goal success after causal evidence was dropped. The queue and recorded evidence retain their existing limits. Both diagnostic and cause registration close atomically before the immutable primary. Later posts cannot change the primary, operation accounting or exception snapshots.

Final primary confirmation closes the posted-evidence gate under its short publication lock, then samples the already-armed exact lifecycle watch outside that lock. An earlier Running poll is not final readiness proof; the close sample retains known lifecycle failure before primary assignment. Executor interruption may revoke a stalled close sample without joining the watch and confirms only already-known evidence. A late revoked sample cannot rewrite the primary.
Queued causal evidence and queue overflow immediately close ActionsClosing, Planner requests, operation approval and dispatch through the shared terminal-evidence gate, before the next owner evaluation. The owner samples the already-armed exact lifecycle watch before taking the posting lock: a completed exit/fault closes authority without depending on a feed call or async notification. A stalled watch retains one in-flight invocation; interruption arbitrates known evidence without joining it or launching another call behind it. Final pure-data authority checks and reservation transitions share the posting lock; provider clock/watch reads precede that lock, and queued evidence is checked again afterward. This linearizes internal reservation authority with causal posting; the external transport's check/enqueue guard remains separate. Diagnostic-only posts preserve those gates; workers never mutate Run state or budgets.

When OnGoal latches while a sent result is pending, the existing operation closes
its Reserved suffix as NotSent immediately. `DispatchClosedByGoal` identifies
that owner decision; no new action or Planner request is admitted. Sent or
uncertain deliveries retain their original settlement deadline. A trusted adapter
may confirm only its actual executed prefix after result and neutral-input
confirmation; a Goal-cancelled suffix is not an executed action. Other partial
execution remains unconfirmed.

`RunSession.ApproveObservation(window)` grants the trusted owner one finite
observation-only opportunity at an already reached boundary. Complete the prior
operation before requesting it. The window must be positive and no greater than
WaitTimeout, and is clamped to the original Run deadline. It charges no action,
decision or recovery budget and cannot dispatch; exhausted action budgets stay
closed. Each reached boundary obtains its own window. Pending observation
settlement blocks OnGoal completion until actual `ConfirmResult`, expiry, or
higher-priority terminal evidence. Early owner completion without confirmation
retains WaitExpired. Ordinary arbitration, cancellation and global deadlines
remain authoritative.

The certified RunExecutor overload accepts an optional bounded owner hook to register
a reached initial observation after BeginRunning and before the certified unit's first
arbitration. Existing callers omit it. The hook may register observation settlement;
it must not dispatch or request Planner work. A thrown hook exception enters the same
arbiter with the original initial failure, cancellation, deadline and armed lifecycle
evidence. It cannot bypass preparation certification or replace that source unit.
