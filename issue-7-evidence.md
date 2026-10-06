# Issue #7 implementation evidence

Base: main `9e63f52eea17fc486a9ee7340aa004bea5f9290c`.
Environment: Windows x64, .NET SDK 10.0.401; actual restored Gua.Testing/Gua.Runtime
1.1.1 packages and their native runtime. Core remains native-free.

`dotnet test -c Release --no-restore --logger trx --results-directory artifacts/tests`:
13 bridge tests, 42 contract tests and 16 foundation tests passed, no skips.
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
