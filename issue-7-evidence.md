# Issue #7 implementation evidence

Base: main `9e63f52eea17fc486a9ee7340aa004bea5f9290c`.
Environment: Windows x64, .NET SDK 10.0.401; actual restored Gua.Testing/Gua.Runtime
1.1.1 packages and their native runtime. Core remains native-free.

`dotnet test -c Release --no-restore --logger trx --results-directory artifacts/tests`:
Initial validation passed 16 bridge tests, 42 contract tests and 16 foundation
tests, no skips. After reconnecting, the published HEAD was fast-forwarded without
losing local WIP (the original patch and stash were retained; comparison proved it
was already incorporated). The latest local validation passed 21 real bridge
tests, 42 contract tests and 20 foundation tests, no skips. This includes standard
typed string lists, intermediate change batches, invalid-selector preflight
rejection, both before/after intermediate type mismatches, and lost replies that
remain Pending without resend/re-poll until Runner EndWait. Windows sandbox
HttpListener startup failed in two fault tests; the same unmodified tests passed
with approved loopback access. These local results require new-head real CI and
actual Codex review before merge.

Subsequent real Codex findings added poll/snapshot Observe revision alignment,
shared batch node/event/serialized-byte budgets, optional UI-tree epoch support,
and exact enum identity/member validation for snapshots and both event sides.
The real native bridge tests include a post-poll Notify race, duplicate selector/
event/value ceiling checks, and schema-valid interoperability faults applied to
the actual host responses. No fabricated host execution or successful action
result is used. The expanded bridge suite passed 44 tests, no skips; final-head CI
and Codex re-review must validate these later changes.

Merged latest main ab9c7cbb1c9df877b5f37ca8f3c5f3ad1405844a (#4 assertions), with
both histories retained and no conflicts. Local integration passed bridge44,
contracts528 and foundation22, skip0; the actual native enum observation now also
feeds PreparedAssertion.EvaluateJson with the paired catalog. Further real Codex
findings require pinned UI/world tree validation and bind every event identity
to its enclosing cursor document/profile. Added actual native response fault
tests for a world tree missing required epoch and schema-valid foreign event
source/epoch/Debug profile on a Player bridge. Latest bridge48 passed, skip0,
and boundaries passed. New final-head real CI and actual review remain mandatory.

Further actual review findings added tree revision/context alignment, bounded
failure handling for counters outside UInt64, and ordinal lookup indexes for
matched IDs/entries/nodes. Real cached UI/world trees from earlier revisions of
the same live epoch now produce Stale. Schema-valid overflow faults exercise
subscribe/snapshot/poll and epoch/revision/frame/owner/registration counters.
A real broad UI selector preserves all 50 targets and 50 changes. Bridge58 passed
locally, skip0, boundaries passed; previous merged Core suites passed contracts528
and foundation22. New final-head CI and Codex review are required.
Further completed review found unconverted event counters, collection-level
Stale loss and flat state keys containing dots. All Observe metadata counters
now validate UInt64 before exposure; target/tree inconsistencies remain Stale;
object state suffixes resolve as whole keys. Real native schema-valid event
overflow faults cover all nine counters, missing/duplicate UI and Object IDs
preserve Stale, and actual object state keys with one/repeated dots retain values.
Bridge75 passed locally with zero skips and boundaries passed. New exact-head
real CI and actual Codex review remain required.
`scripts/check-boundaries.ps1`: passed. Later PR CI supplies final-head evidence;
these local results are not a substitute for real CI or Codex GitHub review.

| Requirement | Actual measured scope |
| --- | --- |
| OPEN-01/03/04, OBS-002/005, VALUE-005 | Actual packaged native bridge to existing managed client: schema/profile checks, typed enum Value/catalog, same-name standard/Observe separation, owner/registration replacement, frame/revision/epoch identity, gap/getter/reset handling |
| AT-OBS-006, PLANNER-001/003 | Player bridge suppresses private registration/value/catalog; nonexistent vs unpublished named reads have the same safe feedback. Wrong profile refuses values. Node cap is truncated. No general Debug-to-Planner projection is claimed |
| AT-BOUND-003 | Public adapters only resolve/read published targets and UI/Semantic/Raw Input; no internal World mutation API or general command tunnel. Cleanup/reset is refused as Planner input |
| AT-ACTION-001 | Repeated same attack uses distinct execution/request IDs; repeated execution ID does not enqueue twice. UI selector freshly resolves replacement and multiple matches refuse |
| AT-ACTION-002 | Native host input completion is represented alone; no damage/Goal/causality result exists in action adapter. Full independent combat fixture acceptance remains #16 integration |
| AT-ACTION-003 | Real native click is consumed and purchase transaction counter commits; proxy drops only its real enqueue reply. Independent ingress=1/transactions=1 and no pending duplicate prove no resend. Separate consumed-completion response loss remains unconfirmed |
| ACTION-005/006, START-004/005 | Native revision guard rejects stale map, original host failure code survives, timed-out enqueue can still execute, disposal releases owned input resources and leaves runtime/other owner operational |
| PACK-002/FIX-002 | Tests reference published 1.1.1 packages; real WebSocket/native bridge with fault firing counters, no fake transport success. External engine/Runner/fixture integration remains open |

The test-only `BridgeFaultProxy` is the #16 seam: it forwards each request exactly
once to the actual packaged native bridge and calls `(command, response)` after
real host response/before downstream responsewrite. Trusted fixture callback can
commit a consumed purchase and record facts before dropping the reply. It is not
packaged in product or Planner-visible.

External guarded input/UI is a real upstream API blocker detailed in
[docs/gua-bridge.md](docs/gua-bridge.md). No local success is presented as external
Godot/Unity acceptance. This PR is partial issue #7 progress and has no closing
keyword. Full scope and outstanding acceptance remain on issue #7.
