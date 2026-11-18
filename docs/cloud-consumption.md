# Local consumer contract for a future Cloud caller

The bounded Windows scripted lane already supports Scenario input, admitted
launch/connection, one click, Goal and saved HTML. This is sufficient to start
Cloud integration planning; general exploration/Replay/repair and detailed Goal
review remain separate. This document introduces no Cloud service or transport.

A caller supplies the existing `scripted-explore-run --json` or `replay-run --json`
inputs described in [scripted runtime](scripted-explore-runtime.md) and
[Replay runtime](replay-runtime.md), including explicit admitted roots, executable
hash, Environment and output directory. Consume the final stdout JSON and process
exit together. `details.runId` correlates the saved Run; `details.artifactDirectory`
is a worker-local path, not a URL or an external path authority.

Read `result.json` through `RunArtifactReader` and preserve its status and
postprocessing state. Goal verified, Plan completed, click success and report
export success do not independently establish Passed. Exit 11 distinguishes a
Passed primary with incomplete mandatory postprocessing. Other execution exits
are documented in [CLI](cli.md). Never use report exit zero as the Run outcome.

The completion inventory identifies saved Trace/HTML files with byte lengths and
SHA256. Use admitted local paths and validate file receipts; result schema
verification alone does not verify every artifact. With `trace.savePolicy=always`,
a finalized saved Trace can be rendered using the existing command:

```powershell
# All paths are explicitly admitted worker-local paths. Output must be new,
# its parent must exist, and it must be outside the source Trace directory.
$reply = & gua-playtest --json report --allow-root $root --trace $trace --output $newHtml
if ($LASTEXITCODE -ne 0) { throw "Report export failed" }
$report = $reply | ConvertFrom-Json
if ($report.status -ne 'CommandSucceeded' -or $report.code -ne 'SavedTraceRead' -or
    -not $report.details.finalized) { throw "Unexpected report response" }
# Retain the separately read Run result and Run ID; export does not regrade it.
```

With onFailure policy, OmittedOnSuccess is legitimate and has no saved HTML to
reconstruct. Missing/corrupt files are distinct from intentional omission. The
existing loopback GET-only Dashboard exposes snapshot Run IDs/results and opaque
report links; it is not a Cloud submission API. Its report reader revalidates the
inventory and rejects traversal or changed HTML. Do not publish raw Recordings,
private inputs, authority keys or logs as a replacement for these contracts.

Executable local verification:

```sh
dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter FullyQualifiedName~DashboardTests
```

`Gua_common_report_links_revalidate_and_missing_detail_is_not_omission` creates a
real finalized Gua Trace, persists a Failed Run, retrieves its HTML through the
opaque report contract, exports HTML through the CLI and verifies the same Run
remains Failed. Neighboring cases cover statuses, exit-11 semantics, omission,
missing/corrupt inventory and path authority. This is local Trace/CLI/reader
coverage, not a new real-game or deployed Cloud verification.

Minimum acceptance for a future caller: validate inputs; capture final JSON and
exit; correlate Run ID and saved result; verify receipts before acquiring HTML;
retain non-Passed and incomplete states; handle omitted/missing detail distinctly;
and check owned-process cleanup separately. Job submission, authentication,
transfer and hosted URLs require the actual Cloud-side contract and are not
implemented or inferred here. Current CI stability investigations do not erase
the historical basic-flow proof, and a green rerun does not resolve their causes.

## Trusted in-process start admission

`CliApplication.ExecuteWithRunAdmissionAsync` reuses the existing runtime dispatch
for observe-run, click-run, scripted-explore-run and replay-run. This library
entrypoint takes a synchronous trusted local bookkeeping callback; it introduces
no command-line option, remote credential, submission schema or service. Existing
`ExecuteAsync` and executable/Tool behavior remain unchanged. Offline commands
and dashboard serving are refused by the admission entrypoint.
An in-process owner may explicitly set `includeTimingDiagnostics:false` on this
new entrypoint. Only the non-authoritative `observationTiming`/`lifecycleTiming`
JSON fields become null; default/normal CLI output stays unchanged. Primary,
postprocessing, Run identity, other result fields and persisted evidence are not
filtered or reconstructed. This permits a bounded result consumer to avoid large
diagnostic arrays without increasing its envelope limit or parsing partial JSON.

After input validation, locked executable hash verification and fixed runtime
configuration, the callback receives `RunStartIdentity`: newly allocated Run ID,
Scenario ID/definition/source hash, admitted build ID, effective Environment hash,
and the validated source Environment byte hash. It contains no paths, Goal
contents, credentials, Recording or Plan authority. The raw Environment digest is
an in-memory correlation input only; it is not added to stored input evidence,
CLI JSON or Planner observations. The owner must compare independently admitted
source identities before registering its job/attempt. Do not learn expectations
from final CLI output, saved result or an untrusted worker message.

The callback runs once, before Gua Trace/Recording artifact ownership and before
game launch/connect. A false return gives exit2/RunAdmissionRejected; callback
faults give exit2/RunAdmissionFailed without exception contents. Actual caller
cancellation before or during the callback gives exit4/CommandInterrupted, even
if the callback returns true. No Run primary or successful result is fabricated.
Input/SDK/Plan validation can refuse earlier without invoking the callback.

This is synchronous trusted code, not a sandboxed provider: it must return promptly
and perform only local bookkeeping, with no blocking IO, game access or remote
calls. It grants no alternate Run/result authority and does not change deadlines,
guarded input, adoption, host attestation or cleanup contracts. The executable
lock is released on refusal. A create-only private Run reservation directory with
its closed `.owner` marker may remain; it is not a started Run, result, active
process, listener or Gua artifact. Its retention belongs to the explicit output
root owner. No existing directory is deleted or reused to hide a refusal.

The public entrypoint can therefore register a local expectation before execution,
then correlate the final CLI JSON and saved Run using the existing consumer
contract. Registration alone never proves a remote worker genuine, a Scenario
Passed, postprocessing complete, or a device safe to release/bill. Native fixture
tests are distinct from real-game or deployed Cloud acceptance.

## Connected Run-to-report acceptance

`RunReportConsumptionAssertions` is a test-only consumer of these existing
contracts. It correlates the final CLI Run ID, status, reason and postprocessing
with `RunArtifactReader`, verifies the current Scenario hash and build/Environment
selection, then obtains that Run's opaque report link from `/api/snapshot`.
The HTTP response is the existing sandboxed iframe wrapper, not raw report bytes:
decode its `srcdoc` and compare it with the receipt-validated saved HTML. Reading
the snapshot/report must leave every original Run file unchanged. Controlled
wrong-Run CLI evidence must fail correlation before serving any report.

The existing cross-platform Dashboard fixture exercises this connected consumer
with a real finalized Gua Trace and a persisted Failed Run. Its CLI envelope is
constructed test evidence, not an executed game command. The Windows scripted
runtime roundtrip and no-Goal rows use their actual CLI entrypoint output; the
same-Recording trial and adopted Replay also pass their actual result through
this consumer. Existing click/Goal/Recording/adoption/cleanup assertions remain.
These tests use the CLI application entrypoint in-process plus actual loopback
HTTP. They do not prove a new external CLI process, packaged native game run,
browser rendering, real action2d run or deployed Cloud integration.

```powershell
dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --filter "FullyQualifiedName~DashboardTests|FullyQualifiedName~ScriptedExploreRuntimeTests"
```

Run the Windows native rows on the established Windows verification machine,
then full regression, boundary checks, archive/fresh Tool and contract smoke.
Linux executes the connected saved-Trace/HTTP consumer, but the Windows native
rows return the existing platform refusal before game composition. Retain exact
tree, first failure artifacts and per-stage evidence. A passing local consumer
cannot establish every #18/#26 browser or real-Runner acceptance condition.
Before implementing actual Cloud submission or delivery, obtain the Cloud repo
and caller contract: admitted worker inputs, result acquisition, artifact policy
and destination. Those are the remaining integration decisions; a new service,
authentication scheme or transfer protocol is not selected by this test slice.
