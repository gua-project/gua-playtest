# Replay composition

`ResolvedReplay.LoadAsync` validates the Plan graph with the static contract validator
and retains its exact source bytes. Scenario, Recording and Plan hashes refer to UTF-8
file bytes, including BOM, whitespace and YAML spelling. Parsing or public copy mutation
cannot change that retained identity. Playback does not reread a changed path. Adoption
evidence is external to the Plan and hashes that retained Plan; the Plan never contains
its own acceptance hash. See [contracts](contracts.md).

`ReplayDriver.ExecuteAsync` uses the execution owner, certified Running boundary and
owned cleanup from [execution](execution.md) and [preparation](preparation.md). The
trusted preparation callback performs Scenario Setup, compatibility and mode checks,
registers fallback input cleanup, and returns an `IReplayObservationFeed`. The optional
initial condition must be established in the same certified boundary unit. An arbitrary
earlier snapshot cannot substitute for it. Replay never restores Trace snapshots.

The feed captures success, failure and the requested checkpoint together at one trusted
condition-clock boundary. Each checkpoint starts a fresh condition session when playback
reaches it. Repeated boundaries run in file order. Each reached checkpoint receives fresh
observation authority under its own timeout, WaitTimeout and the original Run deadline;
it must settle before `onGoal` can pass. This authority consumes no action or decision
budget and cannot authorize more playback. Recording coverage is contiguous from zero
through its final step, including normal releases; splitting at checkpoints does not skip
steps. A batch reserves its whole action count before any dispatch. Existing approved
work can settle after the last action budget is consumed; a new operation cannot reopen
that exhausted budget.

`afterPlan` retains an earlier whole Goal success fact and continues all steps,
checkpoints, completion confirmations and mandatory failure monitoring. Completing all
steps alone cannot produce Passed. `onGoal` reports the first omitted step in
`ReplayProgress.OmittedFromStep`; progress separately reports dispatched/completed steps,
completed checkpoints and Plan completion. Safety cleanup and its exit 11 obligations
remain separate from the immutable primary result.

When `onGoal` becomes verified during an in-flight batch, reserved suffix requests close
immediately. The adapter receives `ReplayDispatchClosedException` on a later send and
settles the actual prefix with required input neutralization. A partial successful receipt
is accepted only for this explicit Goal closure and the exact dispatched count; failed or
unconfirmed prefix results still fail. Impossible completion counts do not enter progress.

`IReplayPlayback` is a trusted adapter. Gua resumes playback on a worker; `IReplayCalls`
marshals its reads and actual sends back to the serialized execution owner. Every send
uses its batch index once, in order, and invokes `beforeSend` immediately before native
enqueue after transport preflight. This boundary rechecks cancellation, deadlines,
current admission, delivery state and authority. An enqueue acknowledgement is not an
action completion. Closed queues revoke late work. All synchronous callbacks must be
bounded by the concrete transport's request policy.

If an adapter reports enqueue without invoking `beforeSend`, the owner records an
uncertain consumed attempt and rejects the adapter contract. That evidence cannot
authorize a send, confirm a result, refund the attempt or permit a retry.

## Published Gua adapters and timing

`GuaUiReplay` delegates v1 UI playback to public `GuaReplayer`. `recorded` keeps Gua's
sequential inter-step delays; those delays can include response time and do not prove
original host application times. `conditionSynchronized` uses the Recording's existing
wait when present and otherwise its delay. The same selector is resolved strictly again
at actual dispatch, with query membership checked against the revision-bound tree.
There is no first-match choice, alternate target, coordinate fallback or route repair.
Because public Gua's recorded mode would ignore an existing wait, that combination is
explicitly Unsupported. The adapter does not silently remove the wait.

UI wire v1 lacks atomic remote epoch guarding. A trusted host lifecycle lease, shared
with **every** frame writer, reset and replacement, is mandatory. A local connection lock
alone cannot provide that guarantee. Completion must match actual request ID, session
epoch and resolved target; a reset after host execution does not confirm the old action.

`GuaTimedReplay` delegates game-input-only v2 conversion and execution to Gua's public
Timed Segment importer/executor. Original offsets and equal-offset order are retained.
Each batch subtracts the preceding Recording offset, rather than response latency. UI,
coordinate fallback, existing wait conditions and general reset/cleanup requests are
rejected by this path. Holds must have normal releases in that complete segment; a split
that cannot satisfy this contract is rejected. Owner-scoped safety release is separate
from Plan play authority and uses Gua's bounded fresh cleanup budget after cancellation.
The caller also registers its independent fallback cleanup during preparation.

Gua's live UI recorder samples `relativeMilliseconds` before selector resolution/enqueue,
after an existing wait. Imported diagnostic/legacy offsets can have unknown provenance.
Public v2 conversion labels that provenance `legacy-unknown`: offsets are scheduling
intentions, not measured original application times. No adapter claims to recover lost
send/application/response timestamps.

`GuaReplayTimingPolicy` explicitly supplies segment, lateness, cleanup, clock and strict
capability requirements from the effective Environment. Gua owns the common-origin
scheduler, ordered application, normal hold release and cleanup. Real execution and lease
budgets remain independent of a controlled simulation clock. Gua's public import preserves
provided leases and applies its existing legacy lease rule; validation rejects a lease
that cannot cover the real execution/lateness budget. Playtest does not extend a lease or
re-input a hold. Simulation needs an explicit scope; application-time and same-tick
requirements need corresponding host capabilities and measured completion evidence.
Unsupported strict modes fail before playback instead of falling back to weaker timing.

These APIs consume Recording, while Trace remains Gua's execution evidence format.
They provide no Planner or adoption command. Concrete engine wiring, Profile/permission
certification, real Planner isolation, Trace persistence and CLI composition use their
respective host integration boundaries. Public package 1.1.1 does not include upstream
PR180's unpublished guarded remote additions.
