# Playtest audit lanes

Select touched lanes only; for cross-boundary changes follow every affected
consumer. Existing acceptance conditions determine required evidence.

| Lane | Contracts and surfaces | High-signal checks |
| --- | --- | --- |
| Wire/static validation | `docs/contracts.md`, `docs/schemas`, Core, CLI, fixtures | bounded original UTF-8/number integrity, offline references, unknown fields, fixed diagnostics, correlation and nonempty required results |
| Assertions/conditions | `docs/assertions.md`, `docs/conditions.md`, Core, Runner | three-valued truth, ambiguity, all branches validated, temporal origins/inclusive boundaries, witness/epoch changes, gaps, no cached continuity or hidden violations |
| Run/async lifecycle | `docs/execution.md`, Runner, launch/Replay drivers | serialized owner, paired Running boundary, independent real deadline, clock regression, cancellation/fault arbitration, ignored cancellation, join/isolate late work, immutable primary, final-operation budget accounting |
| Gua ownership/dispatch | `docs/gua-bridge.md`, GuaIntegration, Runner | current profile/epoch/owner/capability/confirmation at dispatch, fresh targets, atomic segment reservation, sent/uncertain not refunded/retried, correlated completion, held-input cleanup on every terminal path |
| Planner secrecy/adoption | `docs/planning.md`, Runner, Planners.Codex | separate public observation connection, no private Goal/expected/setup/secret feedback, pre-send redaction, complete single proposal, stale/duplicate/expired IDs, closed permits, no direct Gua/shell/file bypass, real sandbox evidence |
| Artifacts/CLI/package | `docs/artifacts.md`, Runner/Persistence, CLI, scripts | primary before cleanup, postprocessing separate, no fabricated result, create-only writes, byte/hash/identity/link checks, safe references, redaction before buffers/hashes, genuine Gua receipts, actual archive/tool execution |
| Real boundary/acceptance | `docs/01-spec-ledger.md`, `docs/02-issue-plan.md`, fixture evidence, GuaIntegration, tests | distinguish Fake/schema/native/transport/engine/real Codex paths; unavailable upstream APIs stay unavailable; genuine host action/observed completion and profile/cancel/reset fixtures |
| Audit workflow/docs | `AGENTS.md`, skills, custom agents, work guidance | discoverable skill, distinct read-only reviewer, full dirty/cumulative snapshot, bounded passes/retries, actionable evidence, consolidated same-pattern fixes, final-HEAD external gates |

Use the narrowest existing test project/case or `scripts/check-boundaries.ps1`.
For applicable runtime/package changes use README checks and CI's sequential
`scripts/test-projects.ps1`, `package-smoke.ps1` and `contract-smoke.ps1`.
Retain failures and unverified scopes. Unit/Fake checks cannot close real bridge,
engine, Planner, packaging or mandatory cleanup acceptance conditions.
