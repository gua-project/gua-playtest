# Replay failure timing

`ReplayTimingJournal` is a Windows test-only transport between the Foundation
Replay fixture and its owned ProcessProbe. It is not part of a shipping package.
The private `<pid>.timing` mapping is never uploaded. The fixture publishes
`replay-timing.json` only for an unexpected CLI exit or the existing controlled
lost-completion case. Successful Runs add no public diagnostic file.

Only fixed numeric fields are accepted: phase, process/operation/request IDs,
monotonic ticks and states. No exception messages, owners, paths, selectors,
Recording/Plan bytes or credentials are serialized. The scope uses AsyncLocal
filtering; SDK and Runner notifications carry no execution authority. Normal
CLI lifecycle output ignores these opt-in Replay events.

The journal retains at most 1,024 rows in each of three segments: parent, trial
host, adopted host. `dropped`, `missing`, `committed`, `attemptedAtLeast` and
observer/error counts describe incomplete evidence. Zero rows are not proof
that work never started. Header/commit markers are best-effort snapshots; killed
hosts may leave partial records. Errors reported at disposal are not exhaustive
when a process was killed. A phase omitted after a fault remains unknown.

| Phase | Meaning | operation / a / b / c |
|---|---|---|
| 1 | host startup | 0 / QPC frequency / 0 / 0 |
| 2 | parent starts a Run | 0 / trial=1 or adopted=2 / 0 / 0 |
| 10, 11 | before/after existing approval clock read | 0 / clock TimeSpan ticks / ResultDeadline TimeSpan ticks / Run number |
| 20, 21 | queue request/admitted | call ID / 0 / 0 / Run number |
| 22, 23, 24 | owner invocation/callback return/caller delivery | call ID / 0 / 0 / Run number |
| 25 | queued callback fault | call ID / cancellation=1, other=0 / 0 / Run number |
| 30, 31 | guarded send start/reply | request ID if available / dispatch state / 0 / Run number |
| 32, 33 | guarded Poll start/reply | request ID / dispatch state / 0 / Run number |
| 34 | correlated recording event lookup | request ID / found=1 / 0 / Run number |
| 35, 36 | guarded send/Poll threw | request ID if available / -1 / 0 / Run number |
| 50 | host consumed | request ID / 0 / 0 / 0 |
| 51, 52 | host Goal-frame start/end | 0 / 0 / 0 / 0 |
| 53, 54 | host EmitActionResult start/end | 0 / 0 / 0 / 0 |

Parent `c` is captured in the Run's execution context, rather than inferred
from notification arrival. SDK request IDs use their signed 64-bit bit pattern.
Queue operation IDs are unique within the process, not SDK request IDs. The
existing one-click fixture plus Run/host segment binds them; they must not be
equated or joined across unrelated Runs.

All timestamps subtract one header QPC origin. On the same Windows machine QPC
has a common timebase across processes; host startup validates its frequency.
Rows are stored by segment/slot, so sort `ticks` to inspect cross-process order.
Convert to milliseconds with `ticks * 1000 / frequency`. Do not substitute a
separately started Stopwatch's elapsed value. For this real-clock fixture,
the deadline's common-clock interval is bounded by phase 10 and 11 timestamps
plus `(b-a) * frequency / TimeSpan.TicksPerSecond`; an arbitrary virtual provider
clock cannot be mapped this way. Clock bracketing reuses the existing provider
read; it adds no provider clock access, deadline or approval authority.

Goal-frame/emit end means the host call returned, not proof of remote delivery.
Queue admission, owner invocation and guarded reply are separate evidence.
Subscription polls in observation timing are not guarded Poll calls. Numeric
timestamps alone cannot separate SDK internals, network transit and host work.
Observers, mapping page faults, barriers and timestamp calls can perturb timing;
the journal performs no per-event flush or application-lock acquisition, and
disabled production diagnostics perform no journal I/O. A later green test
does not resolve an earlier natural failure.
