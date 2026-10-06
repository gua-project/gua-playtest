---
name: playtest-bug-hunt
description: Independently audit Gua Playtest cumulative changes for reproducible contract defects, races, regressions and missing failure handling before initial or consolidated review-fix pushes.
---

# Playtest bug hunt

Read `AGENTS.md`, `README.md`, the task acceptance conditions and relevant
normative docs. Select all touched lanes from
[references/audit-matrix.md](references/audit-matrix.md). Inspect the complete
merge-base-to-HEAD diff plus staged, unstaged and relevant untracked additions.
Record the resolved base, HEAD and working-tree snapshot; missing/inaccessible
evidence is unverified, never a clean validation claim.

Trace the affected path from wire contract through Core, Runner, GuaIntegration,
Planner or CLI to observable results. Matching type names, enqueue acceptance,
schema validity or a Fake success do not establish end-to-end behavior. Check
whether a major contract violation could still pass the supplied assertions.
Read existing tests before choosing bounded verification. Execute available
read-only checks or use temporary output outside source; do not create tests,
edit files, install software, mutate fixtures or change the repository. Return
a precise reproducer and regression test/assertion proposal when execution is
unavailable. Never claim a proposed test ran.

The auditor must be a fresh agent distinct from the implementer. Supply raw
scope/diff/contracts/evidence without coaching it toward a suspected answer.
No edits, commits, pushes, external comments or child agents are permitted,
even if the inherited sandbox permits writing. The parent validates findings,
deduplicates, investigates analogous branches, applies supported fixes and runs
regressions. Follow AGENTS.md's two-pass ordinary/four-pass deliberate PR-wide
cap; do not start another audit loop inside this skill.

For each actionable finding provide severity, exact file/line, trigger, expected
versus actual behavior, violated contract, verification command/result (or
explicit unverified status), narrow fix direction and regression test/assertion.
Ignore style, speculation and missing features without a violated contract.
When none remain, explicitly say so and list audited lanes, performed checks,
missing required evidence and concrete residual risks. A report with blockers
does not authorize push, claim acceptance or substitute for final-HEAD CI and
Codex GitHub AI review.
