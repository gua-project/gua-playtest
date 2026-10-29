# Run artifact persistence (P-08, issue #9)

## 結果と調査材料を失わず保存する

この層は Run の判定と成果物の参照を保存します。Gua Trace のイベント形式やViewerを再実装する層ではありません。例えば購入のGoalが成立しても、必須Traceのflushや入力解放が失敗したら、その事実を一次結果とは別に残します。`Passed` と必須後処理未完了の組合せは終了11で、通常の成功終了にはしません。

[RunArtifactStore](../src/Gua.Playtest.Runner/Persistence/RunArtifactStore.cs)の `BeginPreparation` → `ConfirmPrimary` → `Complete` の順に読むと、準備情報、確定済み結果、後処理の保存境界を追えます。`Complete` は成果物のidentity、長さ、hash、参照を保存時に検査します。[ArtifactContracts](../src/Gua.Playtest.Runner/Persistence/ArtifactContracts.cs)の `ArtifactReceipt` は未取得・省略・取得失敗・保存失敗・保存済みを分け、空ファイルで「保存済み」を作りません。[RunArtifactReader.ReadResultAsync](../src/Gua.Playtest.Runner/Persistence/RunArtifactReader.cs)は `result.json` の形式・サイズ・Run identityを検証します。`Verified` でも `completion.json` やTrace／Recording本体を再読してhash確認するわけではなく、保存後の成果物の無改変を証明しません。

[ArtifactTests](../tests/Gua.Playtest.Foundation.Tests/ArtifactTests.cs)は実ファイルの保存・読戻しと拒否例です。保存先は信頼されたprivate rootを用意し、秘密値は保存bufferやhashに入る前に処理します。既存Runの上書きや別ファイルへのlinkでの代用はできません。具体的な制約、既知の障害と未達のGua統合条件は下記を参照してください。判定とのつながりは [execution.md](execution.md)、全体は [開発者ガイド](developer-guide.ja.md)です。

`Gua.Playtest.Runner.Artifacts` owns local Run summaries and associations, not Gua Trace/Recording formats or a Viewer. Its sources live in `Runner/Persistence` because the repository ignores directories named `artifacts`. The trusted execution owner serializes calls; callbacks and Planner output have no storage/result authority. A private, trusted output root is required: reparse points are rejected, but this is not an OS sandbox against another process concurrently modifying the directory.

## Integration boundary

Read-only consumption uses the [dashboard adapter](dashboard.md). `RunArtifactMetadata`
accepts optional `Identity` and `StartedAt` from the trusted execution owner;
the storage envelope retains these under the same sanitization policy. Missing
legacy values remain unknown. These fields do not replace the public Run schema
or verify the actual game build/effective Environment on the owner's behalf.

1. Create `RunArtifactStore` with an explicit root, `ArtifactLimits` and host-only `PersistenceRedactor`. A random Run identity, separate directory and exclusive owner marker prevent Run reuse. Creation can fail before any file exists; the host must classify that as persistence failure, not infer a Run result.
2. At Preparing entry, before launch/connect, call `BeginPreparation`. It publishes `run.json`: profile/public scope, effective settings with source, used versions, sanitized fixed input copies and SHA-256 of those copies. Source inputs, environment variables and resolved secret values are never copied. An identified Scenario copy additionally stores sourceSha256: the writer computes this exact pre-redaction source-byte digest and verifies it against RunIdentity.ScenarioSha256 before publication. It is distinct from the sanitized copy sha256; other raw input hashes are not retained. InputCopy.SourceUtf8 supplies bounded exact UTF-8 file bytes when BOM, comments or whitespace participate in its source identity. SourceFormat explicitly selects .json, .yaml or .yml; the existing bounded decoder checks that the source describes the supplied document before its exact byte hash is accepted. Without that optional memory, the original JsonElement root bytes are the source. Neither unredacted source bytes nor caller-claimed hashes are persisted as input evidence. Secret references/availability may be explicit safe settings. The existing public Run wire document is assembled by #15; this storage envelope does not replace its schema.
3. At primary confirmation, before cleanup, call `ConfirmPrimary` with #6's immutable `PrimaryResult`, exception evidence and observation/decision references. `primary.json` is create-only. Before/After/PrimaryDecision references cannot contain AfterCleanup; cleanup references are written separately. No result is derived from an observation. A failed primary write still freezes the in-memory primary and marks persistence incomplete.
4. After cleanup, call `Complete` with the same primary in `RunOutcome`, exactly one Trace and one Recording receipt, and only AfterCleanup references. `completion.json` stores associations/postprocessing separately. `result.json` uses the existing `ResultDocument` schema, with unchanged primary status/phase/origin/reason and aggregated postprocessing. It is published last and once. #15 must carry every operation's `PersistenceResult` into postprocessing and use its exit projection. Earlier storage failure cannot be forgotten by a successful final write.
5. `RunArtifactReader.ReadResultAsync` independently uses the existing static contract validator. Missing result is Missing; truncated/wrong-schema/identity-mismatched result is Invalid. Neither a primary Passed snapshot, final observation nor a pending file supplies a missing final result.

The execution API provides `RunExecutor.ExecuteAsync(..., cancellationToken, confirmPrimary)` and the equivalent `OwnedCleanup` hook. Supply `(snapshot, token) => ValueTask.FromResult(store.ConfirmPrimary(snapshot, observations, decisions, token).Saved)`. The frozen snapshot includes primary events and original exception evidence; this hook runs before diagnostics/releases with fresh bounded cancellation authority even when the caller cancelled. False maps to PrimarySnapshotFailed; the primary is unchanged. Registering an ordinary Diagnostics callback is insufficient because caller cancellation can skip that work. #15 owns production assembly. #8's `IPreparationTrace.Record(PreparationEvent(Stage, Code))` must connect to the common Gua adapter before launch, not to a new Playtest timeline. Artifact persistence itself does not produce lifecycle Trace events.

Use the final [execution contract](execution.md) for validated clock authority, owner-thread evidence collection, certified paired Running startup, completed-work confirmation and capture cancellation/fault arbitration. Persistence consumes the immutable outcome and does not reinterpret any of those causes. It supplies no observation feed and does not claim `IIndependentRunObservationFeed`: ordinary sources must join the underlying capture before replacement, and Gua is not granted independence by assumption. The #15 owner must serialize storage calls and isolate/join a timed-out storage invocation before reusing its mutable store; a finite callback wrapper alone is not proof that underlying filesystem work ended. Live bounded-provider acceptance remains open.

The host-owned `PlannerDecisionReference` maps `DecisionRequestId` to `DecisionReference.DecisionId`, `BasedOnObservationId` to `ObservationId`, and the defined `Code` enum name to `ReasonCode`. The #15 execution owner must require `Reference.RunId == store.RunId` and obtain any action identifier from approved Gua payloads; this storage record does not itself grant proposal or dispatch authority. Raw rationale, expected criteria and artifact metadata never enter Planner projections. The real provider/guard/Trace acceptance conditions below remain open.

## Gua-owned receipts and safety

### Trusted Gua writer composition

The CLI assembly's `GuaRunArtifacts` composes Runner persistence and the
GuaIntegration `GuaRunArtifactWriter` without a Runner-to-Gua dependency.
Construct it at Preparing entry, before calling the host's launch/connect port,
with the existing store, fixed metadata, effective artifact ceilings, explicit
Gua options and host-only Recording redaction policy. Pass it as
`IPreparationTrace` to `HostPreparation`, and pass `ConfirmPrimaryAsync` directly
to `RunExecutor`'s pre-cleanup confirmation hook. The hook freezes Gua's primary
outcome and the existing create-only primary summary separately from cleanup.
The preparation owner emits the single `Started` transition; constructing the
adapter only initializes persistence. Evidence producers cannot set the primary
through the capture port.

The trusted owner uses the `GuaRunTrace` policy port's `BeginStep`, `RecordRequest`,
`Evaluate`, `Change` and `Attach` APIs for already-authorized evidence. Each
delegates to the actual public Gua session; the raw session is not exposed.
Playtest redacts real host, game and input data before Gua queues, attachments and
input-copy hashes. Configured sensitive JSON fields become null, including fields
whose values are not literal secrets. Explicit marked objects retain whole-object
masking. Actual string values containing a resolved secret are suppressed as a
whole; source-line scalars containing a secret are omitted. Host/request identities
and Recording text that cannot be changed without changing correlation or replay
are refused when they contain a real secret or a named-sensitive field cannot be
omitted without changing correlation/replay. Actual coordinates, scroll input and
host timing/correlation fields receive the same policy before Recording encoding.
A sensitive settings/input/exception container that cannot retain the required wire
shape is refused before publication.

Fixed format keys, schemas, typed results, enum values, timestamps, generated IDs
and JSON punctuation are trusted structure. A secret that happens to equal public
Viewer text such as Trace does not prohibit that public text. Playtest does not
scan the entire generated HTML/JavaScript for literal absence or reconstruct Gua's
HTML, CSP or escaping. Gua owns rendering and safe data embedding.

The projection bounds JSON bytes, strings, nodes and depth before native admission.
Recording admission checks actual text and populated sensitive fields, then bounds
the native document before attachment/save. The completed Trace is read by Gua with
explicit input limits. The same validated read result is passed to Gua's bounded
RenderHtml API with the smaller of the per-file limit and remaining bundle budget.
Gua returns complete UTF-8 bytes only within that cap; Playtest saves them create-only
and inventories their actual length and SHA-256. Input limits and output limits are
separate: the output cap does not represent a total heap bound.

The receipt inventories manifest, events, snapshots, attachments and report.
RunArtifactStore independently admits every associated file before publishing
the final Result. Success omission requires actual successful Gua completion.
Capture or save failure contributes only postprocessing; immutable primary status
and the existing exit-11 projection are preserved.

Native Gua output is first written into an isolated `.gua-pending-*` directory
under the trusted private artifact root. After genuine native completion, the
summary owner admits the actual bundle bytes together with already charged
run/primary bytes and the exact mandatory completion/result encodings. Only an
admitted bundle is moved into its Run directory. Budget refusal produces failed
artifact receipts while preserving space for the final summaries; independent
receipt validation still reads every published file. Successfully published or
omitted output releases its pending directory. Failed capture or an incomplete
native checkpoint retains isolated diagnostic output and grants no saved receipt.
This staging does not impose a hard deadline on native scheduling or flush.

Native construction failure releases its pending directory. Raw tree/value JSON
is checked for UTF-8 bytes, decoded string lengths, depth and nodes before a DOM
is allocated. Recording admission counts its complete native document, including
targets and scalar fields, before attachment/save.

Gua 1.1.2 limits total event/blob bytes rather than the individual cumulative
`events.jsonl`. Therefore explicit `MaxArtifactBytes` must fit `MaxFileBytes`,
and `MaxFileBytes` must also fit Gua's separate 64 KiB manifest reserve. The
adapter rejects unguaranteed settings before creating a writer. It does not
save an oversized file and then treat later receipt rejection as enforcement
of the effective file ceiling. The manifest, events and mandatory HTML entry
require at least three file slots; the report slot is reserved during the
pre-report inventory rather than added after exhausting the configured count.
Trace and Recording share that file budget. A saved Recording additionally needs
its two native Trace attachments and recording JSON, so at least six slots are required
when both artifacts are saved. Recording slots remain reserved during final Trace
inventory. Encoding admission uses actual bounded Recording/HTML bytes, including
escape expansion, rather than a worst-case character multiplier or report estimate.

Gua 1.1.2 queue admission, writer scheduling, lifecycle stopping and synchronous
filesystem operations do not provide a hard absolute deadline or a proof that
the writer terminated when a flush/dispose timeout was reported. This adapter
awaits the actual public completion operation and does not wrap it with a timeout
that claims to stop background work. The owner must isolate unresolved providers
before reuse; this composition grants no gameplay guard, lifecycle lease,
independent observation feed or production host capability.

`ArtifactReceipt` separates `NotExecuted`, `OmittedOnSuccess`, `CaptureFailed`, `SaveFailed` and `Saved`. Only Passed permits success omission. Non-saved states contain no file/byte/hash fields and create no empty artifact. Capture/flush failures add ArtifactFailed to postprocessing; Passed plus incomplete postprocessing exits 11, while non-Passed primary exits are preserved.

A Saved receipt names a nonempty, bounded, local entry file with byte length and lowercase SHA-256. `AdditionalFiles` enumerates every associated file in a Gua-owned bundle (for example a manifest plus events/attachments), without assuming its format or renaming its contents. Relative names have up to eight safe segments, no URL/drive/traversal components or trailing dots. Trace and Recording cannot alias any associated file. Readback rejects summary replacement, linked paths, length/hash mismatch and Run capacity overflow. Bytes are opaque: tests use opaque fixture bytes and do not claim a Gua format reader or actual Gua integration. The concrete Gua writer must redact before its own queues/buffers/hashes, enforce its total output budget and supply genuine capture/flush evidence including a complete bundle file inventory. A receipt is not proof that an unimplemented upstream writer satisfies this requirement.

The host supplies all resolved secret values and sensitive field names. A single-pass multi-pattern scan suppresses an entire string containing a secret; strings without secrets remain unchanged. This is idempotent and cannot expose overlapping secret tails or form new secrets at deletion boundaries. Sensitive fields/settings become null, preserving the distinction from a supplied empty value. Property names are sanitized and collisions rejected. The bounded sanitized tree is built before UTF-8 storage buffers or hashes. Exception type/stack are sanitized for disk; the original #6 `RunOutcome` remains untouched. Exception messages/inner exceptions and raw Planner text are not collected. #11's `PlannerDecisionReference` maps only identifiers and its structured enum code into `DecisionReference`; pinned Planner schema has no free rationale field. Host metadata/expected criteria are never returned to Planner.

Explicit `ArtifactLimits` bound per-file bytes (256..16 MiB), total associated Run bytes (up to 256 MiB), node/reference count (up to 100000), string chars and depth (32). These hard ceilings are not product defaults. The trusted configuration owner supplies stricter effective Environment limits. Same-directory temporary files use CreateNew, flush-to-disk and create-only rename; normal failures remove pending files. A process interruption can leave pending bytes and absence of final result; no complete-save claim follows. Local filesystem flush is synchronous and is not a hard real-time deadline guarantee. #6 cleanup/adapter isolation must bound provider work; no install or publication is introduced.

## Acceptance evidence and remaining integration

`ArtifactTests` independently parses saved JSON, verifies fixed-copy hash, checks real-data secret suppression independently of trusted structure, validates final result against the existing schema, injects wrong hash/disk target collision/cancellation/byte limit/truncated result, checks observation boundary and primary replacement rejection, and checks all non-saved artifact states and exit 11. The real #6 execution code is composed with local persistence to verify preconnection failure, snapshot-before-release ordering and fresh cancellation authority. A postconfirmation Cancelled marker alone does not manufacture incomplete mandatory work or exit 11. Bundle aliases and actual directory links/junctions are rejected; physical test output directories avoid macOS's `/var` alias without weakening the API. This is local contract evidence for AT-FILE-007, AT-FILE-006, AT-RUN-005/006 and part of AT-TRACE-006/007/009 and AT-PLANNER-009.

Production acceptance requires actual host/Planner/CLI assembly, game lifecycle isolation, bounded provider cancellation and real engine behavior. Native/transport/schema success does not establish those conditions. The report adapter uses the public bounded GuaTraceReport.RenderHtml API from pinned Gua.Testing 1.1.2. Package compatibility checks require the release source identity for both native payloads and the embedded Viewer. No guarded capability follows from the report API.

## Readback, redaction and identity constraints

Trusted summary keys, typed results, generated Run IDs, hashes and timestamps retain their wire values. Actual input strings and scalar secrets are suppressed before sanitized-copy hashing; secret-bearing owner/Planner correlation identifiers fail rather than change identity. Typed ObservationReference and DecisionReference.ObservationId retain actual writer-generated IDs; raw owner observation keys are not persisted. Real JSON fields with the same name remain subject to redaction. Undefined JSON is InvalidEvidence; length mismatch is separate from LimitExceeded; reader cancellation is Interrupted.

JSON property names and scalar raw UTF-8 spans are inspected before decoded copies are allocated. Safe numeric bytes retain their spelling without rounding. JSON punctuation and token boundaries are format structure. Primary enums must be defined before freezing, and status must agree with the RunSession reason mapping; budget reasons permit Failed or Unverified. Preparing cannot publish after primary confirmation or completion.

Redaction policy has hard ceilings of 1024 secret entries, 65536 aggregate UTF-16 secret characters (`PersistenceRedactor.MaxSecretCharacters`), and 1024 sensitive names of at most 256 characters. These are policy ceilings, not effective Environment defaults. The bounded multi-pattern matcher avoids per-value allocations and fixed-point replacement passes. Receipt names are rejected above 1031 characters or seven separators before splitting. Identifiers, filename segments and hashes use bounded ASCII character/length checks; newline/carriage-return suffixes are refused.

Files must be regular files before reading. Unix opens use nonblocking/no-follow flags before checking the handle type, so a FIFO with no writer cannot block admission; Windows requires a disk handle. Missing results are Missing, while access/I/O failures remain Unreadable. The reader preserves ReferenceMissing/ReferenceUnreadable from the independent validator's second open/read instead of treating them as corruption. Public callers cannot substitute that validator, and read races never supply a primary result.

File aliases are checked by the identity of the open handle used for SHA-256. Independent files with identical bytes are permitted; hard links to associated files or storage summaries are refused. The implementation uses installed OS APIs: [Windows FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info), [Linux statx UAPI](https://raw.githubusercontent.com/torvalds/linux/master/include/uapi/linux/stat.h), and [Darwin 64-bit stat ABI](https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/sys/stat.h). Unknown identity/unsupported platforms fail as IoFailure, with no filename fallback.

Case-equivalent Run directory spellings require matching actual directory device/file IDs on every platform, without an OS-name case assumption or parent-directory enumeration. Distinct case-sensitive directories remain invalid even if their result files are hardlinked. Windows metadata-only directory handles use [CreateFileW directory flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) and [FileAttributeTagInfo](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class), with extended drive/UNC names for long roots. Unix reuses nonblocking/no-follow opens and checks directory type on the handle.

### Regression examples

These conditions are encoded in [ArtifactTests](../tests/Gua.Playtest.Foundation.Tests/ArtifactTests.cs); they describe checks, not a successful execution of this revision.

| Initial condition / operation | Expected result |
| --- | --- |
| Redact 100000 `a` characters followed by 100000 `b` characters with secret `ab`, then redact the result again | Bounded processing and idempotent whole-value suppression; no secret-bearing scalar is hashed or published |
| Admit a FIFO receipt with a positive length, then attempt readback with no FIFO writer | Admission reaches the open/type check and rejects the non-regular file without blocking |
| Use FrozenDictionary for version metadata | Saved versions remain a JSON object |
| Supply oversized JSON names, strings or numbers | Bounded allocation and LimitExceeded; valid numeric bytes retain their spelling |
| Supply newline-suffixed identifiers, hashes and reference codes; on Unix, use an actual newline-named file | Invalid names/codes/hashes are refused, even if a file with that spelling exists |
| Admit a genuine saved regular file, then delete it or deny reading before the independent validator's second open | Genuine ReferenceMissing/ReferenceUnreadable remains distinct from corruption; no primary result is inferred |
| Resolve an alternate-case Run directory spelling on the actual test volume | Existing equivalent directory identity verifies; a distinct case-sensitive directory with a hardlinked result is rejected |
| On Windows, create a real output root longer than 260 characters and read its result via an alternate-case spelling | Readback verifies without changing process or machine long-path settings |

## Known flush failure and investigation evidence

The package Trace acceptance test has failed with `flush-timeout` in actual Windows CI. Scheduler probes and failure-only TRX telemetry demonstrate scheduling stagnation, but do not identify each occupied worker or establish the cause of every CI failure. A later green run does not resolve or erase those failures. Queue admission, scheduling and flush deadline behavior still require investigation; native staging supplies no hard deadline guarantee.

The unchanged five-second Trace acceptance assertion records failure-only telemetry, at most 100 samples, from a dedicated thread: elapsed milliseconds, worker count, pending/completed work, writer status and queue count. It records no inputs, credentials, profile data or Planner facts.

Exact historical HEADs, CI run/job IDs, probe timings, observed TRX values and review/recovery records are preserved in the [PR #34 evidence comment](https://github.com/gua-project/gua-playtest/pull/34#issuecomment-6487400072), with its [immutable source](https://github.com/gua-project/gua-playtest/blob/b59a1ac25d3c86e05af9230fe12de7372f43550c/docs/artifacts.md#L137). These are historical measurements, not product defaults or real engine/live Planner acceptance.
