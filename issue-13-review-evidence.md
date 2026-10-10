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

## Consolidated post-response lifecycle and adjacent RPC correction

Actual Codex review of `19fafb32385a12bc24ba31ac69f865747fa795d6` completed at
2026-10-10 22:41:09 UTC with three concrete additional findings. They were
validated and consolidated with the neighboring initialize-response shape gap
identified while following the earlier complete-RPC-response failure pattern.

| Finding / pattern | Correction | Regression assertion |
| --- | --- | --- |
| 4239460741 | Post-response turn/started now uses the same complete turn shape, inProgress state and nested ID checks as pre-response notices. Neighboring item/turn streams require current thread/turn IDs; thread/status shares validation in both paths. Empty/null/whitespace method names refuse output. PLANNER-002/008. | `TurnStartedUsesSameShapeAndIdentityBeforeAndAfterRpcResponse`, valid post-response start case, `NeighboringPostResponseStreamNoticesRequireTurnCorrelation`, `ThreadStatusNoticesUseSameValidationBeforeAndAfterResponse`, existing malformed-frame regression. |
| 4239460746 | Fresh thread and matching thread-start notices must be idle before input; systemError/notLoaded/active are unavailable connections rather than proposal sources. PLANNER-008/010. | `FreshThreadMustBeIdleAndMatchSupportedCliBeforeInput`: safe ConnectionFailure, no input/start-turn write, permanently closed instance. |
| 4239460749 | Fresh thread cliVersion must equal initialized pinned 0.150.1; older, prefix-compatible-but-different, and empty versions refuse input. PACK-004/PLANNER-008. | Same fresh-thread regression checks response and notice paths. Existing supported leading user-agent tests remain. |
| Same complete-response pattern as 4239426152 | Official pinned v1 InitializeResponse requires codexHome/platformFamily/platformOs strings in addition to userAgent. All are checked before initialized/thread/input writes. | `AdjacentInitializeResponseRequiresCompletePinnedShape`: missing/null/number/object refuses output, exactly one initialize write, no later request. [Official fixed schema](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server-protocol/schema/json/v1/InitializeResponse.json). |

Artifact group numbers are execution groups, **not new audit batches or budget
resets**. Ordinary independent audit remains **2/2 consumed** throughout. No new
independent audit covers these backend/test/protocol-doc/public-evidence changes.

The intermediate initialize-only behavior was validated before the remaining
review arrived: [manifest](docs/evidence/issue-13/batch-3/manifest.json),
[source snapshot](docs/evidence/issue-13/batch-3/snapshot/CodexProtocolBackend.cs.txt),
[test snapshot](docs/evidence/issue-13/batch-3/snapshot/CodexProtocolTests.cs.txt),
[focused TRX](docs/evidence/issue-13/batch-3/focused.trx),
[pre-fix TRX](docs/evidence/issue-13/batch-3/pre-fix.trx), and
[full test log](docs/evidence/issue-13/batch-3/tests.log).
It had 174 focused passes, 130/528/1101 full-project passes, three intended
pre-fix failures, and successful build/boundary/archive/tool/contract smoke.
It was not pushed separately and does not replace final consolidated validation.

Final consolidated local validation uses pushed base 19fafb3 plus the source
blob hashes in [manifest](docs/evidence/issue-13/batch-4/manifest.json).
Runtime/test source was complete before final checks; only evidence/docs were
added later. Same Windows/.NET environment and UTC/host-local provenance apply.

- [Focused TRX](docs/evidence/issue-13/batch-4/focused.trx) and
  [log](docs/evidence/issue-13/batch-4/focused.log): 197 pass, 0 fail/skip.
- [Pre-fix TRX](docs/evidence/issue-13/batch-4/pre-fix.trx) and
  [log](docs/evidence/issue-13/batch-4/pre-fix.log): 24 intended assertion failures,
  8 existing refusal passes, 0 skip with 19fafb3 backend; fixed source restored
  in finally. Failures show accepted Completed evidence where invalid/unavailable
  status is required, not build/environment failures.
- [Locked restore](docs/evidence/issue-13/batch-4/restore.log),
  [Release build](docs/evidence/issue-13/batch-4/build.log),
  [boundaries](docs/evidence/issue-13/batch-4/boundaries.log): pass; 0 build warnings/errors.
- [Sequential tests](docs/evidence/issue-13/batch-4/tests.log),
  [Bridge](docs/evidence/issue-13/batch-4/bridge.trx),
  [Contracts](docs/evidence/issue-13/batch-4/contracts.trx),
  [Foundation](docs/evidence/issue-13/batch-4/foundation.trx):
  130 + 528 + 1124 = **1782 pass, 0 fail/skip**.
- [Archive/tool log](docs/evidence/issue-13/batch-4/package.log) and
  [JSON](docs/evidence/issue-13/batch-4/package-evidence.json): pass.
- [Native-free consumer log](docs/evidence/issue-13/batch-4/contracts.log) and
  [JSON](docs/evidence/issue-13/batch-4/contracts-evidence.json): pass.

Full cumulative/post-fix diff, public links, artifact byte hashes and staged
snapshot/source blob hashes are checked before push. Smoke's transient RID lock
changes converge back to the original tracked locks; no dependency changes are
part of this correction. Fake/schema/native-package/consumer evidence remains
separate from real transport, real Planner, OS isolation, Trace/game acceptance.
Production provider and OPEN-09/11 remain unavailable/unverified; issue #13 stays
open. Final-HEAD CI/review success and parent ordering approval remain the merge
gate; earlier-HEAD reports and CI cannot substitute for them.

## Final-head external warning/error findings (evidence group 5)

Actual Codex review 5481112901 of 5f4dbff at 2026-10-10 22:58:36 UTC
raised 4239507490 (valid warning before response refused) and 4239507495
(non-retryable error erased by contradictory completed turn). Both are supported
by the fixed [warning schema](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server-protocol/schema/json/v2/WarningNotification.json)
and [event handler](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server/src/bespoke_event_handling.rs).
Warning is thread-scoped or global, without a turn ID. Its shape/optional target
is validated consistently during RPC and active-turn reads; message/frame/deadline
bounds still apply. A non-retryable error records terminal failure independently
of subsequent retry notices and requires failed completion. Investigated both
response orderings, global/null targets, malformed/wrong targets, floods and
failed/interrupted/completed terminal statuses; raw warning/error text is not returned.

- `WarningBeforeOrAfterResponseIsBoundedNonfatalTelemetry`,
  `InvalidWarningsCloseBeforeAndAfterResponse`, `WarningFloodConsumesMessageLimitBeforeAndAfterResponse`
  cover valid consumption, schema/identity refusal and existing bounds.
- `NonRetryableErrorRequiresFailedTerminalStatus` covers both response orderings,
  contradictory proposal/interrupt refusal, later retry telemetry, safe failed
  completion, no output bytes and permanently closed reuse.
- [Focused TRX](docs/evidence/issue-13/batch-5/focused.trx)/[log](docs/evidence/issue-13/batch-5/focused.log):
  210 pass/0 fail/skip, command `dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter FullyQualifiedName~CodexProtocolTests`.
- [Pre-fix TRX](docs/evidence/issue-13/batch-5/pre-fix.trx)/[log](docs/evidence/issue-13/batch-5/pre-fix.log):
  reviewed 5f4dbff backend with new tests gives 6 intended assertion failures and
  2 failed-terminal passes; fixed bytes restored in finally. Expected Completed
  vs actual OutputInvalid proves warning loss; expected OutputInvalid vs actual
  Completed proves erased terminal error. No build/environment failures.
- [Restore](docs/evidence/issue-13/batch-5/restore.log), [build](docs/evidence/issue-13/batch-5/build.log),
  [boundaries](docs/evidence/issue-13/batch-5/boundaries.log), [sequential tests](docs/evidence/issue-13/batch-5/tests.log):
  locked restore/Release build (0 warnings/errors), boundary check and
  Bridge 130 + Contracts 528 + Foundation 1138 = 1796 pass, 0 fail/skip.
  [Bridge TRX](docs/evidence/issue-13/batch-5/bridge.trx),
  [Contracts TRX](docs/evidence/issue-13/batch-5/contracts.trx),
  [Foundation TRX](docs/evidence/issue-13/batch-5/foundation.trx).
- [Package log](docs/evidence/issue-13/batch-5/package.log)/[JSON](docs/evidence/issue-13/batch-5/package-evidence.json),
  [consumer log](docs/evidence/issue-13/batch-5/contracts.log)/[JSON](docs/evidence/issue-13/batch-5/contracts-evidence.json)
  record actual archive/tool install/execute and native-free packaged consumer.
  Commands: `./scripts/package-smoke.ps1 -Rid win-x64`, `./scripts/contract-smoke.ps1`;
  normal [pack](docs/evidence/issue-13/batch-5/pack.log) restores transient RID locks.
  [Manifest](docs/evidence/issue-13/batch-5/manifest.json) pins exact tested source blobs
  and original artifact bytes; host-local Oct11 UTC+09 is Oct10 UTC.

Source was complete before these executions; later additions are docs/evidence only.
Independent audit remains 2/2 consumed, without another agent or reset. This
entire post-audit warning/error/backend/test/docs/evidence diff received NO further
independent audit, under the concrete external-review correction path. New HEAD
requires its own actual CI and completed Codex review, plus parent ordering
approval. Production, OPEN-09/11 and real transport/Planner/OS/Trace/game scope
remain unverified; issue #13 is still open. Evidence group numbering is not an
audit counter or a reset of the finite review budget.
## Final payload/reroute/context-reset findings (evidence group 6)

Actual Codex review 5481139683 of cee9c75 at 2026-10-10 23:09:41 UTC
raised 4239533715 (terminal error payload identity), 4239533723 (pre-response
model/rerouted), and 4239533727 (context-full usage reset). Fixed-version
[handler](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server/src/bespoke_event_handling.rs),
[reroute schema](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server-protocol/schema/json/v2/ModelReroutedNotification.json)
and [fill_to_context_window](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/protocol/src/protocol.rs)
support all three. Keep latest non-retryable error and compare JsonElement.DeepEquals
before failed classification, refusing different/missing/null payloads; test
matching latest errors with reordered properties and later retry telemetry.
Model reroute requires fromModel/toModel strings, pinned reason and current
thread/turn IDs before buffer and after response. Missing/wrong IDs, fields,
reason and aggregate flood are refused. Synthetic context-full snapshots and
mixed regression/increasing-total snapshots make current/subsequent usage
unavailable, preserving old duplicate/regressive-snapshot tests. The reset
fixture includes exact zero breakdown, synthetic last delta and modelContextWindow.

- `TerminalErrorPayloadMustMatchLatestNonRetryableNotice`,
  `ModelRerouteBeforeOrAfterResponseRequiresValidCorrelationAndShape`,
  `RerouteTelemetryConsumesPreResponseAggregateBound`,
  `SyntheticContextResetLeavesCurrentAndSubsequentUsageUnavailable` map the
  violations to assertions before/after RPC responses and across subsequent turns.
- [Focused TRX](docs/evidence/issue-13/batch-6/focused.trx)/[log](docs/evidence/issue-13/batch-6/focused.log):
  223 pass, 0 fail/skip. Command `dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter FullyQualifiedName~CodexProtocolTests`.
- [Pre-fix TRX](docs/evidence/issue-13/batch-6/pre-fix.trx)/[log](docs/evidence/issue-13/batch-6/pre-fix.log):
  cee9c75 backend gives 10 intended assertion failures and 2 matching-latest
  passes. Actual UsageLimit vs expected OutputInvalid proves changed terminal
  error authority; actual invalid vs expected Completed proves reroute loss;
  actual synthetic CodexUsage vs expected null proves incorrect token evidence.
  Fixed exact bytes restored in finally; no environment/build failure.
- [Restore](docs/evidence/issue-13/batch-6/restore.log), [build](docs/evidence/issue-13/batch-6/build.log),
  [boundaries](docs/evidence/issue-13/batch-6/boundaries.log), [tests](docs/evidence/issue-13/batch-6/tests.log):
  locked restore, Release build 0 warnings/errors and boundary checks pass;
  Bridge 130 + Contracts 528 + Foundation 1151 = 1809 pass, 0 fail/skip.
  [Bridge TRX](docs/evidence/issue-13/batch-6/bridge.trx),
  [Contracts TRX](docs/evidence/issue-13/batch-6/contracts.trx),
  [Foundation TRX](docs/evidence/issue-13/batch-6/foundation.trx).
- [Package log](docs/evidence/issue-13/batch-6/package.log)/[JSON](docs/evidence/issue-13/batch-6/package-evidence.json),
  [consumer log](docs/evidence/issue-13/batch-6/contracts.log)/[JSON](docs/evidence/issue-13/batch-6/contracts-evidence.json)
  pass actual win-x64 archive/tool installation/execution and native-free consumer;
  commands `./scripts/package-smoke.ps1 -Rid win-x64`, `./scripts/contract-smoke.ps1`.
  [Pack](docs/evidence/issue-13/batch-6/pack.log) restores normal tracked lock state.
  [Manifest](docs/evidence/issue-13/batch-6/manifest.json) pins tested source blobs,
  raw artifact bytes and Windows/.NET environment. Local Oct11 UTC+09 is Oct10 UTC.

Backend/tests were final before validation; subsequent edits are docs/evidence.
Independent audit remains 2/2 consumed, no fresh agent/reset. This entire
post-audit payload/reroute/usage/backend/tests/docs/evidence diff received NO
additional independent audit; concrete actual review defects were consolidated
and verified under the known-defect path. New HEAD requires actual final CI and
completed Codex review without unresolved actionable findings and parent order
approval. Production/OPEN-09/11 and real transport/Planner/OS/Trace/game acceptance
remain unavailable/unverified. Issue #13 stays open. Group numbers are execution
artifact groups, never a finite audit-budget reset.
## Raw response/mixed-counter findings (evidence group 7)

Actual Codex review 5481164206 of 2abc8e8 at 2026-10-10 23:19:55 UTC
raised 4239559314 (rawResponse/completed before response) and 4239559317
(opposing counter movement at equal total). Both were independently checked by
the implementer against pinned [event handling](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server/src/bespoke_event_handling.rs)
and a concrete `(7,5,12)` then `(6,6,12)` trace. The same raw/model telemetry
pattern was investigated in pinned [notification mappings](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server-protocol/src/protocol/common.rs):
rawResponseItem/completed, model/verification and model/safetyBuffering/updated
also use thread/turn-correlated nonexperimental envelopes. Consolidated all four
with unchanged pinned schemas (embedded offline, Apache notice already packaged),
current ID validation before/after response and existing aggregate/frame/message/
deadline bounds. Raw response usage/items never establish decision usage or
hydrate canonical output. Experimental and optional-turn capability/hook lifecycle
APIs are not enabled; real inherited capability/OS acceptance remains unverified.
Opposing movement of any exposed cumulative component invalidates the epoch,
including equal/decreasing total. Pure duplicate/regressive snapshots still pass
existing regressions; current/subsequent usage stays unavailable after ambiguity.

- `PassiveTelemetryIsValidatedBeforeAndAfterResponse` covers all four families,
  valid pre/post-response, wrong thread/turn and missing/null required members.
- `PassiveTelemetryCannotHydrateProposalOrExceedBuffer` requires missing-canonical
  output refusal and actual aggregate-byte overflow for each family.
- `OpposingUsageComponentsInvalidateCurrentAndLaterEpoch` covers opposite input/
  output changes, equal/decreasing totals and subsequent turns.
- [Focused TRX](docs/evidence/issue-13/batch-7/focused.trx)/[log](docs/evidence/issue-13/batch-7/focused.log):
  235 pass, 0 fail/skip. Command `dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter FullyQualifiedName~CodexProtocolTests`.
- [Pre-fix TRX](docs/evidence/issue-13/batch-7/pre-fix.trx)/[log](docs/evidence/issue-13/batch-7/pre-fix.log):
  reviewed 2abc8e8 backend gives 8 intended assertion failures, 0 pass/skip.
  Valid early telemetry reports OutputInvalid instead of Completed; mixed usage
  reports synthetic CodexUsage instead of null. Fixed exact bytes restored in
  finally. [Initial pre-fix](docs/evidence/issue-13/batch-7/initial-pre-fix.trx)
  retained 8 correlation/usage assertion failures before test-order refinement.
- [Initial focused TRX](docs/evidence/issue-13/batch-7/initial-focused.trx)/[log](docs/evidence/issue-13/batch-7/initial-focused.log)
  retained 234 pass/1 fail: shortest raw-response flood was below 10000-byte
  limit, yielding legitimate missing-canonical ConnectionFailure. Enlarged its
  schema-valid response ID; limits and assertions unchanged. Earlier tool output
  also reported CS8604 nullable telemetry predicate and a missing artifact output
  directory; both concrete local causes corrected before successful executions.
  Neither failure is acceptance evidence; no unchanged rerun was used to erase it.
- [Restore](docs/evidence/issue-13/batch-7/restore.log), [build](docs/evidence/issue-13/batch-7/build.log),
  [boundaries](docs/evidence/issue-13/batch-7/boundaries.log), [tests](docs/evidence/issue-13/batch-7/tests.log):
  locked restore, Release build 0 warnings/errors and boundary checks pass;
  Bridge 130 + Contracts 528 + Foundation 1163 = 1821 pass, 0 fail/skip.
  [Bridge TRX](docs/evidence/issue-13/batch-7/bridge.trx),
  [Contracts TRX](docs/evidence/issue-13/batch-7/contracts.trx),
  [Foundation TRX](docs/evidence/issue-13/batch-7/foundation.trx).
- [Package log](docs/evidence/issue-13/batch-7/package.log)/[JSON](docs/evidence/issue-13/batch-7/package-evidence.json),
  [consumer log](docs/evidence/issue-13/batch-7/contracts.log)/[JSON](docs/evidence/issue-13/batch-7/contracts-evidence.json)
  pass actual win-x64 archive/tool install/execute and native-free consumer;
  commands `./scripts/package-smoke.ps1 -Rid win-x64`, `./scripts/contract-smoke.ps1`.
  [Pack](docs/evidence/issue-13/batch-7/pack.log) restores normal lock state.
  [Manifest](docs/evidence/issue-13/batch-7/manifest.json) includes backend, helper,
  project/resources and tests source blob hashes plus original artifact bytes;
  environment Windows 10.0.26300 / SDK 10.0.401, host Oct11 UTC+09 = Oct10 UTC.

Source was final before validation; later changes are docs/evidence only.
Independent audit remains 2/2 consumed, with no new agent/reset. Entire
post-audit raw/model/usage/helper/resource/project/test/docs/evidence diff received
NO further independent audit under concrete external-review correction path.
Final HEAD actual CI/review and parent sequencing approval remain mandatory;
earlier reviews prove only their snapshots. Production, OPEN-09/11 and real
transport/Planner/OS/inherited-capability/Trace/game acceptance remain unverified.
Issue #13 stays open. Execution group numbers never reset the audit budget.
## Nonfatal startup notice finding (evidence group 8)

Actual Codex review 5481189421 of 6067a4f at 2026-10-10 23:30:33 UTC raised
4239584832 (inherited legacy-feature deprecationNotice can race thread/start).
Pinned [event handling](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server/src/bespoke_event_handling.rs)
and [DeprecationNoticeNotification schema](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/app-server-protocol/schema/json/v2/DeprecationNoticeNotification.json)
confirm a nonexperimental nonfatal notice with required string summary and optional
string/null details. The neighboring GuardianWarning and existing ConfigWarning
share the same nonfatal startup pattern and are consolidated with pinned offline
schemas. Guardian thread targets are checked against the known/announced thread.
No configuration, authentication, persistent permissions, security or capability
settings are changed. Notice text never returns as backend proposal/error evidence.

- `NonfatalStartupNoticesAreValidatedAcrossThreadAndTurnResponses` exercises
  valid and missing-required-member refusal for all three notice families before/
  after thread/start and turn/start responses, including full announced thread
  validation and no private notice text in reply.
- `NonfatalStartupNoticeFloodConsumesExistingMessageBound` verifies existing
  message limits still close before Planner input is sent.
- [Focused TRX](docs/evidence/issue-13/batch-8/focused.trx)/[log](docs/evidence/issue-13/batch-8/focused.log):
  241 pass, 0 fail/skip. Command `dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter FullyQualifiedName~CodexProtocolTests`.
- [Pre-fix TRX](docs/evidence/issue-13/batch-8/pre-fix.trx)/[log](docs/evidence/issue-13/batch-8/pre-fix.log):
  6067a4f backend gives 3 intended assertion failures, 0 pass/skip. Deprecation/
  guardian valid early notices yield OutputInvalid instead of Completed; malformed
  config warning yields Completed instead of OutputInvalid. Exact fixed source
  restored in finally; no unrelated build/environment failure.
- [Restore](docs/evidence/issue-13/batch-8/restore.log), [build](docs/evidence/issue-13/batch-8/build.log),
  [boundaries](docs/evidence/issue-13/batch-8/boundaries.log), [tests](docs/evidence/issue-13/batch-8/tests.log):
  locked restore, Release build 0 warnings/errors and boundaries pass;
  Bridge 130 + Contracts 528 + Foundation 1169 = 1827 pass, 0 fail/skip.
  [Bridge TRX](docs/evidence/issue-13/batch-8/bridge.trx),
  [Contracts TRX](docs/evidence/issue-13/batch-8/contracts.trx),
  [Foundation TRX](docs/evidence/issue-13/batch-8/foundation.trx).
- [Package log](docs/evidence/issue-13/batch-8/package.log)/[JSON](docs/evidence/issue-13/batch-8/package-evidence.json),
  [consumer log](docs/evidence/issue-13/batch-8/contracts.log)/[JSON](docs/evidence/issue-13/batch-8/contracts-evidence.json)
  pass actual archive/tool install/execute and native-free packaged consumer.
  Commands `./scripts/package-smoke.ps1 -Rid win-x64`, `./scripts/contract-smoke.ps1`;
  [pack](docs/evidence/issue-13/batch-8/pack.log) restores normal tracked lock state.
  [Manifest](docs/evidence/issue-13/batch-8/manifest.json) pins backend/helper/project/
  resources/test source blobs and original raw artifact bytes. Windows 10.0.26300
  / SDK 10.0.401, host Oct11 UTC+09 = Oct10 UTC.

Runtime/test sources were final before these executions; later edits are only
docs/evidence. Independent audit remains 2/2 consumed; no further agent/reset.
Entire post-audit startup-notice/backend/helper/resource/project/test/docs/evidence
diff received NO further independent audit under concrete external-review defect
correction path. Final HEAD actual CI and completed Codex review without unresolved
actionable findings and parent sequencing approval are mandatory. Production,
OPEN-09/11 and real provider/transport/Planner/OS/inherited-capability/Trace/game
acceptance remain unavailable/unverified; issue #13 stays open. Execution groups
are artifact provenance groups, never a reset of the finite audit budget.