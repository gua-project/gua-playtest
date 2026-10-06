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
Preparation reads the session's validated authoritative clock and uses absolute
operation deadlines. A provider's own `TimeoutException` is a Host failure, not
proof the Runner deadline elapsed. Caller cancellation is attributed only through
the owner's token normalization; unrelated provider cancellation remains a failure.

## Explicit policy and authority

`PreparationPolicy` is trusted execution policy, separate from Scenario meaning
and Planner input. It requires mode, endpoint with an explicit port, expected game
build, no endpoint user-info/query/fragment, protocol, host profile, clock,
capabilities, strict-start/build-attestation
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
Launch-mode retry backoff races the exact owned process exit watch; a confirmed
exit terminates with ProcessExited without another connection attempt. Retries
share the original whole-Preparing deadline; an explicitly longer retry
delay is not shortened by the per-operation ceiling.
Each backoff retains an absolute target and verifies clock progress after wake.
An early wake gets a bounded independent physical wait; a clock that still has not
reached the target fails closed rather than spinning or starting another attempt.
Providers return promptly, honor cancellation, and release acquisitions returned after cancellation.
The coordinator also releases late returned connections with a finite independent
shutdown ceiling, and registers each on-time acquisition immediately.
Shutdown ceilings use an independent physical clock, including late self-release
after the Run has finished; they do not consult a frozen or failed Run clock.
Registered cleanup remains governed by #6's overall deadline and release ordering.

`HostIdentity.AttestedGameBuildId` is actual approved game-host/fixture evidence,
never the Gua package's BuildId or a port-match inference. Protocol/profile/clock
and every required capability must match.
Missing identity, capability collection, synchronized boundary or feed fails with
fixed stage-specific Host evidence before Running or dispatch.
Capability membership checks on initial and captured identities use the same
bounded pure-worker path as Setup metadata; blocking or throwing collection code
cannot stall the owner or bypass identity-stage diagnostics.
A strict start rejects outstanding requests before any Setup; it never resets
to conceal them. Scene/save Setup can
change epoch, so identity and strict requests are rechecked after Setup.

`IApprovedSetup` is an Environment-approved fixture mapping, inaccessible to the
Planner. Runner validates the entire operation ID allowlist/count before dispatch,
checks authorization again at each operation, and enforces both the whole Setup
deadline and per-operation/whole-Preparing deadlines. Maximum operations is explicit
and bounded at 1000.
Metadata properties, collection Count/index/Contains and authorization checks are
pure worker-safe reads, isolated within the same bounded Step as asynchronous work.
A blocking read cannot prevent the owner timer/cancellation; late reads cannot dispatch
or mutate Run/cleanup/Trace. The Setup deadline includes metadata acquisition time.
An unended metadata worker keeps ownership exclusion closed and reports unconfirmed
cleanup, preventing unlimited replacement workers. Its late end does not reopen
an exclusion whose outcome was already frozen as unconfirmed.
Receipt failure and unknown outcome terminate without retry;
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
The terminal `OwnedCleanup.OwnershipRelease` stage follows every input/resource
release, including resources registered later by execution. It is skipped if any
preceding resource release is unconfirmed, even when diagnostic evidence is full.
Registration failure also preserves exclusion because a closed cleanup registry
cannot prove that all other resources were released.
Its callback is registered immediately after lease acquisition, before any await;
preparation expiry therefore cannot omit ownership from the cleanup snapshot.
Provider work start and terminal lease confirmation share a closed gate under the
lease lock. Counters cannot be sampled as zero just before late acquisition starts.
Unended provider work, including approved Setup effects, keeps exclusion closed even
after connection release; certified no-effect retry delay is the sole untracked step.
Initial registration failure rolls back the lease before any provider can start;
Busy tracing and all provider callbacks run outside the global lease lock.

Only `Launch` acquires an `IOwnedProcess`. Shutdown targets the exact `Process`
handle created by `Process.Start`, never a process name, discovered PID, another
user's attached process, or a whole process tree. Descendant ownership requires
independent proof and registration. Process shutdown and connection release are
registered with #6 cleanup and have finite deadlines even after caller cancellation.
Connection/owner input release uses `InputRelease` before the process's
`ResourceRelease`, retaining a live host for authoritative release confirmation.
Failure preserves the primary result and reports resource release unconfirmed.
An acquisition still pending at cleanup confirmation also keeps local exclusion
closed, even if its late task subsequently self-releases; that frozen outcome is
not silently rewritten into confirmed cleanup or permission to reclaim an owner.
An attached session owns only its connection/subscriptions/owner input, not the
external host. `ReleaseAsync` must return authoritative evidence and cannot claim
lease expiry as confirmed input release. Launched process exit remains visible
through the returned observation feed during both a blocked capture and a blocked
Planner/action/change wait.
This wrapper remains pending until its actual underlying capture ends after
supersession, so #6 can join it and retain the old authoritative unit together with
the fresh post-work capture. Cancellation is translated to the wrapper's caller
token. It does not advertise independent capture scopes. Its process-exit watch
has a separate lifetime from source cancellation. Process exit interrupts both
capture and the monitor's join; cancellation callback failures
are retained as inner evidence without replacing the selected Host exit cause.

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
Owned process status is pure worker-safe metadata, bounded and launch-stage
wrapped during preparation and execution. Source failure classification is stable:
untyped source defects retain Runner ExecutionError even when losing-watch cancellation
also faults; typed Host lifecycle failures preserve their cause. Ready same-cycle
failure evidence outranks cancellation; a cancellation received before an asynchronous
fault becomes ready cannot retroactively acquire that evidence.

Execution lifecycle trace writes are queued (at most 1000) and joined by the bounded
Diagnostics cleanup stage; persistence cannot delay a ready lifecycle failure.
Record must be worker-safe and owner-independent. Writes are serialized by one
sink gate and independently bounded: successful-stage writes use the original
preparation deadline and operation ceiling; rejection diagnostics use the explicit
operation ceiling on an independent real clock. A blocked initial write cannot
prevent cancellation or launch any host. Diagnostic write failures retain type/stack
as secondary evidence and cannot replace the typed host rejection. Late workers
have no Run or dispatch authority. Trace persistence and prelaunch run-summary
creation are #9/#15 composition work.
The #6 primary remains structured Preparation/Execution/Host/Runner evidence;
typed host failures retain Host-origin ExecutionError (Failed/exit 1), while
unknown Runner defects retain exit 10. Original wrapper/provider exception type
and stack evidence is preserved for #9 redaction, never arbitrary Planner feedback.
The owner's exception recorder traverses nested typed wrappers and aggregate
children iteratively with reference deduplication and the existing evidence-item
ceiling. Leaves take precedence over nested aggregate wrappers after preserving
the public boundary; traversal is separately capped at max(4096, 128 × remaining
evidence slots), up to 100000 visits. Stack/visited growth is bounded too. Concurrent exit-watch failures remain visible
even when the source capture has already completed successfully.
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
