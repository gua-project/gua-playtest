# Fixture and independent evidence contract (#16)

This increment supplies test-only scripted Planner and immutable, preauthorized fault hooks,
fixed case data, and an external Node oracle. It does **not** complete issue #16.
No contractFake run establishes real bridge, real input, Godot, Unity or product E2E acceptance.
The five product assemblies do not ship these helpers. They are compiled only into Foundation.Tests.

## Trusted setup and actual fault boundary

Trusted setup freezes fixtureVersion, actual game buildId, case ID, seed, clock, fault plan,
and tolerance before starting. Record the SHA-256 of `tests/fixtures/playtest/cases.json`
before the run; pass that previously recorded hash to the external oracle. Changed bytes fail.
Position tolerances in movement descriptors are provisional **test specifications** (0.05 game
units), not measured engine accuracy or a product default. They must be agreed with the
engine variant before any acceptance run. Purchase cases require exact counts and fixed ticks.
Real-time variants require a separately pinned, explicit lateness policy before execution;
they cannot silently reuse fixedTick claims. Seed alone does not make a run deterministic.

`FaultHarness` freezes a copy of authorized descriptors at construction. `ApplyAt` increments
the count for exactly one named boundary and invokes the effect callback only for the approved
occurrence. It emits a receipt only after that callback successfully confirms the effect by
returning true. False or throwing callbacks leave no receipt; `RequireAllFired` rejects them
and unreached faults. A callback must confirm the actual operation, never a planned effect.
A receipt alone does not prove end-to-end behavior. No reset, removal,
or plan replacement API exists. Do not expose the harness, descriptors, oracle data or receipts
to the Planner. ScriptedPlanner snapshots only decision payloads and copies the current request
correlation. Runner #11 still validates freshness, permissions and consumed-once decisions.

The real bridge host belongs to #7. Coordinate these insertion points with that owner:

| Responsibility | Trusted boundary and independent observation |
| --- | --- |
| Game | Request ingress; purchase transaction committed through normal input path; transaction counter outside Playtest scoring |
| Adapter/response | Drop enqueue/poll reply after commit, before response write; preserve request correlation; never resend |
| Observation | Before capture; owner replacement and epoch reset; bounded-history gap; hidden and nonexistent targets |
| Planner | Before reply; cancelled reply does not consume a scripted decision |
| Save/exit | Before artifact write and during cleanup; keep primary outcome and postprocessing separate |

Fault handlers must record receipts only when the actual fault effect happens. The host exports
test-only facts independently of Observe and Result. Trusted Runner and Player/PublicAgent use
separate authorized contexts; a query argument is not profile authority. No fixture promotes a
profile. Do not copy Gua's low-level tests or create a second product Trace.

## Purchase evidence bundle

Run `node tools/fixture-oracle.mjs <catalog> <pre-run-catalog-sha256> <bundle>`.
Exit 0 means agreement with this purchase case, 1 means evidence mismatch, 2 means invalid/unpinned
input. It always reports `productEndToEndAcceptance:false`. Hashes prove exact bytes, not provenance.
An acceptance report must retain original host facts, product Run/Result and original Gua Trace;
the JSON bundle is an extraction for comparison, not a replacement Trace. A trusted test driver
must build the extraction from those independently produced files and retain their hashes.

The bundle has these fields (see synthetic checker inputs in `tools/fixture-oracle.test.mjs`):

| Section | Required data and source |
| --- | --- |
| identity | caseId, fixtureVersion, actual buildId, runId, seed, clock, fault, positionTolerance=0, latenessToleranceTicks=0, variant |
| facts | host-runId, requests, transactions, responses, requestIds, faultReceipts, otherOwnerInputReleased=false, attachedProcessTerminated=false |
| result | saved Runner-runId, status, completionConfirmed, planCompletionPolicy=afterPlan, reasonCategory |
| trace | original Gua Trace-runId, requestIds, resends, completionConfirmed |

`reasonCategory` is a test-driver mapping from the eventual #6 stable reason; it is **not** a new
product reason identifier. Pin that mapping before acceptance. A generic timeout cannot pass
the lost-response case. Exactly one request, one committed transaction, zero responses, zero
resends, unconfirmed completion, TimedOut, and the actual fault receipt are required together.
Normal purchase requires one response and confirmed Passed. Missing data fails both paths.
Original evidence files and pinned configuration must be retained alongside oracle output.

## Remaining acceptance matrix

| Requirement / acceptance | Prepared here | Still required |
| --- | --- | --- |
| FIX-001 / AT-FIX-001 | Explicit contractFake/realBridge/godot/unity labels | Actual communication and input evidence mapped per engine |
| FIX-002 / AT-FIX-002 | Shop, 2D/3D wall, enemy phase/replacement case descriptors | Engine scenes, normal play paths, combined input, multiple matches |
| FIX-003 / AT-FIX-003 | Authorized boundary harness, unfired-fault rejection | Real effect receipts at each responsibility boundary |
| FIX-004 / AT-FIX-004 | Independent fixed purchase expectation, adversarial oracle tests | #7 + #10 afterPlan response-drop run and original Gua Trace |
| FIX-005 / AT-FIX-005 | Pre-run bytes hash, exact counters, identity/tolerance checks | Real build/seed/clock/fault/tolerance artifacts and fixed real-time policy |
| FIX-006 / AT-FIX-006 | Host owner/attach facts required by purchase oracle | External secret marker scan, hidden wait denial, screenshot pixel protection and capture failure; inspect actual processes/held inputs |
| FIX-007 / AT-FIX-007 | Fixed temporal-golden case expectations | #4/#5/#6 compare actual evaluator/Runner outcomes to these independent inputs |
| DELIVERY-003 | No Fake promotion or release claim | #18 same distribution end-to-end integration |

`temporal-golden.json` contains externally stated expectations, not a parallel temporal evaluator.
Unknown does not extend deadlines; ordinary all cannot combine truths from different evaluation
units. Completing cancellation must preserve the established outcome and separately report
mandatory postprocessing. Reconcile concrete API states/reasons with #4/#5/#6 before integration.
No tests currently prove screenshot protection: a text sensitive marker cannot establish it.

Repeat checker tests with `node --test tools/fixture-oracle.test.mjs` and test-only helper tests
with `dotnet test --filter FullyQualifiedName~FixtureSupportTests`. Synthetic bundles are solely
checker tests and must never be saved as real-engine acceptance evidence. CI runs both suites.
Issue #16 stays open until all normal/fault paths have actual, independently verified evidence.
