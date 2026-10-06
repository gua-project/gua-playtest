# Issue 5 condition evidence

Base: main `ab9c7cbb1c9df877b5f37ca8f3c5f3ad1405844a` (comparison PR31). Isolated Windows checkout/branch `task-8/repo`, `issue-5-conditions`.

## Requirements

`docs/conditions.md` fixes OPEN-05 condition semantics and API, including complete preparation, selectors/quantifiers, ordinary truth vs temporal facts, nested origins, inclusive start deadlines, Unknown expiry, missing coverage, source/target witness changes, timer wake-ups and error priority. It explicitly defers Run priority/budgets to #6 and actual adapter/authorization/projection to #7/#11.

`ConditionTests` verifies AT-ASSERT-004/005/007/008/009/010 and AT-TIME-001/002/003 at the condition boundary. Literal tables cover every ordinary three-valued pair, all quantifiers with zero/partial/unknown/invalid observations, target count operators, and all 16 ordered completion pairs (32 all/any expectations). Fake monotonic clocks and explicit independent expectations are used; no sleeps or implementation-generated oracle. Existing #16 temporal golden data is unchanged and its Run-outcome cases remain #6-owned.

## Local verification (Windows, .NET SDK 10.0.401)

- `dotnet restore Gua.Playtest.slnx --locked-mode --nologo`: success, pinned existing NuGet graph; no software installation/credential changes.
- `dotnet test Gua.Playtest.slnx --no-restore --nologo`: 660 passed (528 contracts + 132 foundation, including 110 condition tests); 0 failed/skipped.
- `scripts/check-boundaries.ps1`: five-project dependency graph passed. Runner only references Core.
- `git diff --check`: passed.

## Remaining acceptance

Issue #5 must stay open. No real bridge/engine/action/planner/Run acceptance is inferred from these unit tests. Exact residuals and owners are in `docs/conditions.md`: actual same-unit subscription/profile/identity and unsent ambiguous action (#7/#11), Running/wait scheduling and maxDuration/budget/clock priorities (#6/#8/#10), real cross-spec fixture acceptance (#16). No auto-close keyword is used by this PR.

Only final-HEAD real GitHub CI success and actual GitHub Codex review completion with no outstanding/new actionable findings can satisfy the merge gate. Local success, silence and old-head reviews do not.

## Actual Codex review fixes

Actual review5425885136 on25f1b54 identified canonical OPEN-05 mismatch (comment4193250091) and missing temporal deadline/timer handling after observation violations (4193250105). The canonical entry now records the condition-level resolution and exact downstream gaps. Violations preserve error/Unknown while independently advancing Pending/Satisfied/Expired state, resetting unfinished holds, retaining deadline wakeups, and latching expiry after the inclusive boundary. Five independent boundary/latch/group regression cases pass; no oracle or prior thresholds were weakened. Fresh final-head CI/review are required.

