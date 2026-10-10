# Saved-run dashboard

The dashboard reads an explicit `scenarioRegistry` and explicit artifact roots.
Caller-supplied allowed roots remain the authority: registry paths cannot grant
additional access. The registry is the Scenario population, including entries
with no saved Run. Updating the registry is an explicit operation outside this
read-only viewer. Refresh rereads the registry and artifacts; filters operate on
the current snapshot and never launch a game, Planner or Replay.

Choose a game build and effective Environment SHA-256 separately. A Scenario's
current result matches all five identity fields: stable ID, definition version,
exact Scenario bytes hash, actual game build ID and effective secret-free
Environment hash. Changing content invalidates an earlier result even if its
version was accidentally retained. History retains other targets and prior
definitions. Runs with missing identities remain visible with `Unknown` fields;
an Environment source file, Gua library build ID or directory name cannot supply
missing game identity.

Runner statuses are displayed without rescoring. `Unrun` means no matching Run
in a complete index; `Unsettled` means a Run has no final result. Invalid or
inaccessible results are `ResultUnreadable`. Unreadable Run metadata is
`RunUnreadable`, with its independently readable saved result retained in detail.
`PassedPostProcessingIncomplete` shows Passed and incomplete mandatory
postprocessing together. It corresponds to exit 11 rather than normal completion.
The latest Run uses its saved start timestamp, never a later cleanup completion
time or a filesystem modification date. Equal start timestamps and multiple Runs with unknown ordering show
`LatestUnknown`; earlier Passed results cannot hide later failures.

Trace and Recording show independent receipt states: `NotExecuted`,
`OmittedOnSuccess`, `CaptureFailed`, `SaveFailed`, `Saved`, `Missing` or
`Unreadable`. Completion metadata that cannot be decoded has
`CompletionUnreadable`. A saved common Gua HTML report listed in the Trace
receipt links to the existing Viewer. Every associated file's bounded size and
SHA-256 are checked, and the report is checked again when opened. No Playtest
timeline or Trace format is introduced. Saved Trace without a listed HTML
report explains that no report link is available. Reports execute in a sandbox
without origin, network, forms, popups or access to the dashboard.

The list represents registered Scenario execution, not coverage of all game
features or a guarantee of game quality. Partial indexing cannot certify either
the latest result or absence of Runs: `IndexIncomplete` remains explicit after
an artifact-root failure or an indexing limit.

## Host integration

The CLI composition owner resolves its explicit inputs and calls
`DashboardApplication.RunAsync(DashboardIndex, port, readyCallback, token)`.
`DashboardIndex` takes the registry path, artifact roots, caller allowed roots
and `DashboardLimits`. It indexes immediate Run directories only. Defaults bound
the index to 1000 Runs, 16 roots, 1 MiB per opened file and 32 MiB total index
read bytes; configuration can lower these limits within hard ceilings.
Registry validation uses the existing bounded static contract validator.
JSON and YAML registry/Scenario references pass regular-file, no-link and byte
admission before static reference validation. UI ordering uses fixed-width UTC
tick strings to preserve .NET's 100 ns precision and normalize timezone offsets.

The server binds only `127.0.0.1` on an explicit port, serves generated assets
embedded in the CLI assembly, rejects cross-origin and non-GET requests and
provides no filesystem path endpoint. Assets require no Node/Web build step in
the distribution. Startup and browser opening belong to the CLI composition
owner; this module supplies no executable command or automatic browser launch.

The #9 adapter recognizes `kind: playtestRunStorage`, `storageVersion: 1`
separately from public `RunDocument`. The execution owner may supply optional
`RunArtifactMetadata.Identity` and `StartedAt` before `BeginPreparation`.
These fields are persisted under the existing redaction and create-only policy;
the owner must verify the actual connected build and effective Environment before
recording them. Legacy envelopes do not acquire fabricated identity fields.
Their embedded Scenario ID is used only to group historical records.
`result.json` still uses the public independent result reader. The dashboard
never infers a final result from `primary.json`, a Trace event or a pending file.

The output tree must be owner-private. Linked paths and non-regular files are
rejected. Read-only viewing is not an OS sandbox against concurrent changes by
another process with write access to that tree.
