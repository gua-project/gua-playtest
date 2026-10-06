# Playtest configuration and exchange contracts

Playtest schema version 1 separates a Scenario's single Goal, an Environment's execution conditions, and a Replay Plan's operation references. These contracts implement issue #2; they do not execute a game, score a Goal, or accept a Planner proposal. Schemas and static validation are offline. The pure Core assembly never loads Gua native libraries.

## Models and schema names

| JSON kind | Model | Schema |
| --- | --- | --- |
| scenario | ScenarioDocument | scenario.schema.json |
| environment | EnvironmentDocument | environment.schema.json |
| replayPlan | ReplayPlanDocument | replay-plan.schema.json |
| plannerInput | PlannerInputDocument | planner-input.schema.json |
| plannerDecision | PlannerDecisionDocument | planner-decision.schema.json |
| run | RunDocument | run.schema.json |
| result | ResultDocument | result.schema.json |
| scenarioRegistry | ScenarioRegistryDocument | scenario-registry.schema.json |
| adoptionEvidence | AdoptionEvidenceDocument | adoption-evidence.schema.json |

`ContractJson.Serialize` uses these camelCase wire names, string state/status enums and omission of optional null fields. Serializing a model creates new bytes; it does not preserve an old reference hash.

Every object controlling Playtest behavior rejects unknown properties. Version 1 is an integer, not a package version. JSON and YAML decode into the same JSON data model. YAML accepts mappings with string keys, sequences, quoted strings, block strings, and JSON scalar spellings; ambiguous YAML scalar spellings remain strings. Explicit tags, anchors, aliases, merge keys, duplicate keys, multiple documents, and nonfinite numbers are rejected. Anchors are unnecessary for this model; rejecting all aliases also prevents cycles and expansion attacks. File size, nesting, and node limits apply before schema evaluation. Diagnostics are stable codes with no file content, user property names, secret values, or parser exception messages. Connection credentials use secretKey references; endpoint user-info, query and fragment are refused to keep credentials out of copied configuration. An endpoint path is allowed for explicitly hosted websocket routes.

`goal.objective` is natural language; optional `goal.success` and `goal.failure` are machine conditions. No steps, Goal references, or workflows are allowed in Goal. A Scenario with no success describes unverified exploration; a Planner's finish report cannot establish Passed. Setup describes a desired condition; an Environment fixture identifier selects an approved runtime fixture. No configuration field evaluates code.

## Gua 1.1.1 reuse and version checks

Gua schemas under `schemas/gua-1.1.1` are unchanged copies from tag `gua-v1.1.1`, commit `88f5dca4aa97c5d5187ab66ea4416377f3affc96`, with the upstream MIT license and SHA-256 manifest. The product embeds these files and resolves their `$id` references locally. An unresolved reference fails; there is no HTTP fallback. Package consumers use Gua.Testing 1.1.1 `GuaDistribution.ReadSchema/ValidateJson` for Gua-only validation. Core reuses the exact public Value schema without calling `GuaValue` P/Invoke.

Value v1 uses bool/integer/number/string/enum/list/set; integer range is ±9007199254740991, numbers are finite, enum identity includes enumType, and collection element type survives empty values. No null/object/nested collection is introduced into Value. Existing Gua nullable state and Input vector objects keep their distinct schemas. Enum membership comes from Gua enum-catalog-v1; catalog availability and dynamic type consistency are runtime checks.

Gua `GuaVersion.EnsureCompatible` checks required ABI, protocol and capabilities for the connected context before use. ABI 1/protocol `"2"` describe 1.1.1, not a new Playtest version policy. UI tree v2, World tree v1, Value/Observe v1, Trace v1, metadata v2, Timed Segment v1 and spatial-r1 are independently versioned. UI Recording's managed reader handles v1; game-input v2 uses `GuaTimedSegmentImport.FromRecording`. A schema accepting Recording v2 does not promise UI v2 replay. No blanket rejection of future compatible Gua versions is added.

## Targets and observation identity

Target source is `ui`, `object`, or `world`. UI Selector is the original Gua selector object; object Selector is the original world-selector. World properties have no selector. Read region is `standard` with a field, `observe` with a name, or world `property` with a name. Same-named standard fields and Observe registrations never shadow one another. Standard field paths use documented tree names, for example `visible`, `state.checked`, `position.x`; `worldPosition` is not an invented wire field. Whole vector objects are not Gua Value scalars; compare their documented numeric components.

The runtime resolves semantic selectors into sourceId/sessionEpoch/runtime ID and, for Observe, ownerId/registrationId. Cache keys include source, epoch, profile and registration/type identity. Reset, owner removal, registration replacement, and unavailable/type-changed observations invalidate relevant entries. An ID alone does not prove identity. Gua's frame sample/Notify and Snapshot/cursor semantics are preserved. Missing values, gaps, truncation, stale epochs and getter errors are not empty observations. `one` never chooses the first of multiple matches. Condition truth, temporal state and operator semantics remain owned by #4/#5; this schema only represents their agreed operators and validates static type/shape restrictions.

## Fixed references and adoption evidence

Comparison operators, eager preparation, numeric/enum semantics and ordinary three-valued results are defined in [assertions.md](assertions.md). Temporal and selector quantifier semantics remain #5.

Fixed references contain `path` and lowercase SHA-256 of the exact UTF-8 file bytes, including BOM, whitespace, comments and newlines. Plan pins Scenario and Recording, but not Environment: another Environment may run the same Goal under its own compatible conditions. A CLI Goal override must be refused. Loaded bytes are retained; runtime must not reread mutable paths after validation.

Plan covers every Recording step once in original order, with no slice, deletion, insertion or route repair. Checkpoints use `beforeStep` in 0..stepCount inclusive; stepCount is the final boundary. Repeated boundaries run in file order. The recorded policy preserves existing Recording waits and forbids added checkpoints with waits; conditionSynchronized permits checkpoints as additional gates without silently deleting Recording waits. Timing capability, source provenance, permissions and completed actions remain runtime checks in #10. No second scheduler is created.

Plan never contains its own hash or adoption evidence. Separate adoptionEvidence pins Plan, Scenario and Recording and identifies a test Run with the same identities, afterPlan completion, verified Goal, completed Plan and mandatory postprocessing. A syntactically valid evidence file is not proof of adoption: #14 must read the immutable Run/result, verify all hashes, confirm AI-free full completion and explicit adoption, and copy references into managed storage. Changing conditions, timing, formatting or comments invalidates old hashes. Hashes prove bytes, not trust or authorization.

## Finite resource limits

Environment requires explicit positive integer ceilings for duration, actions, decisions, preparation, cleanup, Planner, wait, segment duration/lateness, observation nodes/bytes, regex timeout/pattern length, stagnation/repetition/recovery and Trace bytes/events/queue/attachment bytes. Trace recentSteps alone has the agreed default 100. Zero lateness is supported; duration may be zero in a Gua Timed Segment but its execution/cleanup deadlines stay positive. Gua Timed Segment hard ceilings remain 60000ms/1000 inputs. Effective runtime limits select the stricter ceilings and permission intersection with deny precedence; the validator never changes a requested clock or raises permissions. The fixture values are modest test examples, not latency/performance guarantees or new 8/3/3 or 20ms defaults. Runtime defaults outside this explicit contract belong to #6/#12/#15.

## Planner and result boundaries

One completed PlannerDecision reports execute(single/timed), observe, finite wait(duration/condition), or finish(goalClaimed/stuck/cannotProceed). Correlation is runId/decisionRequestId/basedOnObservationId. No confirmed, owner, clock override, arbitrary command/shell/URL/file access or Goal/result fields are accepted from a Planner. Gua Timed Segment clock declarations are proposals whose current permission/capability checks belong to #11. Single raw input has offset zero and cannot propose cleanup/reset. Timed cleanup is owner-scoped runtime handling, not general command access.

PlannerInput contains only projected objective, real limits/remainings, observation completeness and identity, Gua action definitions and structured feedback codes. The observation has an explicit collected `observedAt` plus independent sourceId/sessionEpoch/revision/frameSequence records for UI/object/world; these do not imply simultaneous host sampling. Available reads carry their declared Value type; unavailable/omitted/truncated/stale/gap reads carry no fabricated Value. Incomplete reads cannot claim complete collection. It is not a raw Scenario/Environment/secret dump. Static validation cannot authorize a read/action, prove freshness, consume a Decision once, or determine a Goal result; #7/#11 perform those checks.

Run separates executionState from Result status. Result preserves phase/origin/reason and postProcessing separately. Statuses are Passed/Failed/TimedOut/Aborted/Invalid/Unverified. Stable reason identifiers in this schema do not fix #6's reason enumeration or tie-break rules. Final-head acceptance of runtime status/exit behavior belongs to #6/#9/#15; Passed with incomplete mandatory postprocessing displays its issue and maps to exit 11, never normal completion.

## Paths and static validation

The API accepts an explicit file and explicit allowed roots. A relative entry argument is resolved once against caller cwd. Each reference and registry artifactRoot is resolved relative to its containing file. No directory search, parent settings merge, network/native/engine/Planner invocation, environment secret resolution, or arbitrary code execution occurs. Canonicalization follows symbolic links and Windows junctions in each path component and checks the final existing target against canonical allowed roots with a separator boundary. The allowed roots are a caller authority, never supplied by the document itself. Invalid or missing references fail locally. Retained bytes reduce content races; runtime resource opening must repeat permission and path checks because mutable filesystem links cannot be made race-free by an earlier static check.

Static validation proves structure, bounded decoding, finite values, fixed-reference hashes, declared kinds and cross-file identity consistency. It distinguishes successful form validation from unverified runtime capabilities, fixture availability, credential supply, provider support, observation schema, permissions, clock guarantees and Goal scoring. Success never indicates runtime Passed.

## Dashboard registration and matching

The explicit scenarioRegistry file is the population; no discovery from Run directories or parent files occurs. It lists unique stable scenarioId, definitionVersion, and hash-pinned Scenario file; both ID and definitionVersion must agree with the referenced Scenario. Names and paths are not identity. Content changes require a new hash even when a definitionVersion was mistakenly kept. The registry file may be updated explicitly outside the read-only dashboard.

Run matching uses `(scenarioId, definitionVersion, scenarioSha256, buildId, environmentSha256)`. buildId must identify the actual connected game build, not a port, path, claimed label, or Gua library `get_version.buildId` (which identifies Gua, not the game); #7/#8 verify the game identity through the approved host/fixture contract before run evidence qualifies. environmentSha256 identifies the effective, secret-free execution conditions after #15 resolution, not merely the source file; the source Environment fixed reference is retained separately. Condition changes, fixtures, seed/clock, permissions and effective limits change this identity. Actual secret values are excluded. Unknown/missing old identities remain historical and cannot confirm an identified target.

#15 supplies explicit registry and artifact-root arguments through its existing CLI assembly; this contract does not invent a command name. Registry loading snapshots the explicit files at view start; each explicit refresh rereads them, preserves each Run's history and performs no writes. UI filters show build and Environment separately. No matching Run is unrun; missing result is unsettled, corrupt/unreadable artifacts are read failures. #26 displays saved Runner statuses without regrading, latest failure without hiding it behind old success, omitted detail separately from missing/failed detail, and Passed plus incomplete postprocessing separately from normal completion. Detail uses the Gua Viewer, with bounded local attachments and no game/Planner launch.

## Ownership and acceptance

| Requirement or OPEN | Static responsibility here | Runtime evidence owner |
| --- | --- | --- |
| GOAL-001, FILE-001..004 | Single Goal, kinds, no override, offline validation, canonical paths, bytes hashes | #6/#8/#10/#14/#15 |
| VALUE-005, ASSERT-001, OPEN-01/04 | Exact pinned schemas, distinct target regions and type declarations | #4/#7 |
| PLANNER-004 | Strict discriminated Decision and bounded waits | #11 |
| RUN-001, OPEN-06 | State/status representation, no invented reason/tie-break semantics | #6/#9/#15 |
| OPEN-08 | Separate evidence and full-recording boundaries | #10/#14 |
| OPEN-10 | Explicit finite limits and documented locations | #6/#12/#15 |
| OPEN-12 | Model/schema names and actual Issue mapping | #15/#17 |
| DASH-001..004 | Registry, identities, roots and update contract | #9/#15/#26/#17/#18 |

Static fixtures and unit tests are evidence for forms and paths only. Explore/Replay shared-Goal scoring, actual host/version capabilities, full adoption and browser/distribution acceptance remain linked integration obligations, not claims made by static tests.
