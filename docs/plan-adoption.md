# Plan candidates and explicit adoption

`PlanManager` is a trusted host service over a private managed root. `CreateAsync`
copies exact validated Scenario and Gua Recording bytes into a random directory,
then creates `plan.json` with relative fixed references, full Recording coverage,
no checkpoints and `afterPlan` completion. Supplied Goal conditions are copied;
observed states never become expected values automatically. Edit conditions
explicitly before trial. Any change, including whitespace or timing, requires
another trial and approval. Existing Recording waits remain intact.

Supply the host's `PersistenceRedactor`. Known secret collisions reject publication
rather than rewriting pinned bytes. Recording must already be a Gua-owned redacted
export with secret references. The private root and `.plan-authority` key are host
resources excluded from Planner access and public artifact bundles. Reparse points
and nonregular files are rejected; this is not an OS sandbox against another
process modifying the trusted root or stealing its key. HMAC authenticates local
host provenance, not publisher approval, permissions or safe actions. Losing the
key invalidates old receipts.

The Replay composition opens its Run store and calls `BeginReplayPreparation`
with the effective host-certified identity before invoking the driver,
passes that store as `trialStore` to `ReplayDriver.ExecuteAsync` to bind its Run ID,
calls `ConfirmPrimary` at primary confirmation, then
`CompleteReplay` after cleanup and artifact receipt validation. `RecordTrialAsync`
receives that store, the driver-issued `ReplayOutcome` and effective Scenario,
build and Environment identity from trusted preparation. Only the driver constructs
an outcome. It binds retained Plan/Scenario/Recording hashes to `ReplayProgress`.
All steps must be dispatched and confirmed, all checkpoints must complete, the
machine Goal must be verified, `afterPlan` must finish, and mandatory postprocessing
and persistence must succeed. `onGoal`, omitted suffixes and unverified Goals cannot
issue a trial. ReplayDriver has no Planner port, certifying an AI-free trial.

Trial publication copies immutable `run.json`, `result.json` and `completion.json` into the candidate,
checks their hashes against the bytes originally published by the same live store,
rechecks raw and decoded summary content with the host Plan secret policy,
then signs their hashes, Run identity and progress in `proof.json`. Temporary Run
storage can be removed afterwards. Plan never contains its own hash or adoption
state. `AcceptAsync` verifies the current graph, signed proof and summaries,
requires explicit adoption and approval of expectations, and creates `accepted.json`
once. Its signed payload contains the existing `AdoptionEvidenceDocument`. Imported
or hand-written evidence cannot confer authority. `IsAcceptedAsync` rechecks these
identities and reapplies the current host secret policy to retained summaries,
signed payloads and envelopes. A strengthened policy can invalidate older trial
or adoption evidence; verification preserves those files and refuses authority.
Publication never overwrites an existing file or adoption.

Summary copies retain the Run store's explicit per-file limit (up to 16 MiB) in
the signed proof; candidate inputs retain the 4 MiB contract limit. A cancellation
recorded after primary confirmation permits adoption when mandatory cleanup and
persistence succeed, following `RunOutcome.PostProcessingComplete`. Real cleanup
failures remain disqualifying. Evidence preserves candidate-relative paths,
including subdirectories, and rechecks them with the retained hashes.

`PlanCommand.ExecuteAsync` supplies a standalone handler for host integration.
The packaged CLI currently dispatches neither `plan` nor `replay`. The shared
composition must pass its secret policy and connect these handler forms:

```text
plan create --managed-root <root> --allow-root <input-root> --scenario <file> --recording <file> --timing <recorded|conditionSynchronized>
plan accept --managed-root <root> --plan <managed-plan.json> --trial <trial-id> --approve --approve-expectations
```

The Replay integration must check `IsAcceptedAsync`; an unaccepted candidate requires
explicit `--allow-candidate` and must retain all normal host safety limits. Persist
the trial through the sequence above and return the trial ID for adoption. The
Plan handler creates candidates or accepts trials; engine execution belongs to
Replay composition.
