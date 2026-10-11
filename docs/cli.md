# Explicit CLI inputs

`gua-playtest` is noninteractive. Add `--json` anywhere to request one final JSON
document on stdout; stable diagnostics go to stderr. Normal output is concise
text. Exceptions, source document content, credentials and environment dumps are
never diagnostics. All paths and roots are explicit; no discovery, parent config
merge, arbitrary `--set`, Goal override or Replay-to-Explore fallback exists.

```text
validate --allow-root <existing-directory> <json-or-yaml-file>
validate --gua-schema <packaged-schema-name> <json-file>
doctor --native
plan create --managed-root <private-root> --allow-root <input-root> --scenario <file> --recording <file> --timing <recorded|conditionSynchronized>
plan accept --managed-root <private-root> --plan <managed-plan.json> --trial <trial-id> --approve --approve-expectations
report --allow-root <existing-directory> --trace <saved-trace-directory> [--output <new-html>]
explore --allow-root <existing-directory> --scenario <file> --environment <file>
replay --allow-root <existing-directory> --plan <file> --environment <file>
```

`validate` and `report` perform no game launch, connection, operation, Planner or
Goal scoring. Their exit 0 means command success. Report reads a finalized Gua
Trace through `GuaTraceReader`; optional HTML uses the embedded `GuaTraceReport`
Viewer. The output's existing parent must be inside an allowed root. Output inside
the source Trace (including filesystem case aliases) or overwriting an existing output is refused. Reader issues or an
unfinished Trace fail closed. Detail formats remain Gua-owned.

Input directory and file identities are checked before an equivalent case alias
is rebased onto the explicitly supplied root for snapshot validation. A distinct
directory with matching names or bytes gains no authority. This also applies to
saved Trace directories and existing report output parents.
Static commands preserve the contract's symbolic-link and junction resolution,
and check the final target against the explicit roots. Managed Plan commands
retain their stricter artifact path rules.

Plan commands use [PlanManager's managed evidence chain](plan-adoption.md).
Candidates preserve exact validated Scenario and Recording bytes, including Goal
meaning and hashes. Adoption requires both explicit approval flags and a genuine
full AI-free ReplayDriver/store trial. A JSON evidence file alone grants no
authority; forged, stale, incomplete and `onGoal` trials remain rejected.

If a candidate graph references secrets, supply repeated
`--secret-env <secretKey>=<environment-variable-name>` to either Plan command.
Only referenced keys are resolved, only in the host process. Missing supplies,
duplicate or unreferenced bindings fail. No variable enumeration occurs. Values
feed the same `PersistenceRedactor` used by PlanManager, together with sensitive
field names `password`, `token`, `authorization`, `secretValue`. Known collisions
are rejected before pinned-byte publication rather than silently rewriting Goal
or Recording meaning. Reference keys remain references, never literal secret
values. The CLI passes its secret-reference snapshot hashes through PlanCommand
to PlanManager. A replaced Scenario, Recording or Plan fails before candidate or
adoption publication, so secret policy and saved bytes cannot refer to different
inputs. The managed root and authority key must remain private host resources.
The complete command response, including its code and candidate path, must also
pass the private secret policy before candidate or adoption publication, in both
text and JSON modes. Already-resolved values remain protected if another supply
is missing, cancellation occurs, or policy construction fails. Resolution stops
at the persistence policy's 1024-key and 65536-character ceilings. Diagnostics
that collide with a known value are suppressed; a content-free JSON document
preserves the final-document boundary when named fields would disclose a value.

Explore/Replay accept repeated `--allow-root`, optional positive `--max-actions`
and `--max-duration-ms`, and repeated `--deny-action`. Offline resolution retains
the source bytes/hashes and reports all three ceiling inputs: Scenario,
Environment and CLI. Effective ceilings take their minimum; action IDs intersect
Scenario restrictions with Environment permissions before applying deny. A larger
CLI ceiling never raises an existing limit. Other limits remain the explicit
Environment values. This is a provisional offline reduction, not host-certified
permission or execution identity. It does not resolve credentials.

Execution currently returns exit 2 / `ProductionRuntimeUnavailable` after valid
inputs. Environment v1 has no host mode, executable, authoritative clock or auth
policy fields. A trusted composition must provide those separately, preserve
Plan timing (including simulation), attest the actual game build, intersect all
permissions and resource limits with current host authority, and pass only
necessary secret supplies into host preparation and persistence. Its identity
must hash effective secret-free conditions separately from the retained source
Environment hash. Missing inputs cannot be filled by defaults. Published
Gua.Testing 1.1.1 has no required external atomic guard; a local mutex or dummy
lease cannot replace it. Explore additionally requires the approved isolated
live Codex provider. No successful Run or trial is fabricated at this boundary.

`CliApplication.WriteRunResultAsync` supplies the result display boundary for a
trusted composition, using `RunOutcome` and `PersistenceResult` without
duplicating result rules: 0 Passed, 1 Failed,
2 Invalid, 3 TimedOut, 4 Aborted, 5 Unverified, 10 Runner error, 11 Passed with
mandatory postprocessing/storage incomplete. A secondary failure preserves a
non-Passed primary exit. Command cancellation is exit 4. Child stdout/stderr are
drained without buffering or saving raw text, so neither channel inherits the CLI
JSON stream or leaks arbitrary game output into diagnostics.
