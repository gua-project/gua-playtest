# Explore orchestration

`ExploreDriver.ExecuteRunningAsync` composes inside the existing `RunExecutor`
execute callback, after its certified Running boundary. Supply the existing
`RunSession`, `PlannerGate`, Planner, cleanup owner and authoritative initial
capture. Preparation, primary arbitration, total budgets and mandatory cleanup
retain their existing owners. See [execution](execution.md) and
[planning](planning.md) for approval and completion rules.

The trusted host supplies `IExploreObservationFeed`: each frame combines the Run
condition unit, independently projected public Planner state and private progress
reads from the same capture time. `IExploreWork` executes an adopted decision
using existing host operations. Its synchronous reads and sends use
`IExploreCalls` to marshal onto the Run owner; a send invokes `beforeSend` exactly
once immediately before dispatch and returns its actual acknowledgement.
An acknowledgement that omitted this guard consumes an uncertain attempt and
terminates as unconfirmed; recording that evidence grants no dispatch permission.
`Completed` requires a confirmed result and neutral owned inputs. A missing or
uncertain receipt terminates through the existing failure rules. Worker callbacks
must be short and synchronous. Elapsed waits use the authoritative condition
clock and retain their full duration before their finite observation window.
Before the first adopted action can own input, Explore registers the supplied
owner-scoped release callback with the existing cleanup owner. Final cleanup
attempts it even when work is interrupted or returns no neutral-input receipt.
The callback must release only this Run's inputs and tolerate already neutral
inputs. Failed release remains post-processing evidence under the existing rules.

## Progress and stall detection

`ProgressDefinitions` is optional trusted configuration. No definitions, missing
reads and unavailable values are Unknown; they cannot establish improvement or
invent numeric zero. Progress never substitutes for the configured Goal.
Initial and live private capture timestamps are contract validated before use.
Every live frame updates private progress immediately; only the latest pending
frame is retained for the next action boundary, independent of capture frequency.

Milestones use existing prepared conditions and temporal evaluation. Initially
True milestones establish a baseline. Each later first False-to-True attainment
counts once, including transient attainment observed between decision boundaries.
Private temporal deadlines join the existing RunMonitor condition clock wakes;
each wake requests fresh capture without interrupting approved work or granting
result authority.
Metrics have a stable configured ID, direction and positive `MinImprovement`.
Only a certified change from the best value reaching that threshold replaces the
best. Small changes accumulate against that best; deterioration, recovery and
runtime identity replacement do not reset history.

The trusted host may supply a comparable semantic situation. Exclude volatile
revision, frame, animation and runtime identity from that projection. Repetition
compares the whole approved action and its before/after situation, including
repeated multi-action routes. Missing or oversized situations break comparability.
History retains at most 1,000 action boundaries; a larger configured repeat
threshold cannot be established within this history. Progress reads are bounded
and contract validated. Private values, milestone definitions and metric scores
are not added to Planner input or feedback.

Use explicit positive `StagnationActionLimit`, `StagnationRepeatLimit` and
`RecoveryDecisionLimit`; there are no production defaults introduced here.
Known progress without improvement, comparable repetition, or a Planner
`stuck`/`cannotProceed` report enters `SuspectedStall`. Recovery starts only at a
completed decision boundary, after normal result confirmation and input release.
It consumes both the existing total decision budget and the cumulative recovery
budget. Improvement returns to Exploring without replenishing either budget.
Neither recovery nor driver reconstruction resets progress history. Replacing
definitions or effective limits within one Run is rejected.

Recovery exhaustion uses the existing `RecoveryExhausted` budget reason. It means
this exploration reached its configured limit; it is not proof that the game is
impossible or faulty. A Planner finish claim is not machine success. Replay does
not use this recovery loop.
