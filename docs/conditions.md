# Condition engine: OPEN-05

This is the normative condition contract for issue #5. `docs/assertions.md` owns comparison semantics and ordinary three-valued truth. No Run status, budget priority, action authorization or profile escalation is defined here.

## Preparation and evaluation units

`PreparedCondition.Create(JsonObject, AssertionOptions, EnumCatalogSnapshot?)` validates the complete composite/target structure and eagerly calls the existing `AssertionPreparation`/`PreparedAssertion` APIs for every comparison, including unselected and timed branches. Invalid configuration throws a fixed-code `AssertionConfigurationException` before a session starts. A prepared tree is immutable and reusable across independent sessions. Duration and target-count numbers use the exact original wire-integer parser; decimal/binary64 rounding cannot admit a fractional configuration. Original JSON and Value bytes are retained.

`Leaves` returns trusted `ConditionLeafRequest(ConditionPath, DefinitionJson)` requests for the observation adapter. Paths start at `$`, with `/conditions/N` and `/condition` for groups and time wrappers. These requests contain configured selectors/reads and are not public planner feedback.

`Start(IClock)` or `Start(IClock, TimeSpan origin)` takes the explicit monotonic origin at Running or at a Replay/intermediate wait point's arrival. All nested time nodes share this origin. No preparation time or unmonitored historical time contributes to a hold. The clock is the execution policy's condition clock; global real-time safety/budget clocks remain #6's responsibility. The explicit-origin overload lets success/failure monitors share exactly one authoritative Running/wait boundary. The owner serializes calls. A backward/out-of-range clock is rejected with a fixed message.

`ConditionSession.Evaluate(ConditionObservationUnit)` reads the clock exactly once. `EvaluateAt(unit, TimeSpan evaluatedAt)` instead uses the trusted unit capture/boundary timestamp and checks it against the current clock; timestamps before the origin/last unit or in the future are rejected. This allows initial within=0 to use the actual Running boundary despite delivery latency. A delayed unit starts a hold at its first observed True timestamp, with no credit for earlier unmonitored time. The adapter must provide every relevant intervening unit or invalidate continuity; the Run owner still applies current real-time deadlines. All leaf observations must belong to the same trusted capture/subscription boundary. The unit copies its path dictionary, leaf observations copy their target arrays, and no omitted path is taken from an older unit. Missing paths yield Unknown/Missing. The adapter supplies a complete-search flag, an opaque scope identity, target lifetime identities and original `ValueJson` plus approved enum catalog, or a fixed unavailable code. Scope identity must include source/session/epoch/profile/owner/query lifetime; target identity must include registration/cache lifetime, not just a reusable target name. Duplicate/empty identities and contradictory unavailable data are observation violations. A world singleton must also have an explicit identity.

Every comparison Value in every leaf is evaluated before aggregation, even when another branch has succeeded, the search is incomplete, or a temporal fact is latched/expired. Invalid Values/catalog changes remain observation violations and override decisive truth. Missing, getter errors, stale, gap, truncation and regex timeout remain Unknown. Raw wire Values enter `PreparedAssertion.EvaluateJson` directly; no intermediate number conversion or alternate compare implementation is used.

## Quantifiers and target counts

| Search | one | any | all | none |
| --- | --- | --- | --- | --- |
| Complete, zero matches | False | False | False | True |
| Complete, one match | comparison | comparison | comparison | negated comparison |
| Complete, multiple matches | TargetAmbiguous violation | existing TruthLogic.Any | existing TruthLogic.All | negated TruthLogic.Any |
| Incomplete, zero/one match | Unknown | True if a returned match is True; otherwise Unknown | Unknown | Unknown |
| Incomplete, multiple matches | TargetAmbiguous violation | True if a returned match is True; otherwise Unknown | Unknown | Unknown |

Errors in returned comparisons override this table. Unknown is preserved by negation. No first-match reduction occurs. `TargetAmbiguous` is a condition observation violation; the action owner must stop before sending input (#7/#11), which is not claimed as tested here.

`targets` conditions evaluate Selector match count, not a collection Value's element count. `exists` with a returned match is True even for incomplete search; `notExists` with a returned match is False. With zero matches both require complete search. All six `count*` operators require complete search. Value assertion `count*` remains the #4 collection comparison, quantified over targets in the ordinary way.

## Truth, completion and expiry

`ConditionEvaluation` contains only `EvaluationResult`, `Completion`, `EvaluatedAt`, and `NextEvaluationAt`. It contains no Value, identity, selector, pattern or exception text. Actual projection to a profile remains the adapter/gate's responsibility (#7/#11).

| Completion | Meaning |
| --- | --- |
| Open | Ordinary condition/group can change on a later unit; current True is usable but not latched. |
| Pending | A time condition has not established its fact. Known inner True is gated False until its hold completes; inner Unknown remains Unknown. An entirely temporal unresolved group is Pending. |
| Satisfied | A temporal fact is retained. Only time nodes latch; a group is permanently Satisfied when all children are Satisfied (all), or any child is Satisfied (any). |
| Expired | Local temporal attainment is impossible. An expired Unknown leaf retains Unknown with Expired as a separate dimension. Parents treat error-free Expired children as unavailable future alternatives with effective False. |

An all with any Expired child propagates impossibility. An any is Expired only if every child is Expired. Ordinary children stay Open, and another branch can still become True after a sibling expires. Expiry of a failure condition is not evidence that failure occurred. Mandatory monitoring violations cannot be hidden by a retained success fact. #6 decides terminal outcomes from success/failure conditions and global deadlines; this module never converts Expired into Scenario failure/timeout.

An observation violation preserves its error/Unknown truth while still advancing temporal state independently: it resets an unfinished hold, keeps the inclusive deadline wake before/at the boundary, and latches Expired after it. Established Satisfied/Expired state is not erased by an error. Parent completion propagation still runs, but an error always prevents a truth result from establishing success or failure. The canonical OPEN-05 entry in `03-open-decisions.md` records this condition-level resolution and distinguishes downstream Run/bridge acceptance still pending.

`tests/fixtures/playtest/condition-state-table.json` fixes every ordered pair of Open/Pending/Satisfied/Expired with independent all/any expectations. The complete nine-pair ordinary three-valued table, quantifier table, and count table are literal test data. Existing `temporal-golden.json` expectations are unchanged.

## within/for state machine and continuity

`withinMilliseconds` is an inclusive deadline for the beginning of a successful hold. `forMilliseconds` is its continuous duration; absent for means zero, absent within means no local start deadline. A time node must specify at least one. At `within=5000, for=2000`, a start at 4500 completes at 6500 and a start at exactly 5000 completes at 7000. A later start is inadmissible. A False/Unknown/violation, missing interval, scope/target membership replacement or changed witness resets the unfinished hold. After the start deadline a reset makes the node Expired. Unknown does not extend the deadline.

The first known True starts at the current evaluation time. A later True retains its start only with matching witness identity and `ContinuousFromPrevious=true`. This flag is trusted adapter evidence covering the entire previous interval **through the current evaluation**, including any timer wake. It means no relevant intermediate changes were omitted from evaluation and no observation continuity was lost. Repeated polling endpoints, equal Values, notification silence, or a cached last sample cannot supply this evidence. The provider must either feed all relevant changes as evaluation units or invalidate this flag. Gaps and unavailable reads reset the hold. A new True after reset can begin a new hold only inside within. Membership changes conservatively reset even any; quantified any resets when its proving target set changes, and group any resets when its proving branch set changes. Each witness includes its condition path so different assertions on the same target cannot borrow one another's hold.

Established within/for facts survive subsequent False/Unknown observations, including missing evidence. Subsequent observation violations still surface and override success. Ordinary all does not combine historical True observations. Independent timed children can establish facts at different times; a time wrapper around all instead requires the whole group to be simultaneously True and continuously maintained.

## Timers and downstream ownership

`NextEvaluationAt` is an absolute condition-clock time: the active hold's completion, the earliest nested timer, or one clock tick after an inactive inclusive within deadline. Exact-boundary False/Unknown remains Pending until the first later tick, allowing starts at the inclusive boundary. Satisfied/Expired composite alternatives remove unused timers. `ConditionTimer.WaitAsync` waits through the supplied `IClock`; it does not fabricate an observation or resume a cached hold. The Run owner races timers with change notifications, cancellation and its real-time deadlines, cancels obsolete waits and supplies fresh evidence. Fake-clock tests advance directly or via this helper, with no sleep-based oracle.

The API does not impose a finite global wait, extend maxDuration, reserve segment budgets, arbitrate simultaneous success/failure, authorize actions, or decide cleanup outcomes. Those belong to #6 and the action/gate owners. It also does not infer upstream Observe history or unpublished bridge APIs.

## Acceptance and remaining integration

Local tests cover AT-ASSERT-004/005/007/008/009/010 and AT-TIME-001/002/003 at the condition boundary, including raw wire integrity, independent state tables, unknown expiry, timers, target replacement, nested origins, latched and unused branch violations. Fixed-code result projection contains no source Values/identities. This is not proof of an action remaining unsent or a profile-authorized planner payload.

Issue #5 remains open for:

- #7/#11: actual Selector/Observe adapter continuity, epoch/profile/owner/catalog changes, same-unit captures, profile-safe feedback, ambiguous-target action suppression (AT-ASSERT-004/010, AT-START-005).
- #6/#8/#10: actual Running/wait placement, timer/notification scheduling, failure-monitor handling, same-cycle global deadline priority, final sent-operation wait/budget behavior and condition vs real-time clock policy (AT-TIME-004, AT-BUDGET-003, AT-CLOCK-002, AT-START-005).
- #16 and those owners: execute the condition API in the real bridge/engine cross-spec fixture and retain independent transaction measurements (AT-FIX-007). Unit/Fake success does not close these acceptance gaps.
