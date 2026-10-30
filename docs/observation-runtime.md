# Observation-only Windows Run

`observe-run` launches an explicitly selected Gua-enabled Windows executable and
evaluates a Scenario through the existing HostPreparation, RunExecutor and
condition evaluator. It uses no model service and dispatches no game input.
This is a deterministic observation lane, not AI exploration, navigation or Replay.

```text
observe-run --allow-root <input-and-output-root> --allow-root <game-root>
  --scenario <scenario.json> --environment <environment.json>
  --executable <absolute-game.exe> --executable-sha256 <64-hex-digest>
  --working-directory <game-directory> --output-root <existing-private-directory>
  [--argument <one-child-argument>] [--child-env APPDATA=<existing-private-directory>]
```

Arguments are passed individually without a shell. For a headless Godot build,
explicitly supply `--argument --headless`. The optional child environment accepts
only `APPDATA` and `FACTORY_SAVE_DIR`, each an existing directory within an allowed
root. Save-file arguments remain explicit game arguments. `GUA_BRIDGE_PORT` is
set from Environment.connection.endpoint, which must be an explicit IPv4
`ws://127.0.0.1:<port>/` endpoint without credentials, query or fragment.
The child otherwise retains SystemProcessLauncher's existing inherited environment;
this mechanism is not a game-process sandbox or permission grant.

Environment.buildId must be `sha256-<lowercase-executable-digest>`. The command
locks the executable against replacement/write during the Run, verifies its hash,
and checks the Windows TCP listener owner against the exact process acquired by
SystemProcessLauncher before connection and on subsequent captures. A listener
belonging to another process is refused, even if it speaks Gua. Gua's library
BuildId is never used as the game build identity. An external provenance record
must additionally bind that executable and its DLL/package hashes to the selected
source commit/archive. The command does not infer archive provenance from a hash.

The current lane accepts one immediate scalar `assertion` success condition, with
bool/integer/number/string reads explicitly allowed by Environment. It refuses
Setup/fixture, credentials, failure conditions, grouped/temporal conditions,
navigation, input permissions and raw input before launching. Player and Debug
are accepted only when the actual host observation profile matches; nothing
promotes the host profile. Snapshot reads claim no interval continuity.
Other CLI commands retain their existing execution-capability checks.

The real clock begins before Preparing. Scenario and Environment duration/action
ceilings are reduced by the existing EffectiveExecution resolver. Preparing uses
the Environment preparation deadline, while each Gua request uses its wait
timeout. Conditions, primary-result arbitration and cancellation stay in Runner.
There is no alternate Goal evaluator. The observation module availability check
is not a live Codex/provider check.
`maxActions: 0` remains zero: the certified initial Goal can pass, otherwise the
existing ActionsExhausted rule terminates the Run. It never grants an input.

Run metadata is saved before launch. Captured facts are recorded in the common
Gua Trace before primary confirmation; late captures cannot append Before facts
after that boundary. Primary is saved before owned cleanup. Recording is
`NotExecuted` because no actions were dispatched. Playtest applies its existing
real-data redaction and capacity policy; Gua owns Trace/reader/report HTML.
Trace capacity is reduced to reserve the existing writer's manifest and summary
budgets, without increasing Environment.traceMaxBytes.

The CLI returns the established Run exit code and a JSON `details` object with
runId, artifactDirectory, driver, primary phase/origin, persistence and
postprocessing facts. Cancellation may leave a diagnostics debt under Runner's
existing rules even when the owned process was released. A passing Goal with
incomplete mandatory postprocessing remains exit 11. Cleanup terminates only the
exact acquired process; it does not establish graceful game-exit behavior,
terminate descendants or exercise an attached-game lifecycle.

Input-producing external Runs still require an atomic host dispatch capability.
Published Gua.Testing 1.1.2 exposes `GuaRemoteGuardedSession`; its public capability
and host support must be verified and connected by an input-producing composition.
This observation command does not wire that API or establish its game acceptance.
A local mutex, a successful observation, or this listener-owner check does not
replace that capability. Live Codex still requires independently verified OS
isolation, an approved authentication reference and explicit usage limits.
