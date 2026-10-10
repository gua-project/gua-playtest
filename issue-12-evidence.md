# Issue 12 evidence

Base: `01008b63282992cf28e0efb949624bb3b7d8012c`. Windows desktop,
.NET SDK 10.0.401, Release. Shared correction commit:
`5125a11c8eea37f5d8c2afa7bcb7384485eb2a67`.

| Contract | Violation detected / assertion |
|---|---|
| STALL-001 | Missing definitions/reads remain Unknown; Planner input excludes private metric values; Goal/Failure arbitrate independently. `ExploreTests`. |
| STALL-002 | Initial True milestone and unknown metric baseline are not improvement; transient first milestone and accumulated numeric threshold are retained across recovery/identity replacement. `ExploreTests`. |
| STALL-003 | Whole A/B route repetition, improving actions, missing intermediate samples and full semantic comparison. `ExploreTests`. |
| STALL-004 | Full elapsed wait precedes recovery; queued owner dispatch awaits actual receipt; cancellation before dispatch sends nothing. `ExploreTests`, `RunSettlingTests`. |
| STALL-005 | Planner stuck and private-progress stagnation consume both cumulative budgets; invalid proposals remain finitely bounded and never execute. `ExploreTests`. |
| STALL-006 | Recovery exhaustion retains Budget origin; finish without Goal is unverified or success-unconfirmed. `ExploreTests`. |
| Shared lifecycle | Delayed receipt settles observed Goal; actual failure/cancel/deadline retain priority; an actual process exit during pending settlement remains causal evidence. `RunSettlingTests`, `RunReviewTests`, `PreparationTests.FinalLifecycleSampleSharesPrimaryClosureAfterEarlierPollWasNotReady`. |

Before shared correction, the unchanged full-wait and final-action Goal assertions
failed (13 passed, 2 failed):
`artifacts/explore-blockers/testk_DESKTOP-3CJOU9S_2026-10-10_20_43_32_net10.0.trx`.
After correction, focused Execute/Planner/Explore selection: 467 passed, 0 skipped:
`artifacts/shared-settling/testk_DESKTOP-3CJOU9S_2026-10-10_20_49_09_net10.0.trx`.
Expanded Explore selection: 20 passed, 0 skipped:
`artifacts/explore-tests/testk_DESKTOP-3CJOU9S_2026-10-10_20_53_28_net10.0.trx`.
Commands: `dotnet test tests/Gua.Playtest.Foundation.Tests --no-restore -c Release`
with the corresponding `--filter` and `--logger trx --results-directory`.

These are unit/Fake orchestration and contract evidence. Concrete Gua Explore
host binding, real engine, real Planner, Trace integration and CLI binding are
unverified. Published Gua.Testing 1.1.1 does not contain the unpublished guarded
dispatch additions. No mocks count as those acceptance gates. Final-head CI,
independent audit and actual GitHub Codex review remain separate gates.

Local cumulative audit budget: deliberate PR-wide, at most four passes total.
Pass 1 identified same-capture private violation priority, intermediate improvement
retention and unavailable metadata validation. All eight intended pre-fix
assertions failed in `artifacts/audit-1-before/testk_DESKTOP-3CJOU9S_2026-10-10_21_02_48_net10.0.trx`.
Grouped corrections also cover analogous target unavailable metadata. No shared
Execution/Planning API changes were needed for these audit findings.

Before audit fixes, full sequential suites passed: Bridge130, Contracts528,
Foundation890, zero skipped. Release build had zero warnings/errors; dependency
boundaries passed. Package smoke evidence:
`artifacts/smoke-win-x64-ec5c304fbf524dd1a71378923e44bfcc/evidence.json`;
native-free contract smoke evidence:
`artifacts/contracts-37c2dc053e974b2d8f617b6c02b2b8c6/evidence.json`.

After pass 1 fixes, Foundation901 passed with no skips/failures:
`artifacts/audit-1-foundation/testk_DESKTOP-3CJOU9S_2026-10-10_21_04_32_net10.0.trx`.
Pass 2 found repetition history wrongly tied to the independent progress threshold.
Both intended assertions failed before correction:
`artifacts/audit-2-before/testk_DESKTOP-3CJOU9S_2026-10-10_21_06_42_net10.0.trx`.
History now retains the documented finite 1,000 boundaries independently of the
progress threshold; absent definitions and a three-operation route have regressions.
