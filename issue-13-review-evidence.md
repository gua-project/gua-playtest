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
Executed 2026-10-11 UTC on the preserved checkout plus this batch's source diff.
Source changes were complete before the full validation; later edits only added
this evidence document and adjusted documentation wrapping. Tests use Fake
protocol transport; they do not invoke a real Planner, engine or game.

| Command / scope | Result | Evidence |
| --- | --- | --- |
| `dotnet test ...Foundation.Tests.csproj -c Release --filter FullyQualifiedName~CodexProtocolTests` initial batch | 139 pass / 1 fail / 0 skip. Positive lifecycle fixture had duplicate items introduced while strengthening fixtures; corrected, never hidden. | `artifacts/codex-review-followup/testk_DESKTOP-3CJOU9S_2026-10-11_07_14_23_net10.0.trx` |
| `dotnet test ...Foundation.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Codex` | 141 pass / 0 fail / 0 skip. | `artifacts/codex-review-followup/focused-fixed/*.trx` |
| Disposable pre-fix HEAD backend with selected new regressions | 42 intended assertion failures / 6 pass / 0 skip. Covers rate telemetry, ephemeral/effective restrictions, start shape, retry ordering, malformed error. Source restored in finally. | `artifacts/codex-review-followup/pre-fix/{run.log,*.trx}` |
| Disposable pre-fix HEAD backend, `FullyQualifiedName~InvalidItemMembersCannotBecomeBackendTerminalEvidence` | All 8 intended assertions fail, expected OutputInvalid vs accepted backend/terminal evidence. Source restored in finally. | `artifacts/codex-review-followup/pre-fix-items.log`, `pre-fix-items/*.trx` |
| `dotnet restore --locked-mode` / `dotnet build --no-restore -c Release` | Success; build 0 warnings / 0 errors. | Execution output and final-HEAD CI to be linked in PR. |
| `./scripts/test-projects.ps1` | Bridge 130, Contracts 528, Foundation 1068: all pass, 0 skip. | `artifacts/tests/*_2026-10-11_07_16_{30,44,53}_net10.0.trx` |
| `./scripts/check-boundaries.ps1`, `git diff --check` | Pass. | Execution output. |
| `./scripts/package-smoke.ps1 -Rid win-x64` | Actual archive extraction and tool install/execute pass. | `artifacts/codex-review-followup/package.log`, `artifacts/smoke-win-x64-2a242e346e8b4528ad770476220d2c0a/evidence.json` |
| `./scripts/contract-smoke.ps1` | Native-free published consumer validation pass. | `artifacts/codex-review-followup/contracts.log`, `artifacts/contracts-4bc1f7e57bf341ada4f20e7edaa6f1ca/evidence.json` |

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
