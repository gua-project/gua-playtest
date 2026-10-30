# One guarded UI click

`click-run` runs one explicitly selected UI click through the same Windows launch,
build hash/owned-listener checks, Preparing boundary, RunSession reservation,
RunMonitor condition evaluation and RunResult/Trace/cleanup as `observe-run`.
It is deterministic trusted work, not a Planner, exploration, or a Replay Plan.
Scenario still defines its Goal; no action sequence is added to Scenario or Setup.

```text
click-run --allow-root <input-output-root> --allow-root <game-root>
  --scenario <scenario.json> --environment <environment.json>
  --executable <absolute-game.exe> --executable-sha256 <64-hex-digest>
  --working-directory <game-directory> --output-root <private-directory>
  --target-id <ui-id>
  [--argument <child-argument>] [--child-env FACTORY_SAVE_DIR=<private-directory>]
```

The [launch/identity/root/argument rules](observation-runtime.md) apply. The UI ID
contains1..128 ASCII letters/digits or `-_.:`. Environment.permissions.actionIds
must be exactly `["click"]`, rawInput false, and the scalar success read must be
explicitly allowed. Optional Scenario.setup is a desired initial condition only:
immediate assertions/all/any are read in the same batched synchronization unit
and evaluated by PreparedCondition before Running. Every read needs permission;
false/unknown refuses before any click. No fixture or state mutation is supplied.
Scenario.constraints.allowedActionIds, when present, must also allow click.
Credentials, fixture, navigation, grouped/temporal Goal or failure conditions
remain outside this lane. `observe-run` still refuses Setup and every
input permission and never accepts `--target-id`.

The supplied action ceiling is not increased. If the initial Goal already holds,
no click is sent. Otherwise one operation is reserved. Zero budget prevents the
click. A fresh unique selector result is matched against the actual UI tree; the
published Gua1.1.2 `guarded_dispatch_v1` session binds source, epoch, profile and
revision at the host. A local lifecycle lease or unguarded send is not used.
The Run's current dispatch authority and owned listener are checked at the public
SDK's beforeDispatch boundary. An enqueued receipt is not completion or Goal
success. Completion is correlated by the SDK and independent fresh observations
are evaluated by the existing condition owner. A completed click can still fail
its Scenario or time out waiting for the Goal.

An uncertain receipt/completion is terminal and has no retry/resend. A rejected
preflight remains unsent; a dispatch already attempted consumes its action even
when execution is unconfirmed. The command emits actual guarded receipt facts
and action accounting separately from primary status. Trace stores bounded facts;
submission success means Enqueued only, and completion keeps the original numeric
Gua error code. SDK dispatch errors are bounded code tokens, not arbitrary remote
diagnostic text.
capture/storage failures affect postprocessing. This lane does not capture an
official Recording: its receipt is NotExecuted, and no Replay acceptance is
claimed. It does not use a model, authentication reference, or model budget.

Preparing checks actual WebSocket readiness before handing a connection to
HostPreparation. A read-only connect/handshake cancellation not caused by the
caller, or a WebSocket connection error, is retryable only after self-release.
The existing Preparing deadline/attempt ceiling still applies; identity or
capability incompatibility is not retried. Unregistered connections remain
tracked for final cleanup if self-release fails. Monitor capture supersession
joins the serialized bounded read before its final fresh capture; only terminal
cleanup irreversibly interrupts the shared socket. Guarded session disposal and
owned process shutdown remain separate obligations, with unconfirmed release
reported rather than assumed.
