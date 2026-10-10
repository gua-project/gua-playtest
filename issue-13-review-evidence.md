# Issue #13 / PR #38 external-review correction evidence

This batch continues `issue-13-codex-provider` from pushed HEAD
`087c1564ed91fc644db0ac072cc3818567ee1025`. Intended base and resolved merge-base:
`0b77154dbf35f35d6ae58811d8d8e6800deb2baa`. Local `main` is older and was not used
as the PR comparison baseline. The commit containing this document records the
tested final source snapshot; exact final-HEAD CI/review evidence belongs in PR #38.

The three pre-existing changed files and three untracked files were preserved
before further edits under
`C:\Users\testk\AppData\Local\Temp\issue-13-preserved-08c9355875f24195a7175312029adaa6`.
No reset, remote reconstruction, authentication change, persistent permission
change or global security change was performed.

## Contract and concrete finding mapping

All assertions below are in
`tests/Gua.Playtest.Foundation.Tests/CodexProtocolTests.cs`.

| Review finding | Violation and fix | Regression assertion |
| --- | --- | --- |
| 4237748180 | Paired account rate-limit telemetry previously rejected a valid fast turn. Consume it within existing frame/message/deadline bounds. PLANNER-008/010. | `PairedUsageAndRateLimitTelemetryBeforeResponseRemainBounded`: valid ordering completes; overflow refuses output. |
| 4237748189 | Invalid terminal item members previously reached backend failure classification. Validate every ThreadItem, including non-assistant items and canonical completions, offline against the fixed schema. PLANNER-008/010. | `InvalidItemMembersCannotBecomeBackendTerminalEvidence`: completed/failed/interrupted paths all return OutputInvalid, no bytes, closed session. |
| 4237748197 | Non-ephemeral effective thread could persist the first Planner message. Confirm true before sending input. PLANNER-009/FILE-006. | `ThreadMustConfirmEphemeralBeforePlannerInput`: missing/false/null/string is ConnectionFailure, no turn/input write, then closed. |
| 4237772712 | Correlated stream retry telemetry before start response was rejected. Validate and buffer it within aggregate limits; use the same validation after response. No client resend. PLANNER-008/010. | `CorrelatedRetryNoticeBeforeOrAfterStartResponseAllowsTerminalCompletion`, `InvalidRetryNoticesCloseBeforeAndAfterStartResponse`, `PreResponseRetryTelemetryConsumesAggregateBufferLimit`: correlation, boolean/error schema, one start write, bounds. |
| 4237772720 | Broader effective thread permissions were accepted. Require approval never/readOnly/network false before input. Schema-omitted network defaults false. PLANNER-009. | `EffectiveThreadRestrictionsMustMatchBeforeSendingPlannerInput`, `ReadOnlySandboxOmittedNetworkUsesSchemaDefaultFalse`: mismatch refuses all input; schema default succeeds. |
| 4237772725 | ID-only start response could authorize a later proposal. Validate required Turn shape/status, items, optional view/timestamps/error, and analogous turn-start notice. PLANNER-008/010. | `InvalidStartTurnCannotBeRescuedByLaterCompletion`: malformed fields and non-inProgress start refuse output and close; existing lifecycle/correlation tests remain. Positive fixtures now contain required fields. |
| 4237772727 | Malformed CodexErrorInfo became ConnectionFailure. Validate known string/structured union before terminal classification and in retry telemetry; explicitly bound HTTP status to uint16. PLANNER-010. | `MalformedErrorUnionCannotMasqueradeAsBackendFailure`: invalid details refuse output in all three terminal branches. `ValidErrorUnionRetainsSafeConnectionFailure` preserves genuine failures, null/optional details and structured variants. Existing usage/session-budget tests remain. |

Reviewed source contracts: README, docs/README, docs/planning,
docs/codex-protocol, issue #13 acceptance and docs/01-spec-ledger.
Pinned official schema source: `openai/codex` tag `rust-v0.150.1`,
`codex-rs/app-server-protocol/schema/json/v2/{ThreadStartResponse,TurnStartResponse,TurnCompletedNotification,ErrorNotification}.json`.
All 32 copied ThreadItem definitions match upstream assertions after removal of
description/title/default annotations; numeric-equivalent 0/0.0 is preserved.
TurnError uses its reachable CodexErrorInfo/NonSteerableTurnKind definitions.
Apache LICENSE is included in archive output and tool package
`tools/net10.0/any/notices/Codex.LICENSE`.

## Execution evidence

Environment: Windows 10.0.26300, win-x64, .NET SDK 10.0.401 / MSBuild 18.9.11.
Executed 2026-10-10 UTC (host local date 2026-10-11, UTC+09:00) on the
preserved checkout plus this batch's source diff. TRX filenames use host local time.
Source changes were complete before the full validation; later edits only added
this evidence document and adjusted documentation wrapping. Tests use Fake
protocol transport; they do not invoke a real Planner, engine or game.

| Command / scope | Result | Evidence |
| --- | --- | --- |
| `dotnet test ...Foundation.Tests.csproj -c Release --filter FullyQualifiedName~CodexProtocolTests` initial batch | 139 pass / 1 fail / 0 skip. Positive lifecycle fixture had duplicate items introduced while strengthening fixtures; corrected, never hidden. | [initial failure TRX](docs/evidence/issue-13/batch-1/initial-focused.trx) |
| `dotnet test ...Foundation.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Codex` | 141 pass / 0 fail / 0 skip. | [focused TRX](docs/evidence/issue-13/batch-1/focused-fixed.trx) |
| Disposable pre-fix HEAD backend with selected new regressions | 42 intended assertion failures / 6 pass / 0 skip. Covers rate telemetry, ephemeral/effective restrictions, start shape, retry ordering, malformed error. Source restored in finally. | [pre-fix log](docs/evidence/issue-13/batch-1/pre-fix.log), [TRX](docs/evidence/issue-13/batch-1/pre-fix.trx) |
| Disposable pre-fix HEAD backend, `FullyQualifiedName~InvalidItemMembersCannotBecomeBackendTerminalEvidence` | All 8 intended assertions fail, expected OutputInvalid vs accepted backend/terminal evidence. Source restored in finally. | [item regression log](docs/evidence/issue-13/batch-1/pre-fix-items.log), [TRX](docs/evidence/issue-13/batch-1/pre-fix-items.trx) |
| `dotnet restore --locked-mode` / `dotnet build --no-restore -c Release` | Success; build 0 warnings / 0 errors. | Console outcome recorded here; [actual ae6eb05 CI](https://github.com/gua-project/gua-playtest/actions/runs/38091051537) also validates identical first-batch runtime/test source. Full captured build logs for the later changed source are below. |
| `./scripts/test-projects.ps1` | Bridge 130, Contracts 528, Foundation 1068: all pass, 0 skip. | [Bridge](docs/evidence/issue-13/batch-1/bridge.trx), [Contracts](docs/evidence/issue-13/batch-1/contracts.trx), [Foundation](docs/evidence/issue-13/batch-1/foundation.trx) |
| `./scripts/check-boundaries.ps1`, `git diff --check` | Pass. | [Actual ae6eb05 boundary CI](https://github.com/gua-project/gua-playtest/actions/runs/38091051537); diff-check console outcome recorded here. |
| `./scripts/package-smoke.ps1 -Rid win-x64` | Actual archive extraction and tool install/execute pass. | [package log](docs/evidence/issue-13/batch-1/package.log), [JSON](docs/evidence/issue-13/batch-1/package-evidence.json) |
| `./scripts/contract-smoke.ps1` | Native-free published consumer validation pass. | [contract log](docs/evidence/issue-13/batch-1/contracts.log), [JSON](docs/evidence/issue-13/batch-1/contracts-evidence.json) |

## Audit budget and remaining acceptance

Independent audit remains ordinary **2/2 consumed**. No fresh independent auditor
was spawned, and no counter was reset. This is AGENTS.md's concrete external
finding correction path. Every file in this batch is a post-audit diff with **no
additional independent audit**: backend, schema helper/resources/license, project
resource/notice configuration, regressions, protocol docs and this evidence.
Earlier audit reports do not cover this snapshot.

The cumulative PR also touches the README and CLI adapter. Local evidence covers
wire/static validation, Planner secrecy/adoption conversion, lifecycle/failure,
and Windows artifact/package lanes. Actual CI on all four platform lanes and
fixture-oracle, plus actual Codex GitHub review completion for the final HEAD
with zero unresolved actionable findings, remain mandatory before merge. The
coordinating parent must approve ordering. Local success is not that gate.

Issue #13 stays open. Production provider launch/authentication, OPEN-09/11,
actual OS isolation/inherited capabilities, real transport/model, real Planner,
Trace integration and game acceptance remain unverified and unavailable. Existing
Core/Runner authority and Replay independence were not changed.

## Further concrete external findings on ae6eb05

The actual Codex GitHub review completed at 2026-10-10 22:27:31 UTC on
`ae6eb051a6b52c400b0f84a569e80bfd00876f1b` and reported four new findings.
The former seven findings had been fixed and resolved. Its
[four-platform foundation CI](https://github.com/gua-project/gua-playtest/actions/runs/38091051537)
and [fixture-oracle](https://github.com/gua-project/gua-playtest/actions/runs/38091051539)
subsequently succeeded. Those results do not satisfy the gate for the later fixes.

| Finding | Concrete correction and analogous path | Assertion / public evidence |
| --- | --- | --- |
| 4239426152 | Validate complete pinned ThreadStartResponse and Thread before input; validate thread-start notices through the same Thread schema. Require a fresh ephemeral thread with no historical turns; bound timestamps to int64. PLANNER-008/009. | `CompleteThreadStartShapeIsRequiredBeforeInput`, `InvalidOrHistoricalThreadStateCannotOpenNewRun`, `FullyShapedThreadNoticeMatchesResponseAndAllowsTurn`. |
| 4239426154 | Validate complete pinned ItemCompletedNotification params before canonical caching, both before/after start response; require completedAtMs integer/int64. PLANNER-008/010. | `CanonicalCompletionTimestampIsRequiredBeforeCaching` checks missing/null/string/fraction/overflow, no bytes, closed session. |
| 4239426157 | A valid non-null error is accepted only on failed turns. Completed/interrupted/inProgress error contradictions refuse output. PLANNER-010. | `NonFailedTurnCannotCarryValidBackendError` uses valid usage-limit and structured connection errors; existing failed/null/optional error cases remain. |
| 4239426162 | Replace local-only ignored artifact references with committed original TRX, captured logs and smoke JSON. Publish both failure evidence and successes, with hash manifests. | [Batch 1 manifest](docs/evidence/issue-13/batch-1/manifest.json), [batch 2 manifest](docs/evidence/issue-13/batch-2/manifest.json). Original artifacts are copied without modification; `.gitattributes` preserves their bytes so SHA-256 verification survives checkout. Generated binaries remain in actual CI artifacts rather than Git. |

Further validation used base HEAD ae6eb05 plus the known-defect source diff,
2026-10-10 UTC / host-local 2026-10-11, same Windows/.NET environment. The source
hashes in batch 2's manifest identify the committed source snapshot associated
with this validation. After build, only a source-provenance comment and evidence/
documentation changed; runtime and test behavior is identical. New complete schema assertions match the official fixed tag after
annotation removal. No independent audit was added; budget remains **2/2**.

- [Focused TRX](docs/evidence/issue-13/batch-2/focused.trx): 171 pass, 0 fail/skip.
- [Pre-fix TRX](docs/evidence/issue-13/batch-2/pre-fix.trx) and
  [captured log](docs/evidence/issue-13/batch-2/pre-fix.log): 28 intended assertion
  failures / 1 existing refusal pass / 0 skip with ae6eb05 backend, then exact
  fixed backend restored in finally. The passing inProgress terminal case was
  already refused by the terminal-status guard and is not counted as detection
  of a new violation.
- [Locked restore](docs/evidence/issue-13/batch-2/restore.log) and
  [Release build](docs/evidence/issue-13/batch-2/build.log): pass, 0 warnings/errors.
- [Sequential full test log](docs/evidence/issue-13/batch-2/tests.log) and
  [Bridge TRX](docs/evidence/issue-13/batch-2/bridge.trx),
  [Contracts TRX](docs/evidence/issue-13/batch-2/contracts.trx),
  [Foundation TRX](docs/evidence/issue-13/batch-2/foundation.trx):
  130 + 528 + 1098 pass, 0 fail/skip.
- [Boundaries](docs/evidence/issue-13/batch-2/boundaries.log) and diff checks: pass.
- [Archive/tool log](docs/evidence/issue-13/batch-2/package.log) and
  [smoke evidence](docs/evidence/issue-13/batch-2/package-evidence.json): pass.
- [Native-free consumer log](docs/evidence/issue-13/batch-2/contracts.log) and
  [evidence](docs/evidence/issue-13/batch-2/contracts-evidence.json): pass.

This further backend/helper/schema/resource/test/protocol-doc/evidence/attribute diff also
received **no additional independent audit**. Production provider, OPEN-09/11,
real transport/Planner/OS enforcement/Trace/game acceptance remain unverified.
Final-HEAD actual CI and completed actual Codex review with zero unresolved
actionable findings, plus parent sequencing approval, are still required.

Published evidence originally triggered diff-check reports because retained CRLF
was treated as trailing whitespace.  .gitattributes  now recognizes cr-at-eol
for this byte-preserved evidence directory; other whitespace checks remain enabled.
The complete ae6eb05-to-published-source diff and attribute/doc changes then passed
git diff --check. Original artifact bytes and manifests are unchanged. This
attribute/document-only correction required content/hash/diff checks, no runtime rerun.
