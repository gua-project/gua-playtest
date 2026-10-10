# Planner exchange and adoption boundary (issue #11)

## 提案を操作権限に変える境界

Planner は「公開された店のボタンを押す」と提案できますが、合否、権限、現在の対象を決定しません。観測後にボタンが差し替わった場合、JSONの形が正しくても古い対象へ入力してはいけません。Runner は完了した一件の応答を照合し、許可・対象・定義・時計・予算を確認して採用し、送信直前にも再確認します。

[PlannerGate.Begin / Adopt](../src/Gua.Playtest.Runner/Planning/PlannerGate.cs)から [PlannerContracts](../src/Gua.Playtest.Runner/Planning/PlannerContracts.cs)の `ProjectedPlannerState`、`PlannerRequest`、`IPlannerAuthority` を読んでください。公開投影には目的・承認された観測・残予算を渡し、採点の期待値やfixtureの秘密を渡しません。[PlannerTurn.AwaitAsync](../src/Gua.Playtest.Runner/Planning/PlannerTurn.cs)は応答待ちにも失敗条件・期限・取消を監視します。[PlannerGateTests](../tests/Gua.Playtest.Foundation.Tests/PlannerGateTests.cs)が古い応答、二重採用、部分実行、公開範囲の拒否例です。

gate の確認だけでは、確認とホストenqueueの間の競合や実Plannerのファイルアクセスを隔離できません。具体的なguardと隔離の残条件は下記と [gua-bridge.md](gua-bridge.md)を参照してください。形式検査、採用、実Codex接続の役割は [開発者ガイド](developer-guide.ja.md)で説明します。

`Gua.Playtest.Runner.Planning` is owned by the serialized Run driver. Core's
`PlannerExchange.Validate` validates a **completed** UTF-8 JSON document with
the existing bounded decoder, offline pinned schemas and semantic validator.
It performs no file/network/native/Planner invocation. Invalid diagnostics are
fixed codes without response bytes or exception text. Stream fragments never
grant authority. This is the existing exchange format, not a second Decision.

## Public projection

`PlannerGate(run, realClock, authority, runId, publicObjective, effectiveLimits)`
accepts the objective and resolved secret-free limits only. Its advertised Run
ceilings must equal the execution owner's limits. `Begin(ProjectedPlannerState,
recovering)` consumes #6's one active `PlannerPermit`; the request ID is generated
by the Run owner.
Request namespace/counter, active request, feedback and recovery/unsafe state are
attached to the Run lifetime. Reconstructing a gate cannot reuse an ID, reset
recovery accounting, forget feedback or reopen uncertain dispatch authority.
`CopyInput()` returns a defensive copy of objective, real limits,
remaining duration/actions/decisions after this request, bounded structured
feedback, approved public observation and original Gua action definitions.
Recovery request/retry decision remainder is clamped to the recovery allowance,
including the current request; it never advertises the larger general budget.

The trusted projection must come from #7's independently configured Planner
observation profile, never by filtering a Debug connection after a read. Its
explicit `PublicReads` and `PublicWaitConditions` are host approvals, not a copy
of Goal success/failure, expected values, setup, fixture or private judgement
reads. Observation reads outside that scope are refused. Available/unavailable/
omitted/truncated/stale/gap and genuine empty collections retain their distinct
wire states. Values cannot be invented for incomplete reads. Lost delta baseline
sets `IPlannerAuthority.RequiresResynchronization`; the host must resubscribe and
supply the new full boundary before opening another request. The gate never
reconstructs missing history. Source identity/time/profile stay in the existing
observation schema. Host projection/redaction is still required before sending;
schemas and C# assembly separation are not a technical sandbox.

## One completed proposal and current authority

`Adopt(request, completedJson, cancellationToken)` consumes the request once, including invalid
proposals. It matches run/request/observation IDs and rejects closed, cancelled,
duplicate and expired responses. Invalid unsent responses carry `RetryAllowed`
only while #6's real decision/action/recovery budgets allow another request.
Each recovery permanently consumes that budget. No retry revives a cancelled
permit, and the final valid request can still approve its operation. Recovery
classification is sticky through rejected retries, even when the caller omits
the recovering flag on the next request; only confirmed approved work ends it.
Approval failure always closes the request permit. Expiry during validation is
PlannerTimeout, with no advertised retry; timeout evidence stays in the arbiter.

The pinned schema has exactly execute(single/timed), observe, finite wait or
finish. Unknown fields, multiple kinds, internal commands, confirmed/owner,
shell/URL/file requests and reset/cleanup inputs are refused. Observe reads and
wait conditions must match the independently approved public scope. A proposed
wait or goalClaimed finish does not grade the Goal. Finish receives one finite
final observation opportunity; the driver then reports execution complete to
the existing machine arbiter.

`IPlannerAuthority` is a trusted **host validation port**, not a new game
Capability. Its seven independent checks cover current permissions, original
Gua Action definition/type/range/active/confirmation policy, context/epoch/profile,
freshly resolved targets, actual clock/timing capability and owned input boundary.
`CheckInputBoundary` must use actual Gua input semantics: reject a single leaving
holds, and require timed holds to close within their approved segment. Secrets
and consent come only from approved host policy. A description is not consent;
old confirmed evidence is not approval. Relevant prerequisites, not a revision
counter alone, determine freshness. Ports must return promptly; exceptions fail
closed. Exceptions retain diagnostic evidence and arbitrate ExecutionError with
Host origin, never a Planner invalid-output retry. Each check receives copies so
it cannot rewrite the approved proposal.

Only after these checks does the permit reserve the entire segment atomically.
Existing Gua hard ceilings and stricter segment/lateness/execution/cleanup/wait
limits remain in force. `ApprovedDecision.BeginDispatch(index)` repeats current
checks for the entire approved proposal before #6 marks that request Uncertain.
It requires the next ordered input index and refuses duplicates/reordering.
It does not require neutral input between requests of the same approved segment,
allowing the approved release. New decisions always require neutral input.
Approved authority remains bound to the caller cancellation token after the
Planner turn returns. Cancellation revocation and dispatch commit use an atomic state;
the callback only revokes the lease, never mutates the Run or interrupts a backend.
The serialized dispatch owner arbitrates cancellation before invoking transport.
Callbacks do not block on host checks. References are weak and completion
unregisters without joining callbacks, so a long-lived caller source cannot retain completed Runs.
`ConfirmSent` changes evidence without charging twice;
`ConfirmResult` checks #6's finite result deadline. The operation-level result
cannot be confirmed until every reserved input has
crossed dispatch, preserving ActionUnconfirmed when a segment stops partially.
`Complete` distinguishes
NotSent, SentUnconfirmed, PartialExecution and Confirmed. Uncertain/partial work
closes new Planner requests even before the arbiter receives ActionUnconfirmed;
there is no automatic resend. Only reserved requests can be refunded.

The gate does not make a check/send race atomic. #7's concrete guarded transport
must compare current epoch/profile/definitions while enqueuing/consuming. Gua
1.1.1's external unguarded route cannot satisfy this; upstream PR 180 is not a
published capability. No unrestricted transport fallback is introduced.

## Monitoring, faults and diagnostics

`PlannerTurn.AwaitAsync` runs the existing #6 `RunMonitor` while a Planner thinks.
All clock arguments are bound to the Run owner's validated real/condition clocks; a
second supplied clock cannot replace deadline authority. A completed backend reply
is confirmed on the serialized monitor before its post-work capture join, without
adopting or dispatching. This closes PlannerTimeout and starts the finite WaitTimeout
for fresh synchronization/adoption. The default feed joins the underlying old capture
before requesting a new unit; this module never infers independent capture capability.
Failure conditions, condition timers, real deadlines and cancellation retain
their machine-owned arbitration. On interruption Run authority closes first,
then a finite owner-scoped input release is attempted, then cancellation is
requested from the actual Planner. A noncooperative late result has no authority.
Trusted drivers may use ConfirmResponse only for actual completed backend evidence,
before fresh capture; it grants no approval and cannot rebase an existing window.
Expired active adoption closes the permit and carries PlannerTimeout or confirmed
WaitExpired to arbitration. Adoption faults also arbitrate pending clock/contract
evidence before release. Only the release attempt uses a fresh physical monotonic
cleanup clock, so an invalid execution clock cannot skip or extend cleanup.
Cancellation arriving after the final monitor unit or during adoption checks is
arbitrated before returning operation authority. Any ready terminal adoption
failure retains its priority over cancellation; approved unsent work is revoked
before the same finite input-release/backend-interruption sequence.
Cancellation callback faults are contained and recorded without replacing the
primary outcome or owned-input-release evidence.
The returned release flag must enter the driver's mandatory cleanup/postprocessing
evidence; it is never manufactured from lease expiry. The callback may release
only acquired Run-owned inputs. Synchronous blocking providers cannot be preempted
by a task deadline and are not conforming adapters.

`PlannerReplyStatus` distinguishes completed output, usage limit, connection
failure and invalid output, without leaking exception text. `PlannerTurn.Events`
maps these to #6's canonical PlannerUsageLimit/PlannerConnectionFailure/
PlannerOutputInvalid with Planner origin, never Scenario invalid. Those canonical
reasons come from the reviewed #6 merge on main (2668926); this module does not redefine
their priority. A noncontinuable rejected proposal exposes `TerminalEvent`.
PlannerTurn sends that ready terminal event directly to the arbiter after the
preceding monitor's fresh units. It never opens a new capture/join after closing
the request permit, which could extend a finite request wait to MaxDuration.
Exhausted invalid-output retries terminate as
PlannerOutputInvalid, rather than continuing without a request.

`PlannerDecisionReference(RunId, DecisionRequestId, BasedOnObservationId, Code)`
is the immutable host artifact reference. #9 maps IDs and the structured enum
code into sanitized storage. The pinned response has no arbitrary free-text
rationale; raw response/exception/host facts are never feedback. #12 owns Explore
recovery; Replay never invokes this gate to repair its fixed Plan. #13 owns actual
App Server version, structured-output transport and sandbox enforcement.

## Acceptance and remaining evidence

`PlannerGateTests` exercises AT-ACTION-004/006 and AT-PLANNER-002/004/005/006/007
controller paths: one request, stale correlation, duplicate/partial JSON, finite
retry, final permit, separate current checks, atomic budget reservation,
unsent/uncertain/partial feedback, immutable projection, public read/condition
scope, resync requirement and release-before-cancel against a blocked Planner.
Projection tests cover controller portions of AT-PLANNER-003/OBS-006. Existing
machine conditions preserve GOAL-003/ASSERT-010 and schema control restrictions.
These are deterministic native-free tests, not real Codex/game E2E evidence.

Keep issue #11 open for full acceptance. Remaining obligations:

- #6 final reviewed/merged initial boundary and execution policy; integrate final main and
  rerun checks/review after #6 merges.
- #7/#10/#15: actual current-definition/type/value/consent/input-lifetime checks,
  guarded enqueue, public source/profile/epoch identity, real resubscribe,
  action confirmation and permitted read/condition composition (INPUT-001/003,
  CLOCK-005, BOUND-003, AT-PLANNER-001/003/005/006).
- #8/#12/#15: actual Explore scheduling and forwarding adoption TerminalEvent,
  ordinary finish observation/completion, mandatory cleanup flags and Replay
  no-repair behavior (ACTION-004, GOAL-003, PLANNER-010).
- #9/#13: send-before redaction plus persistence redaction, actual App Server
  failure/usage reporting, OS-specific sandbox/network/approval/direct-Gua
  bypass prevention (OPEN-09, PLANNER-009/010). A mock gateway is not real E2E.

CI/review evidence belongs to the PR's actual final HEAD. No issue auto-closure,
release, credentials or unrecognized software installation is performed here.
