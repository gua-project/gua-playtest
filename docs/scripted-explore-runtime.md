# Player UI scripted Explore

`scripted-explore-run` composes the existing PlannerGate and ExploreDriver with
a built-in deterministic script. The script proposes one explicit UI click;
later requests report `cannotProceed` and remain subject to the existing finite
decision/recovery budgets. It never retries that click, resets the game, or
claims the Scenario Goal. The normal RunSession/RunMonitor evaluates the Goal.
This lane needs no Codex, model credentials, external script or paid API.

```text
scripted-explore-run --allow-root <input-output-root> --allow-root <game-root>
  --scenario <scenario.json> --environment <environment.json>
  --executable <absolute-game.exe> --executable-sha256 <64-hex-digest>
  --working-directory <game-directory> --output-root <private-directory>
  --target-id <explicit-ui-id> [--argument <game-argument>]
  [--child-env FACTORY_SAVE_DIR=<private-directory>]
```

The Windows [launch/hash/listener ownership rules](observation-runtime.md) and
[single-click argument/root rules](single-ui-runtime.md) apply. Environment
profile must be `Player`, actionIds exactly `["click"]`, rawInput false. Goal is
one scalar assertion over an allowed **UI standard** read with an exact explicit
ASCII ID (1..128 characters, letters/digits or `-_.:`). Role/name/regex selectors
are outside this lane. Optional Setup uses
only immediate assertions/all/any of allowed UI standard reads. Observe/World
private Goal, credentials, fixture, Navigation, failure conditions, temporal or
grouped Goal, held input and general action scripts are refused in this lane.
Other runtime commands retain their existing contracts.

In addition to Goal/Setup reads, Environment.permissions.reads must explicitly
contain these public preflight reads for the command's target:

```json
[
  {"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"visible","valueType":{"type":"bool"}},
  {"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"enabled","valueType":{"type":"bool"}}
]
```

## Public source and dispatch

The child is explicitly launched with the published Gua runtime setting
`GUA_OBSERVATION_PROFILE=player`. This lowers the Run's observation profile;
it cannot enable remote in a compiled Shipping/OFF distribution or elevate a
Player-only distribution. The selected child configuration and script identity
participate in the effective-runtime hash. Actual returned profile/source/epoch
and owned endpoint are still checked.

Judgement and Planner projection own distinct connections, scoped to each full
standard-UI capture. Their point values come from one native UI tree, selected
using Gua's selector engine. Source/profile/epoch bracket that immutable tree;
later UI revision changes cannot turn the point snapshot into mixed values.
No continuous/delta history is claimed. Existing ReadBatch behavior is unchanged.
Idle read connections are closed so unsolicited snapshot pushes cannot hold up
the game between these snapshot-only operations. The public feed reads
only the two explicitly permitted UI fields; it never filters a Debug capture or
copies Scenario conditions, expected values, Setup, fixture or private progress
into Planner input. Original server action definitions are obtained through the
published guarded SDK and validated against the existing v1/v2 catalog schemas.
An unavailable catalog is refused, even for this UI-only script; no empty
production catalog or metadata is fabricated. A host must support this public
metadata route as well as observe_v1 and guarded_dispatch_v1.

The proposal carries actual Run/request/observation correlation IDs. PlannerGate
validates it and reserves the operation. ExploreDriver marshals work through
IExploreCalls; no worker directly mutates the Run or sends an action. Before the
SDK's actual guarded send, approval is rechecked against the public basis and
fresh source/epoch/profile/revision. Host-side atomic guard remains authoritative
for changes after that hook. Stale or cancelled unsent work is refused; a
consumed/unconfirmed request is never resent.
During active request settlement, cached public reads are marked stale with no
values and retain their real original ID/time; they are not advertised as fresh
Planner input. Fresh public capture precedes the next request. The actual send
owner remains alive until completion/cleanup and is distinct from read scopes.

## Outcomes and evidence

An initial satisfied Goal sends no proposal or click. Zero action/decision
budget cannot create dispatch authority. A confirmed click alone is not Goal
success. Missing Goal, cannotProceed, interruption, capability rejection and
uncertain completion keep their existing finite Runner reasons. With no progress
definitions, progress is unknown; UI revision changes are not invented progress.

RunResult, primary snapshot, mandatory cleanup and postprocessing keep their
existing owners. Official Gua Recording contains the actual click/request and
native pre/post revisions; `runner.explore-ui-click` Trace receipts also carry
the proposal's Run/request/observation references. Acquisition/capture/save
failures remain distinct from no execution and cannot overwrite the primary.
The command reports action/decision counts and script request count separately.

This is a bounded scripted Planner integration. It does not supply general
exploration strategy, Navigation, general Replay/repair, private judgement
alongside a Debug host, or real Codex process isolation and authentication.
