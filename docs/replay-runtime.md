# One guarded click Replay on Windows

`replay-run` executes a private managed Plan through ReplayDriver, Gua's public UI
scheduler, remote atomic guarded dispatch, independent Goal evaluation and owned cleanup.
It has no Planner port. Offline `replay`, `observe-run` and `click-run` keep their contracts.

```text
plan create --managed-root <private-plans> --allow-root <input-root>
  --scenario <scenario.json> --recording <recording.json> --timing conditionSynchronized
replay-run --managed-root <private-plans> --plan <managed-plan.json>
  --environment <environment.json> --allow-root <input-output-root>
  --executable <absolute-game.exe> --executable-sha256 <64-hex-digest>
  --working-directory <game-directory> --output-root <private-output>
  [--argument <child-argument>] [--child-env FACTORY_SAVE_DIR=<private-save>]
  [--child-env APPDATA=<private-appdata>] [--allow-candidate]
```

Repeat `--allow-root` for additional explicit roots. Launch, image locking, listener
ownership, child arguments and SDK checks follow the existing [Windows runtime
rules](observation-runtime.md). Release configuration alone proves no capability;
Shipping/remote-OFF and Player-only permissions cannot be promoted by settings.

This lane admits exactly one public UI click with an explicit ASCII ID, no sensitive
value/secret reference, role/focus/coordinate fallback or existing wait. Timing must
be `conditionSynchronized`, completion `afterPlan`, with no checkpoints or Plan.initial.
The existing single relative delay must fit the finite operation window. Unsupported
plans are rejected before launch without changing timing, coverage or expectations.
The Goal is one permitted scalar assertion. Optional Scenario.setup is an immediate,
read-only assertion/all/any over permitted reads in the certified initial boundary.
Fixture, Navigation, credentials, raw input and failure conditions are unsupported.
All existing limits remain effective. Zero action allowance cannot send. AfterPlan
requires the complete step even when Goal was already true; completed input alone
never establishes Goal or trial success.

Original Plan/Scenario/Recording bytes and hashes are retained. Windows read-sharing
locks bind acceptance checks, playback and trial publication to those same inputs.
Gua resolves the same explicit ID again and atomically checks source/epoch/profile/
revision at enqueue. This remote adapter does not claim a host-wide local lifecycle
lease. Only the execution owner handles queued reads/sends. Each adapter is used once;
unknown receipt/completion is not retried, refunded or repaired. Cancellation before
send is unsent; after an unconfirmed send, that action remains consumed. Existing
priorities apply: ActionUnconfirmed can outrank Cancelled. Canceled optional diagnostics
remain visible; actual resource release is confirmed separately.

A candidate requires `--allow-candidate`; otherwise PlanManager must verify adoption.
The private root and existing `.plan-authority` come from Plan creation. Replay does
not recover/create a missing authority. This key authenticates local trial provenance,
grants no remote access, and must stay outside public bundles and Planner access.

A full AI-free, Goal-verified Replay with mandatory postprocessing and persistence
complete can publish a driver/live-store trial. Responses contain `progress`, input
hashes, `trialState` and `trialId`. Failed, partial, canceled or unconfirmed execution
cannot issue a trial. Trial publication failure after a successful Run reports
`SaveFailed`, incomplete command postprocessing and exit11; it does not rewrite the
already persisted primary. Replay leaves the Plan unadopted. Review expectations and
use the separate explicit [plan accept](plan-adoption.md) with both approval flags.

The source Recording is input, not a newly captured Replay Recording. Recording capture
for this Run is NotExecuted. Trace associates actual receipt facts with step zero and
retained Plan/Recording hashes. Gua owns Recording parsing, Trace and HTML rendering;
Playtest keeps its secret/capacity policy. Publish results/counts/hashes, not private
Recording content or the authority key. General exploration, walking, hold/release,
recorded timing and broader Replay capabilities remain separate integration work.
