# Gua observation and action bridge (issue #7)

Core remains native-free. `Gua.Playtest.GuaIntegration` alone owns Gua.Testing/
Gua.Runtime 1.1.1 dependencies. No Gua sources, native libraries, protocol schemas,
Selector implementation, input scheduler or World command interpreter are copied.

## Observation boundary

`BridgeObservations(endpoint, profile, requestTimeout, maxNodes, maxBytes)` owns
one GuaWebSocketContext and its Observe cursor. Runner judgement and Planner
projection require distinct instances with explicitly configured host endpoints.
The supplied profile is checked against the returned Observe profile. It does
not change the remote host profile. In Gua 1.1.1 the managed World methods accept
a profile parameter but do not send it to the bridge; the connection/host ceiling
is authoritative. Do not connect a Planner to a Debug endpoint and filter its
result afterward. No profile escalation or credential resolution happens here.

`Read(JsonObject)` accepts a validated `common.read`; `ReadBatch` polls once for all active reads in a temporal evaluation pass. It reuses Gua UI/World
queries and resolves every selector afresh. Returned `BridgeReadCollection` has
the actual collection time, completeness and immutable result list. Each read
has an explicit availability/reason, source/session/profile/runtime identity,
independent source revision/frame and Observe owner/registration/type identity.
No empty-success, zero-value or first-match fallback is used. Matching intermediate Observe events and paired before/after enum catalogs are returned even when the final value returns to its old value. Temporal consumers must batch every active read each pass; sequential independent calls share one cursor and cannot recover notifications consumed by the earlier call. Standard reads are snapshots only and do not prove uninterrupted state between reads. Quantifiers and
condition evaluation remain with #4/#5. One assertion with multiple returned
targets must be rejected by the quantifier consumer, never reduced to index 0.

Value and its paired enum catalog are cloned directly from the original wire
JsonElement. Consumers convert `Value.GetRawText()` / `EnumCatalog.GetRawText()`
to JsonObject for the #4 comparison API. Do not deserialize Value numbers through
double before the comparator. The adapter refuses dynamic type mismatch and
keeps getter errors without arbitrary exception text. Standard and named reads
are separate even when both are called `visible` or `state.checked`.

Snapshot/queries/tree/context checks are bracketed; an epoch or revision change
returns stale. Independent host sampling is not presented as simultaneous.
Observe identity retains the snapshot's frame, standard identity the tree frame.
There is no identity cache to accidentally reuse across replacement or reset.
Query truncation and configured byte/node limits return truncated. The packaged
managed client buffers a remote response before this adapter can inspect its
length: these are returned-document limits, not a total transport-memory ceiling.
A hard preallocation transport ceiling remains an upstream/integration obligation.

The existing connection-owned Snapshot/cursor API supplies continuity. Gap and
stale_session remain explicit and sticky until `Resubscribe()` is invoked.
Resubscribing creates a new boundary, never reconstructs missed history. #5 must
invalidate accumulated temporal evidence at such a boundary. An unknown poll
reply is not retried; Gua invalidates that cursor's owning connection. Disposal
releases the owned cursor/connection and never resets or kills the host.

## Action boundary

`ActionAttempt` separates Run ID, execution ID, action ID, session epoch and Gua
request ID. Selector and freshly resolved Runtime ID are retained for UI attempts.
Each execution ID is reserved before dispatch, including rejected/uncertain
requests. Calling it again returns its existing attempt rather than sending again.
Attempt storage has a caller-provided finite ceiling.

Status and confirmed stage are distinct. Host completion retains original Gua
error codes. A successful attack input cannot establish damage, a Goal or causal
observation change. TimedOut/Aborted preserve their enqueued/dispatch stage:
they are not proof of nonexecution, cancellation or rollback. Unknown receipt or
consumed completion replies remain Pending and have no resend path. Runner's
finite deadline calls `EndWait` to record TimedOut/Aborted with the retained stage.
An uncertain consumed-completion poll is not repeated. Pre-dispatch exceptions
retain Rejected/NotSent, allowing a corrected action with a new execution ID. Polling
requires matching request and epoch; UI completion also requires its Runtime ID.
Terminal feedback is retained. Later evidence does not silently rewrite a timeout.

`OwnedGameInput` is a **local trusted-host route**, not an external game client.
It reuses GuaGameInputSession's guarded native enqueue and pure validation with
expected epoch and profile-specific action revision. Native consumes under the
same guard. Permissions/cancellation are rechecked at the enqueue boundary. The
adapter exposes Semantic/Raw Input only; Cleanup/reset/release-all are refused.
Confirmation-required actions are refused until Runner provides trusted consent.
Dispose releases only this Run's owner; the supplied GuaRuntime and other owners
remain alive. Timed input uses Gua's existing Recording executor in #10/#22.
Secret-valued input and hostApplied timestamp evidence are not implemented here.

`BridgeUiActions` uses the existing managed remote UI client. In 1.1.1 UI wire
does not atomically compare expected epoch while enqueueing. Dispatch therefore
fails closed with `atomic-dispatch-unavailable` unless trusted host setup supplies
a real lifecycle lease shared by its reset/replacement/frame writers. A polling
check or arbitrary no-op token is not such a lease. The tests use an actual host
frame-writer lock. An external Godot/Unity process cannot supply that in-process
lock; this adapter does not claim safe external Running dispatch for it. Gua's
remote EnqueueAction maps generic InvalidOperationException to InvalidArgument;
that result is retained as dispatch-unconfirmed, not proof of nonexecution.

## Actual upstream API evidence and unresolved external route

All source links are pinned to Gua 1.1.1 commit
`88f5dca4aa97c5d5187ab66ea4416377f3affc96`:

- [Managed remote context](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/bindings/dotnet/src/Gua.Testing/GuaWebSocketContext.cs): `Query`, `EnqueueAction`, request-scoped `TryPollActionEvent`, `GetContextStatus`; no game-input remote session method. UI enqueue sends nodeId but no expectedSessionEpoch.
- [Managed World transport](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/bindings/dotnet/src/Gua.Testing/GuaWebSocketWorld.cs): `WorldQueryCommand` ignores the public profile parameter.
- [Managed Observe transport](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/bindings/dotnet/src/Gua.Testing/GuaWebSocketContext.Observe.cs): Snapshot+subscription, generation-bound tokens, lost-reply connection invalidation.
- [Managed guarded local input](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/bindings/dotnet/src/Gua.Runtime/GuaGameInput.cs): `ValidateGameInput`, `GuaGameInputSession.SendGuarded`, owner-specific `PollResult` and Dispose.
- [Native bridge](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/native/gua-ws-bridge/src/ws_bridge.cpp): per-connection game-input owner and release on disconnect; `press_game_input_action`/`set_game_input_action` command uses existing unguarded enqueue handler; expectedSessionEpoch is parsed for reset_context, not game input/UI enqueue.
- [Existing wire client](https://github.com/gua-project/gua/blob/88f5dca4aa97c5d5187ab66ea4416377f3affc96/packages/inspector/src/core.ts): narrow game-input wire commands and receipt/poll flow, without atomic epoch/revision guard.

A thin wire adapter could transmit those existing remote commands and preserve
owner/correlation/no-resend, but it cannot make a check-then-send atomic. Target
replacement/reset or permission/action-map changes can occur between checks and
enqueue/consume. AT-OBS-005 and current-permission/freshness cooperative acceptance
cannot claim that race closed. No unguarded wire adapter was substituted.

Candidate minimum upstream extension: an additive capability-negotiated guarded
UI/game-input request with required source/session epoch and current profile/map
revision; compare and consume under the native context lock, retain rejection
and completion identity, retain connection-owned cleanup. Add managed remote
methods preserving ambiguous transport outcomes. Old clients keep existing wire
behavior; Playtest rejects the insufficient capability. This is a proposal for
parent approval/coordination, not a Gua edit or new release in this PR.

Game build identity is not inferred from GuaVersion.BuildId. Approved fixture/
host attestation remains #8/#16 integration work. Real Godot/Unity, secret input,
external guarded dispatch, bounded preallocation and downstream Runner integration
remain unverified. Issue #7 stays open until its full acceptance is established.
