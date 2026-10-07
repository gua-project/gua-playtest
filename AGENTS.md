# Repository instructions

Gua Playtest is a C#/.NET 10 game-external Runner. Read `README.md`,
`docs/README.md` and the affected normative contracts before changing behavior.
Keep Core native-free, Gua dependencies in GuaIntegration, Fake providers in
tests, and Gua Trace/Recording formats owned by Gua. Published Gua.Testing 1.1.1
does not imply unpublished guarded APIs or real engine acceptance.

## Validation and evidence

- Use issue acceptance conditions and `docs/01-spec-ledger.md` as the baseline.
  Map major requirements to a concrete violation, test location/assertion,
  execution evidence and unverified scope in the PR or referenced artifact.
- Record tested commit, environment, command, result and log/artifact references.
  Distinguish unit/Fake/schema, real transport, real engine, packaged consumer
  and real Planner evidence. Skips, old-HEAD success and green reruns do not
  erase failures or satisfy missing acceptance evidence.
- Reuse existing regressions. For high-impact contracts, verify a selected
  assertion detects the violation using pre-fix code, injected invalid evidence
  or a disposable mutation. Verify the intended assertion fails, rather than an
  unrelated environment error. Never weaken tests to reduce push count.
- Run focused checks first and `git diff --check`. Runtime/dependency changes
  also require the affected README/CI checks; documentation-only changes need
  content, link and consistency checks, not invented runtime tests.

## Independent audit and push gate

Use [$playtest-bug-hunt](.agents/skills/playtest-bug-hunt/SKILL.md) for independent
defect investigation and every gate below. The implementer owns fixes; the
auditor is a separate, fresh, behaviorally read-only subagent.

- After repository changes, including untracked additions, finish focused
  validation and spawn exactly one `playtest_auditor` before the initial push
  or final handoff. Repeat this gate before each consolidated review-fix push
  within the remaining audit budget, except for the known-defect path below.
  A read-only audit or unchanged repository needs no automatic audit.
- Supply task scope, intended base and resolved merge-base/HEAD, cumulative
  branch diff, status, staged/unstaged diffs and relevant untracked contents,
  contract mapping and accessible execution evidence. Audit every matrix lane
  touched by the cumulative diff, not just the last commit.
- Explicitly prohibit edits, commits, pushes, external comments and further
  agent spawning. `.codex/agents/playtest-auditor.toml` defaults to read-only,
  but inherited permission overrides can take precedence. Reject audit-authored
  source changes. A report proves only its reviewed snapshot; disclose later
  changes rather than claiming that the old report audited the new diff.
- Wait for the report. Independently validate findings; reject unsupported,
  speculative, duplicate, style-only or out-of-scope claims with reasons.
  Investigate the same failure pattern in neighboring branches/callers and
  consolidate supported fixes plus regression coverage before another push.
- After audit-led fixes and focused verification, allow at most one final
  auditor pass. Ordinary gate: at most two passes per task/review-fix batch.
  A deliberate PR-wide gate may instead use at most four total passes for that
  batch, including passes already spent; it replaces rather than nests the
  ordinary gate. The parent alone owns this finite review/fix sequence.
- Stop independent auditing as soon as no actionable finding remains or the
  pass limit is reached. The cap stops additional independent or recursive
  audits, not implementation of known defects. Do not reset the counter or
  start a new batch to evade the cap; report the budget and outstanding findings.
- At the cap, concrete defects evidenced by an existing audit, actual CI or
  external review may still be corrected within the authorized task. The
  implementer must validate each finding, investigate analogous branches,
  consolidate supported fixes, and run the relevant regression checks plus
  changed-surface validation before a consolidated push. Map each fix to its
  evidence and assertions, and explicitly identify the post-audit diff that
  received no additional independent audit. Then run actual CI and external
  review on the new HEAD. This path permits correction, not new scope, a fresh
  independent audit or a claim that an earlier report covers later changes.
  Findings without concrete support and missing required evidence remain
  blockers to acceptance; never claim the task complete while defects remain.
- Keep verification focused on changed surfaces and validated failures. For an
  unchanged command and unchanged implementation, allow at most one rerun after
  addressing a concrete environmental cause; further unchanged reruns stop and
  require a blocker report. Evidence-supported code or test fixes require fresh
  regression verification and may proceed to the known-defect path above.
  Such verification is not an additional audit and does not reset its counter.
- If independent agents or required evidence are unavailable, disclose the
  blocker; a self-review is not an independent pass. An explicit user waiver
  can waive the local gate only, never the merge conditions below.

## External review and merge

Local audit is not a substitute for external review. After any batch is pushed,
including the disclosed known-defect path, require actual CI success and actual
Codex GitHub AI review completion for the **final HEAD**, with zero new or unresolved actionable findings before
merge. Absence of comments, a pending request, old-commit review or local success
does not prove completion. A subsequent code change invalidates that evidence.
Do not hide findings, omit external review or weaken tests to reduce pushes.
Do not merge until these conditions and the coordinating parent's ordering
approval are satisfied. Do not open new issues without user authorization.
