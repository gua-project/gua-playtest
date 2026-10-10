# Codex protocol conversion

`CodexProtocolBackend` converts the App Server JSONL exchange into completed
bytes, typed backend status and optional token usage. A backend instance belongs
to one Run: it initializes one connection/thread and starts one turn per unique
decision request. `CodexPlannerAdapter` in CLI composition implements the existing
`IPlanner<PlannerInputDocument, PlannerReply>` port. Runner owns proposal adoption,
budgets, clocks, monitoring, input release and game dispatch. Neither project
references the other's implementation; no Core or Decision schema changes are
required. Usage is available on `CodexReply`, not invented as zero or added to the
Runner exchange.

When token notifications are present, usage is the delta from the pre-turn
cumulative high-water counters, so all model responses in that turn contribute
and repeated snapshots do not double count. Counters come from `tokenUsage.total`,
not `last` (the latest model response), following the
[0.150.1 protocol implementation](https://github.com/openai/codex/blob/rust-v0.150.1/codex-rs/protocol/src/protocol.rs).
Absent notifications leave usage unavailable. After any completed turn without
notifications, subsequent turn usage also remains unavailable: thread cumulative
counters cannot separate the missing turn's consumption from the next turn.

The protocol port is not a live provider. There is no process launcher, credential
resolver, authentication operation or production factory here. The existing
`CodexPlanner<TInput,TDecision>` still refuses invocation, and Replay does not
require Codex. A caller-supplied protocol transport is an owned host port, not
evidence that a connection has permission to launch Codex or reach a game.

## Exchange and limits

The host supplies only the existing public `PlannerInputDocument`, an output
schema, a redaction function and explicit finite frame/message/time limits.
Redaction traverses decoded JSON string values before serializing the user data
message. Sensitive property names are refused. No observation text becomes a
developer/system instruction. Host policy must identify the required secret
values; the converter does not discover secrets or assume that arbitrary text or
screenshots are safe. Trace persistence still requires its independent redaction.

Frames are strict UTF-8, bounded JSON objects with duplicate keys refused at all
depths. Request IDs and thread/turn IDs correlate the exchange. Stream fragments,
reasoning and intermediate messages are never adopted. Only one final assistant
message in the matching `turn/completed` notification can return bytes; its
Run/request/observation correlation must match. Secret-bearing decoded output is
refused, rather than edited into a different action. Runner still validates the
complete proposal against its pinned schemas and current authority.
An absent items view defaults to `full`. Live 0.150.1 completion notices usually
carry `summary` items; those cannot supply a proposal on their own. The converter
hydrates the final item from an identical, correlated canonical `item/completed`
event held in bounded memory. Missing canonical output is a connection failure;
changed summary text is refused. Asynchronous deliveries and not-loaded views
cannot supply proposals. This preserves ephemeral threads: 0.150.1 rejects
`thread/read(includeTurns)` for ephemeral threads, and the converter does not
enable persisted history to work around that restriction.

Known lifecycle notices can precede start responses, but their thread/turn
identities must match that exchange. Subsequent correlated item/usage/completion
events are buffered until the matching start response is verified, then replayed
in order. Aggregate pre-response events and canonical output each consume at most
the configured frame-byte limit, in addition to message and deadline bounds.

Server capability requests, duplicate response IDs, malformed frames, wrong
correlation, invalid usage and exceeded bounds close the connection's converter.
Disconnection returns a connection failure; usage-limit completion returns a
usage-limit status without server error text. There are no automatic resends.
After cancellation, timeout or protocol failure the instance cannot consume
another request. Awaited transport tasks have a finite timeout even if their
asynchronous implementation ignores cancellation; late faults are observed but
late results are discarded. Transport methods must return promptly, and the host
must dispose/interrupt its owned connection. This converter does not create or
terminate a process and does not certify a noncooperative transport's cleanup.
Concurrent decisions never share a reader. The lifetime request-ID set is capped
at 10,000; per-decision message bounds include setup/handshake messages. A transport
must cap a frame before allocating it, not only after this converter receives it.

## Version and isolation investigation

The converter targets `codex-cli 0.150.1` and uses its nonexperimental fields.
Use `codex app-server generate-json-schema` to inspect the selected binary's
actual contract. Protocol compatibility alone does not establish a supported OS
matrix or real provider acceptance.
The initialize response must advertise `0.150.1` in its leading user-agent
product before the converter sends `initialized` or starts a thread. Missing
or incompatible version evidence returns a connection failure.

The 0.150.1 `InitializeParams` has `capabilities.experimentalApi`.
`PermissionProfileListParams/Response` and the `permissionProfile/list` request
exist; their response lists profile IDs and `allowed` flags. Generated
nonexperimental `ThreadStartParams` has legacy `sandbox` and free-form `config`.
Generating with `--experimental` reveals its named `permissions` property,
explicitly incompatible with `sandbox`. Thus 0.150.1 does expose profile selection
to experimental clients; absence from the default schema is not evidence that
the binary lacks it. Both generated variants' `TurnStartParams` `readOnly` policy
have `networkAccess`, without read roots. Profile configuration, effective
restrictions, inherited tools and actual Windows enforcement still need separate
verification. This converter does not enable experimental profile selection.

Current [App Server documentation](https://learn.chatgpt.com/docs/app-server)
describes named profiles for beta clients; [permissions documentation](https://learn.chatgpt.com/docs/permissions)
distinguishes profiles from legacy sandbox settings. These current descriptions
must be checked against the chosen binary; a schema field alone does not prove
OS enforcement. The absence of read roots in the turn policy alone also does not
prove that external isolation is the only solution.

OPEN-09 remains unresolved. Acceptance must cover inherited config/environment,
MCP/connectors, browser/computer-use, local Gua sockets and all other bypass paths,
as well as source/save writes and private expected-value reads. Model service
communication is distinct from command sandbox network access. The converter
provides no authentication, game connection, CLI update or OS/global security
configuration. The owner must select the supported version,
permission/isolation strategy and authentication reference, then independently
verify enforcement before production composition can open the provider.

## Contract evidence

`CodexProtocolTests` uses an in-memory fake protocol transport, not a model or game.
It checks PLANNER-008's thread/turn conversion, PLANNER-009/FILE-006's pre-send
secret handling and safe failure results, PLANNER-007/010's cancellation/late-result
closure and failure classification, and BOUND-004/START-007's unavailable live
provider. Existing `PlannerGateTests` own completed-proposal adoption and
release-before-backend-cancellation. Existing persistence tests own save-time
redaction. Byte/frame/message/deadline bounds are explicit; no clock or scoring
rule is replaced. Requirements PLANNER-008/009, PACK-004, FIX-006 and OPEN-09/11
still require real provider/OS/Trace/package/game acceptance. Fake tests cannot
close issue #13 or certify isolation.
