# Player one-click Explore to adopted Replay

This is the bounded same-artifact integration step in issues #1, #10 and #14.
It uses existing commands: one Player public-UI scripted Explore, its official
Gua Recording, a managed candidate, an AI-free trial, explicit adoption and a
separate Replay Run. It adds no general strategy, Navigation, broader Replay,
repair, external script or live Codex execution.

## Prepare and Explore

Follow [scripted-explore-run](scripted-explore-runtime.md) with the chosen game's
actual executable hash, one explicit UI target, a permitted scalar Goal, finite
limits and private input/output roots. Keep the Player Environment unchanged
for Replay. Replay uses the published Player child setting and still checks the
actual host profile/source/epoch. Neither command can enable remote in a
Shipping/OFF build or elevate a compiled observation ceiling.

Require Goal verification, confirmed mandatory cleanup and saved Recording.
Use the exact official Recording artifact identified by the successful Run;
verify its recorded hash. Do not substitute a fabricated Recording or an older
Run's file. A click/Recording alone is not a successful Goal or adoption proof.

## Create and trial the candidate

```text
plan create --managed-root <private-plans> --allow-root <private-input-root>
  --scenario <original-scenario.json> --recording <explore-recording.json>
  --timing conditionSynchronized
```

The command copies fixed original bytes. It does not adopt the Plan or infer
expected conditions from observed state. Review the copied Scenario/Goal and
Plan before authorizing a trial. Keep `.plan-authority`, the candidate graph
and raw Recording private. The authority key authenticates local trial
provenance; it is not a remote permission or game authority.

Run [replay-run](replay-runtime.md) against the returned managed Plan path and
same Player Environment/build with `--allow-candidate`. Use a fresh owned process
and fresh private game save/appdata for each Run, according to the game's
existing approved launch options. No reset, save mutation or replay repair is
performed by this workflow. The trial dispatches the complete single step even
if the Goal was initially true; a confirmed action alone does not prove Goal.

The single original relative delay must fit the existing finite operation
window. If it does not, Replay refuses it; do not trim Recording bytes, rebase
clocks or extend limits silently. General recorded timing/holds/checkpoints
remain outside this runtime lane.

Require exit0, `postProcessingComplete=true`, complete/Goal-verified progress
and `trialState=Saved`. A failed, incomplete or unconfirmed trial cannot become
adoption evidence. A candidate without `--allow-candidate` must be refused
before launching a process.

## Explicit adoption and separate Replay

Adoption is a separate user decision, after reviewing the intended expectations:

```text
plan accept --managed-root <private-plans> --plan <managed-plan.json>
  --trial <successful-trial-id> --approve --approve-expectations
```

Neither Explore nor trial automatically supplies these approvals. Then run
`replay-run` again with the adopted Plan and without `--allow-candidate`, using
another fresh process/save/output. Require `acceptedAtStart=true`, a distinct
Run ID, complete Goal-verified progress, confirmed cleanup and verified saved
results. Replay has no Planner port and does not capture another source
Recording. Changed Plan/Scenario/Recording bytes invalidate old trial/adoption
authority; do not overwrite the source Recording or reuse stale success.

## Evidence boundary

`ReplayProfileTests` launches a native ProcessProbe child and reads its actual
profile on all supported CI platforms. `ScriptedExploreRuntimeTests`' roundtrip
case uses native ProcessProbe transport on Windows for the same-Recording
sequence, candidate/approval refusals, copied-input mutation, one click per Run,
Player receipts, distinct Run IDs and verified result readers. That fixture is
not a real game. Non-Windows CLI runtime cases verify their unsupported-platform
refusal and do not exercise the Windows composition.

Publish only counts, results and hashes. The CI `roundtrip.json` is a bounded
fixture summary, not a second runtime Trace format; raw Recording, private Plan
inputs and `.plan-authority` are excluded. Real game/archive verification of
this full sequence requires a Windows environment and separate acceptance
evidence. Existing one-click engine evidence does not prove general Replay,
Navigation, real Codex isolation/authentication or the full v0.1 release loop.
