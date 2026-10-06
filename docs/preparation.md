# Launch, attach and trusted preparation (issue #8)

`Gua.Playtest.Runner.Preparation` is a native-free orchestration boundary, using
the issue #6 execution owner, original preparation deadline and `OwnedCleanup`.
No second Run state machine, budget, Trace wire format or Gua implementation is
introduced. A new `HostPreparation` is constructed for every Run, including any
restart. The CLI composition and approved engine fixture adapters remain explicit
integration work; this module does not make the validation CLI a playable command.

Compose `PrepareAsync(run, cleanup, setup, plannerCheck, token)` into the production
`RunExecutor.ExecuteAsync` preparation callback; return the resulting
`PreparedHost.Boundary` and keep its `Feed` for monitored Running work. The preparation
deadline comes directly from that session, so callers cannot renew it. The executor
evaluates the certified initial capture before invoking its execution callback.
No user-supplied callback gets an alternate primary-result authority.

## Explicit policy and authority

`PreparationPolicy` is trusted execution policy, separate from Scenario meaning
and Planner input. It requires mode, endpoint with an explicit port, expected game
build, protocol, host profile, clock, capabilities, strict-start/build-attestation
requirements, finite operation/retry/shutdown limits and a bounded connection
attempt count (1..100). Durations must be positive and at most one day. Launch
requires a `LaunchCommand`; attach rejects one. `SystemProcessLauncher` requires
absolute existing executable and working directory paths and passes individually
specified arguments without a shell. Nothing searches for ports or executables.
Launch creation returns an async task promptly; a delayed OS acquisition is either
registered or shut down under the independent bounded cleanup ceiling.
No engine install, package publication, authentication or credential change occurs.

`IPreparationConnector.ConnectAsync` performs only connection establishment. A
`ConnectionNotReadyException` certifies that no connection or side effect was
dispatched/acquired and is the only retryable outcome. All other exceptions,
unknown receipts, timeout and cancellation terminate the preparation attempt.
Retries share the original whole-Preparing deadline. Async providers return
promptly, honor cancellation, and release acquisitions returned after cancellation.
The coordinator also releases late returned connections with a finite independent
shutdown ceiling, and registers each on-time acquisition immediately.

`HostIdentity.AttestedGameBuildId` is actual approved game-host/fixture evidence,
never the Gua package's BuildId or a port-match inference. Protocol/profile/clock
and every required capability must match. A strict start rejects outstanding
requests before any Setup; it never resets to conceal them. Scene/save Setup can
change epoch, so identity and strict requests are rechecked after Setup.

`IApprovedSetup` is an Environment-approved fixture mapping, inaccessible to the
Planner. Runner validates the entire operation ID allowlist/count before dispatch,
checks authorization again at each operation, and enforces both the whole Setup
deadline and per-operation/whole-Preparing deadlines. Maximum operations is explicit
and bounded at 1000. Receipt failure and unknown outcome terminate without retry;
successful Setup alone does not prove the start preconditions. Attach needs explicit
fixture authorization and never implies reset consent. The fixture must implement
only its declared operation mapping; it cannot substitute arbitrary shell/internal
game logic for approved scene/save/checkpoint setup. Gua context reset is not game
restoration. `CurrentRestorable` is independent evidence, never inferred from a
recorded current snapshot. Profile/capability insufficiency fails closed.

## Ownership, monitoring and release

The coordinator's endpoint exclusion covers this Runner process and exact endpoint
URI only. It cannot exclude another Runner process, endpoint aliases, a user or
manual game input. It is not a reset/frame lifecycle lock and cannot authorize
unguarded external dispatch. Hosts must supply actual exclusive ownership/guard
evidence for stronger guarantees. Input owner separation does not mean game-state
isolation. A lease is released after every acquired obligation confirms release;
unknown release leaves local exclusion closed rather than authorizing a new Run.

Only `Launch` acquires an `IOwnedProcess`. Shutdown targets the exact `Process`
handle created by `Process.Start`, never a process name, discovered PID, another
user's attached process, or a whole process tree. Descendant ownership requires
independent proof and registration. Process shutdown and connection release are
registered with #6 cleanup and have finite deadlines even after caller cancellation.
Failure preserves the primary result and reports resource release unconfirmed.
An attached session owns only its connection/subscriptions/owner input, not the
external host. `ReleaseAsync` must return authoritative evidence and cannot claim
lease expiry as confirmed input release. Launched process exit remains visible
through the returned observation feed during a blocked Planner/action wait.

## Initial synchronized observation

After Setup, identity and limited Explore Planner availability checks, the trusted
adapter synchronizes a new subscription and initial evaluation capture. It must
verify source/epoch/continuity and check only actual beginning prerequisites;
future Boss/attack absence and a normal empty UI are not blanket rejection rules.
No whole-Tree stillness prerequisite is introduced. Replay needs no Planner.

`SynchronizeAsync(captureRequestId, token)` returns `InitialBoundary` with current
captured `HostIdentity`, continuity, precondition truth, the echoed request ID,
actual real capture time, synchronization evidence ID, exact `RunObservation`,
continuous feed and independent current-start restoration evidence. The capability
and profile are rechecked in that capture, not inferred from earlier metadata.

The paired startup boundary provided by #6 is the integration contract: arm only
after readiness, correlate a new capture with its one-use RequestId, retain actual
real and condition timestamps and synchronization evidence, then certify the
exact precondition/initial-success/failure unit. Do not relabel an old preparation
snapshot. Real delivery delay consumes the original Running duration; it does not
backdate only condition time or credit preparation toward a hold. Before any AI or
Replay dispatch, #6 begins Running on that certified boundary and evaluates both
initial condition trees. Initial failure/death wins over success. The feed must
retain all changes from that boundary and invalidate stale/gap/epoch evidence.

## Trace and integration status

`IPreparationTrace.Record(PreparationEvent)` adapts fixed stage/code evidence into
the common Gua Trace lifecycle sink owned by #9. Its first call is Started before
launch/connection, so startup failure is traceable without a live engine. It is
not a persisted new format, arbitrary exception-message channel or Planner export.
Trace persistence and prelaunch run-summary creation are #9/#15 composition work.
The #6 primary remains structured Preparation/Execution/Host/Runner evidence;
more detailed Launch/Connect/Identity/Setup/Planner/Synchronize failure stages are
retained by this sink without overwriting the original failure during cleanup.

Requirement coverage: GOAL-003; START-001..007; cooperative REPLAY-003. Unit
`PreparationTests` exercises launch/attach ownership, Codex-free Replay, launch
failure trace, identity/strict pending/capability failure, forbidden Setup and
unknown result, unsatisfied prerequisites after successful Setup, stale epoch,
original preparation deadline, cancellation after acquisition, finite safe retry
and unknown-release exclusion. These are contract tests, not real engine acceptance.

Keep #8 open: actual approved Unity/Godot host fixture attestation, subscription/
capture synchronization and guarded external play dispatch remain unverified.
Installed Unity was inspected at `C:/Program Files/Unity/Hub/Editor`: 2022.3.22f1,
6000.5.3f1 and 6000.7.0b2. Presence is not a licensed engine run or FIX-007 evidence.
Gua 1.1.1 lacks the published external atomic guard needed by #7; Gua PR180 is
unpublished. No future capability is claimed. #7/#11/#15/#16 must connect actual
host identity, permission/clock/profile, fixture, initial conditions and guarded
dispatch; #9 must persist lifecycle evidence before launch. AT-START-001..007,
AT-GOAL-003 and cooperative AT-REPLAY require those independent real results.
