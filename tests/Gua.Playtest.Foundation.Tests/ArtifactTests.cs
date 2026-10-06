using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class ArtifactTests : IDisposable
{
    private const string Secret = "SECRET_MARKER_7f933";
    // macOS's global temp path can include /var -> /private/var. The API deliberately rejects
    // aliased roots; use a physical directory in the test output on every OS.
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "playtest-artifact-tests-" + Guid.NewGuid().ToString("N"));
    private static ArtifactLimits Limits => new(65536, 524288, 1000, 16384);
    private RunArtifactStore Store(ArtifactLimits? limits = null) => RunArtifactStore.Create(root, limits ?? Limits,
        new PersistenceRedactor([Secret], ["password"]));
    private static RunArtifactMetadata Metadata => new("public-test",
        [new("limit", "50", "environment"), new("secretKey", "provider/key", "environment"), new("note", Secret, "command")],
        new Dictionary<string, string> { ["gua"] = "1.1.1" },
        [new("scenario", JsonDocument.Parse("{\"password\":\"hidden\",\"note\":\"" + Secret + "\",\"count\":50}").RootElement)]);
    private static PrimaryResult Passed => new(ResultStatus.Passed, new(RunReason.GoalSatisfied, RunPhase.Execution, RunOrigin.Condition));
    private static ArtifactReceipt[] Omitted => [new(ArtifactKind.Trace, ArtifactState.OmittedOnSuccess), new(ArtifactKind.Recording, ArtifactState.NotExecuted)];
    private static RunOutcome Outcome(PrimaryResult? primary = null) => new(primary ?? Passed, [], [], [new("OriginalType", Secret)]);
    private static void Confirm(RunArtifactStore store, PrimaryResult? primary = null) => Assert.True(store.ConfirmPrimary(primary ?? Passed,
        [new(ObservationBoundary.PrimaryDecision, "at-result")], [new("decision-1", "observation-1", "action-1", "Approved")],
        [new("OriginalType", Secret)]).Saved);

    [Fact]
    public async Task Unique_create_only_result_and_independent_schema_readback()
    {
        var store = Store(); var second = Store();
        Assert.NotEqual(store.DirectoryPath, second.DirectoryPath);
        Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        var complete = store.Complete(Outcome(), Omitted, [new(ObservationBoundary.AfterCleanup, "after-release")], DateTimeOffset.UtcNow);
        Assert.True(complete.Saved); Assert.Equal(0, complete.ExitCode(Outcome()));
        var reader = await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits);
        Assert.Equal(ResultReadState.Verified, reader.State); Assert.Equal(ResultStatus.Passed, reader.Result!.Status);
        var bytes = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "result.json"));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(store.DirectoryPath, "result.json")));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.ConfirmPrimary(new(ResultStatus.Aborted,
            new(RunReason.Cancelled, RunPhase.Execution, RunOrigin.User)), [], [], []).Failure);
    }
    [Fact]
    public async Task Missing_or_partial_result_does_not_infer_pass_from_primary_or_last_observation()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        File.WriteAllText(Path.Combine(store.DirectoryPath, ".pending-interrupted"), "{\"status\":\"Passed\"}");
        Assert.Equal(ResultReadState.Missing, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        File.WriteAllText(Path.Combine(store.DirectoryPath, "result.json"), "{\"status\":");
        Assert.Equal(ResultReadState.Invalid, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
    }
    [Fact]
    public void Secret_redaction_precedes_fixed_copy_hash_and_preserves_original_exception()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        var outcome = Outcome(); Assert.True(store.Complete(outcome, Omitted, [], DateTimeOffset.UtcNow).Saved);
        foreach (var file in Directory.GetFiles(store.DirectoryPath)) Assert.DoesNotContain(Secret, File.ReadAllText(file));
        using var run = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "run.json")));
        var copy = run.RootElement.GetProperty("inputs")[0];
        var fixedBytes = Encoding.UTF8.GetBytes(copy.GetProperty("document").GetRawText());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixedBytes)), copy.GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("document").GetProperty("password").ValueKind);
        Assert.Equal(50, copy.GetProperty("document").GetProperty("count").GetInt32());
        Assert.Equal(Secret, outcome.Exceptions[0].StackTrace);
    }
    [Theory]
    [InlineData(ArtifactState.NotExecuted, false)]
    [InlineData(ArtifactState.OmittedOnSuccess, false)]
    [InlineData(ArtifactState.CaptureFailed, true)]
    [InlineData(ArtifactState.SaveFailed, true)]
    public async Task Distinct_capture_states_and_postprocessing_exit11(ArtifactState state, bool incomplete)
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        var result = store.Complete(Outcome(), [new(ArtifactKind.Trace, state), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow);
        Assert.True(result.Saved); Assert.Equal(incomplete ? 11 : 0, result.ExitCode(Outcome()));
        var read = await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits);
        Assert.Equal(!incomplete, read.Result!.PostProcessing.Complete);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "completion.json")));
        Assert.Equal(state.ToString(), json.RootElement.GetProperty("artifacts")[0].GetProperty("state").GetString());
        foreach (var artifact in json.RootElement.GetProperty("artifacts").EnumerateArray())
            foreach (var optional in new[] { "fileName", "bytes", "sha256", "additionalFiles" })
                Assert.False(artifact.TryGetProperty(optional, out _));
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "trace.json")));
    }
    [Fact]
    public void Artifact_hash_readback_and_bad_receipt_preserve_failure_code()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved);
        var failed = new PrimaryResult(ResultStatus.Failed, new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host)); Confirm(store, failed);
        File.WriteAllText(Path.Combine(store.DirectoryPath, "trace.gua"), "actual-gua-owned-fixture");
        var bytes = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "trace.gua"));
        var result = store.Complete(Outcome(failed), [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length,
            new string('0', 64)), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow);
        Assert.Equal(PersistenceFailure.InvalidEvidence, result.Failure); Assert.Equal(1, result.ExitCode(Outcome(failed)));
        Assert.Equal(failed, Outcome(failed).Primary);
    }
    [Fact]
    public async Task Valid_saved_artifact_is_linked_without_inventing_trace_format()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        var bytes = Encoding.UTF8.GetBytes("opaque-existing-Gua-format");
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "trace.gua"), bytes);
        Assert.True(store.Complete(Outcome(), [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes))), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
    }
    [Fact]
    public void Bounds_cancellation_disk_failure_and_boundary_errors_are_classified()
    {
        var tiny = Store(new(256, 256, 1000, 256));
        Assert.Equal(PersistenceFailure.LimitExceeded, tiny.BeginPreparation(Metadata).Failure);
        Assert.Empty(Directory.GetFiles(tiny.DirectoryPath, ".pending-*"));
        var cancelled = Store();
        Assert.Equal(PersistenceFailure.Cancelled, cancelled.BeginPreparation(Metadata, new CancellationToken(true)).Failure);
        var blocked = Store(); Directory.CreateDirectory(Path.Combine(blocked.DirectoryPath, "run.json"));
        Assert.Equal(PersistenceFailure.IoFailure, blocked.BeginPreparation(Metadata).Failure);
        Assert.Empty(Directory.GetFiles(blocked.DirectoryPath, ".pending-*"));
        var boundary = Store();
        Assert.Equal(PersistenceFailure.InvalidEvidence, boundary.ConfirmPrimary(Passed, [new(ObservationBoundary.AfterCleanup, "late")], [], []).Failure);
    }
    [Fact]
    public void Traversal_empty_artifact_omission_for_failure_and_result_replacement_are_rejected()
    {
        foreach (var name in new[] { "../trace.json", "result.json", "https://example.com/trace.json" })
        {
            var store = Store(); Confirm(store);
            Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(),
                [new(ArtifactKind.Trace, ArtifactState.Saved, name, 1, new string('a', 64)), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Failure);
        }
        var failed = new PrimaryResult(ResultStatus.Aborted, new(RunReason.Cancelled, RunPhase.Preparation, RunOrigin.User));
        var aborted = Store(); Confirm(aborted, failed);
        Assert.Equal(PersistenceFailure.InvalidEvidence, aborted.Complete(Outcome(failed), Omitted, [], DateTimeOffset.UtcNow).Failure);
    }
    [Fact]
    public async Task Bundle_files_are_bounded_read_back_and_cannot_alias_across_artifact_kinds()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "trace"));
        var bytes = Encoding.UTF8.GetBytes("Gua-owned-bundle-fixture"); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "trace", "manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "trace", "events.jsonl"), bytes);
        Assert.True(store.Complete(Outcome(), [new(ArtifactKind.Trace, ArtifactState.Saved, "trace/manifest.json", bytes.Length, hash,
            [new("trace/events.jsonl", bytes.Length, hash)]), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath + Path.DirectorySeparatorChar, Limits)).State);
        var alias = Store(); Confirm(alias); File.WriteAllBytes(Path.Combine(alias.DirectoryPath, "trace.gua"), bytes);
        Assert.Equal(PersistenceFailure.InvalidEvidence, alias.Complete(Outcome(),
            [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length, hash), new(ArtifactKind.Recording, ArtifactState.Saved, "trace.gua", bytes.Length, hash)], [], DateTimeOffset.UtcNow).Failure);
    }
    [Fact]
    public void Linked_root_is_rejected_before_child_creation_and_linked_artifact_is_rejected()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "link");
        CreateDirectoryLink(link, target);
        Assert.Throws<InvalidDataException>(() => RunArtifactStore.Create(Path.Combine(link, "must-not-create"), Limits, new([], [])));
        Assert.False(Directory.Exists(Path.Combine(target, "must-not-create")));
        var store = Store(); Confirm(store);
        var source = Path.Combine(target, "source.gua"); File.WriteAllText(source, "fixture");
        CreateDirectoryLink(Path.Combine(store.DirectoryPath, "linked"), target);
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(),
            [new(ArtifactKind.Trace, ArtifactState.Saved, "linked/source.gua", 7, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture")))),
            new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Failure);
        // Remove links without traversing targets; test cleanup owns only this temporary tree.
        Directory.Delete(Path.Combine(store.DirectoryPath, "linked")); Directory.Delete(link);
    }
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // Directory junctions test the same reparse-point boundary without elevation or Developer Mode.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
    }
    [Fact]
    public void Prior_metadata_failure_remains_postprocessing_even_when_final_write_succeeds()
    {
        var store = Store();
        Assert.Equal(PersistenceFailure.Cancelled, store.BeginPreparation(Metadata, new CancellationToken(true)).Failure);
        Confirm(store);
        var result = store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow);
        Assert.True(result.Saved); Assert.Equal(11, result.ExitCode(Outcome()));
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(store.DirectoryPath, "primary.json")));
    }
    [Fact]
    public async Task Runner_snapshot_persists_preconnection_failure_before_owned_release_and_retains_original_exception()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved);
        var clock = new ImmediateWorkClock();
        var run = new RunSession(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, 3, 2, 32), clock, clock);
        var released = false;
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (cleanup, _) =>
        {
            cleanup.Register(CleanupStage.ResourceRelease, _ =>
            { Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "primary.json"))); released = true; return ValueTask.FromResult(true); });
            throw new IOException(Secret);
        }, (_, _) => throw new InvalidOperationException("MustNotExecute"),
        confirmPrimary: (snapshot, token) => ValueTask.FromResult(store.ConfirmPrimary(snapshot, [], [], token).Saved));
        Assert.True(released); Assert.Equal(RunPhase.Preparation, outcome.Primary.Cause.Phase);
        Assert.Equal("System.IO.IOException", outcome.Exceptions[0].Type); Assert.NotNull(outcome.Exceptions[0].StackTrace);
        var result = store.Complete(outcome, [new(ArtifactKind.Trace, ArtifactState.CaptureFailed), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow);
        Assert.True(result.Saved); Assert.Equal(10, result.ExitCode(outcome));
        foreach (var file in Directory.GetFiles(store.DirectoryPath)) Assert.DoesNotContain(Secret, File.ReadAllText(file));
    }
    [Fact]
    public async Task Runner_snapshot_uses_fresh_token_after_cancellation_and_cancel_marker_does_not_change_complete_exit()
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved);
        var clock = new ImmediateWorkClock();
        var run = new RunSession(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, 3, 2, 32), clock, clock);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (_, _) => ValueTask.FromResult(true),
            (_, _) => ValueTask.FromResult(true), new CancellationToken(true), (snapshot, token) =>
            { Assert.False(token.IsCancellationRequested); return ValueTask.FromResult(store.ConfirmPrimary(snapshot, [], [], token).Saved); });
        Assert.Equal(ResultStatus.Aborted, outcome.Primary.Status); Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        var passed = Outcome() with { PostProcessing = [new(PostProcessingReason.Cancelled)] };
        var confirmed = Store(); Confirm(confirmed);
        var result = confirmed.Complete(passed, Omitted, [], DateTimeOffset.UtcNow);
        Assert.True(result.Saved); Assert.Equal(0, result.ExitCode(passed));
        Assert.True((await RunArtifactReader.ReadResultAsync(confirmed.DirectoryPath, Limits)).Result!.PostProcessing.Complete);
    }
    private sealed class ImmediateWorkClock : Gua.Playtest.Core.IClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => new(Task.Delay(Timeout.Infinite, cancellationToken));
    }
    [Theory]
    [InlineData("a")]
    [InlineData("Passed")]
    [InlineData("runId")]
    public void Structural_secret_collisions_fail_without_publishing_corrupt_wire_fields(string secret)
    {
        var store = RunArtifactStore.Create(root, Limits, new([secret], []));
        var result = store.ConfirmPrimary(Passed, [], [], []);
        Assert.Equal(PersistenceFailure.InvalidEvidence, result.Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, ".pending-*"));
    }
    [Fact]
    public void Aggregate_input_copies_and_undefined_documents_are_bounded_classified_failures()
    {
        var store = Store(new(4096, 65536, 1000, 4000));
        var document = JsonDocument.Parse("{\"text\":\"" + new string('x', 3000) + "\"}").RootElement;
        var metadata = new RunArtifactMetadata("public", [], new Dictionary<string, string>(), [new("first", document), new("second", document)]);
        Assert.Equal(PersistenceFailure.LimitExceeded, store.BeginPreparation(metadata).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
        var invalid = Store();
        Assert.Equal(PersistenceFailure.InvalidEvidence, invalid.BeginPreparation(metadata with { Inputs = [new("undefined", default)] }).Failure);
    }
    [Fact]
    public async Task Receipt_length_mismatch_and_read_cancellation_have_distinct_categories()
    {
        var store = Store(); Confirm(store);
        var bytes = Encoding.UTF8.GetBytes("fixture"); File.WriteAllBytes(Path.Combine(store.DirectoryPath, "trace.gua"), bytes);
        var mismatch = store.Complete(Outcome(), [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length - 1,
            Convert.ToHexStringLower(SHA256.HashData(bytes))), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow);
        Assert.Equal(PersistenceFailure.InvalidEvidence, mismatch.Failure);
        var saved = Store(); Confirm(saved); Assert.True(saved.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        Assert.Equal(ResultReadState.Interrupted, (await RunArtifactReader.ReadResultAsync(saved.DirectoryPath, Limits, new CancellationToken(true))).State);
    }
    [Fact]
    public void Physical_hardlink_alias_is_rejected_but_independent_identical_bytes_are_valid()
    {
        var bytes = Encoding.UTF8.GetBytes("identical-opaque-fixture"); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var store = Store(); Confirm(store);
        var trace = Path.Combine(store.DirectoryPath, "trace.gua"); var recording = Path.Combine(store.DirectoryPath, "recording.gua");
        File.WriteAllBytes(trace, bytes);
        CreateHardLink(recording, trace);
        var receipts = new[] { new ArtifactReceipt(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length, hash),
            new ArtifactReceipt(ArtifactKind.Recording, ArtifactState.Saved, "recording.gua", bytes.Length, hash) };
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(), receipts, [], DateTimeOffset.UtcNow).Failure);
        var separate = Store(); Confirm(separate);
        File.WriteAllBytes(Path.Combine(separate.DirectoryPath, "trace.gua"), bytes); File.WriteAllBytes(Path.Combine(separate.DirectoryPath, "recording.gua"), bytes);
        Assert.True(separate.Complete(Outcome(), receipts, [], DateTimeOffset.UtcNow).Saved);
    }
    private static void CreateHardLink(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/ln")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = OperatingSystem.IsWindows() ? new[] { "/c", "mklink", "/H", link, target } : new[] { target, link };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
    }
    [Theory]
    [InlineData("observation-1")]
    [InlineData("decision-1")]
    [InlineData("action-1")]
    [InlineData("Approved")]
    public void Reference_secret_collisions_fail_without_persisting_changed_identifiers(string secret)
    {
        var store = RunArtifactStore.Create(root, Limits, new([secret], []));
        var result = store.ConfirmPrimary(Passed, [new(ObservationBoundary.PrimaryDecision, "observation-1")],
            [new("decision-1", "observation-1", "action-1", "Approved")], []);
        Assert.Equal(PersistenceFailure.InvalidEvidence, result.Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, ".pending-*"));
    }
    [Fact]
    public async Task Nonregular_artifact_and_result_are_rejected_before_blocking_read()
    {
        if (OperatingSystem.IsWindows()) return; // FIFO fixture exercises installed Unix open/stat ABI on CI.
        var store = Store(); Confirm(store);
        void Fifo(string path)
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/mkfifo") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
        }
        Fifo(Path.Combine(store.DirectoryPath, "trace.gua"));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(),
            [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", 1, Convert.ToHexStringLower(SHA256.HashData([1]))),
             new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Failure);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Fifo(Path.Combine(store.DirectoryPath, "result.json")); elapsed.Restart();
        Assert.Equal(ResultReadState.Invalid, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
    }
    [Fact]
    public void Readonly_versions_preserve_object_wire_shape_and_scalars_are_bounded_before_copy()
    {
        var store = Store();
        var versions = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, string> { ["gua"] = "1.1.1" });
        Assert.True(store.BeginPreparation(Metadata with { Versions = versions }).Saved);
        using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "run.json")));
        Assert.Equal(JsonValueKind.Object, saved.RootElement.GetProperty("versions").ValueKind);
        Assert.Equal("1.1.1", saved.RootElement.GetProperty("versions").GetProperty("gua").GetString());
        foreach (var text in new[] { "\"" + new string('x', 100000) + "\"", "1" + new string('0', 100000), "{\"" + new string('x', 100000) + "\":true}" })
        {
            using var source = JsonDocument.Parse(text);
            var bounded = Store(new(4096, 65536, 1000, 32));
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(PersistenceFailure.LimitExceeded, bounded.BeginPreparation(Metadata with
                { Inputs = [new("oversize", source.RootElement)] }).Failure);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
            Assert.False(File.Exists(Path.Combine(bounded.DirectoryPath, "run.json")));
        }
    }
    [Fact]
    public async Task Result_access_failure_is_unreadable_and_dangling_link_is_invalid()
    {
        var store = Store(); Confirm(store); Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        var file = Path.Combine(store.DirectoryPath, "result.json");
        if (OperatingSystem.IsWindows())
        {
            using var blocked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(ResultReadState.Unreadable, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
            return; // Creating a Windows file symlink needs privileges; real dangling links run on Unix CI.
        }
        var permissions = File.GetUnixFileMode(store.DirectoryPath);
        try
        {
            File.SetUnixFileMode(store.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(ResultReadState.Unreadable, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        }
        finally { File.SetUnixFileMode(store.DirectoryPath, permissions); }
        File.Delete(file); File.CreateSymbolicLink(file, Path.Combine(store.DirectoryPath, "missing-target"));
        Assert.Equal(ResultReadState.Invalid, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        File.Delete(file);
    }
    [Fact]
    public void Redaction_suppresses_containing_values_without_repeated_allocations_or_exposed_tails()
    {
        var redactor = new PersistenceRedactor(["ab", "abc", "bc", "\uD83D\uDD11secret"], []);
        foreach (var text in new[] { "prefix abc suffix", "abc", "cab", "safe \uD83D\uDD11secret data" })
        { Assert.Equal("", redactor.Redact(text)); Assert.Equal("", redactor.Redact(redactor.Redact(text))); }
        Assert.Equal("unchanged", redactor.Redact("unchanged"));
        var adversarial = new string('a', 100000) + new string('b', 100000);
        var watch = System.Diagnostics.Stopwatch.StartNew(); var before = GC.GetAllocatedBytesForCurrentThread();
        var sanitized = redactor.Redact(adversarial);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("", sanitized); Assert.InRange(allocated, 0, 32768); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Throws<ArgumentException>(() => new PersistenceRedactor([new string('x', PersistenceRedactor.MaxSecretCharacters), "y"], []));
    }
    [Fact]
    public async Task Result_identity_accepts_equivalent_Windows_path_casing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store(); Confirm(store); Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        var alternate = Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!, Path.GetFileName(store.DirectoryPath).ToUpperInvariant());
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(alternate, Limits)).State);
    }
    [Fact]
    public async Task Result_identity_uses_actual_volume_case_lookup_and_rejects_distinct_directories_with_hardlinked_results()
    {
        RunArtifactStore? store = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Store();
            if (candidate.RunId != candidate.RunId.ToUpperInvariant()) { store = candidate; break; }
        }
        Assert.NotNull(store); Confirm(store); Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        var alternate = Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!, store.RunId.ToUpperInvariant());
        Assert.NotEqual(store.DirectoryPath, alternate);
        if (Directory.Exists(alternate))
        {
            // Real case-insensitive volume, including default macOS volumes: no OS-name assumption.
            Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(alternate, Limits)).State);
        }
        else
        {
            Directory.CreateDirectory(alternate);
            CreateHardLink(Path.Combine(alternate, "result.json"), Path.Combine(store.DirectoryPath, "result.json"));
            // Prove real directory metadata admission succeeds; a generic type rejection must not
            // make this identity regression vacuously pass on Unix.
            Assert.NotEqual(FileIdentity.ReadDirectory(store.DirectoryPath), FileIdentity.ReadDirectory(alternate));
            Assert.Equal(ResultReadState.Invalid, (await RunArtifactReader.ReadResultAsync(alternate, Limits)).State);
        }
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
    }
    [Fact]
    public async Task Directory_identity_case_lookup_preserves_long_Windows_output_paths()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longRoot = Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 100));
        RunArtifactStore? store = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = RunArtifactStore.Create(longRoot, Limits, new([Secret], []));
            if (candidate.RunId != candidate.RunId.ToUpperInvariant()) { store = candidate; break; }
        }
        Assert.NotNull(store); Assert.True(store.DirectoryPath.Length > 260);
        Confirm(store); Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        var alternate = Path.Combine(longRoot, store.RunId.ToUpperInvariant());
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(alternate, Limits)).State);
    }
    [Fact]
    public void Oversized_receipt_names_fail_before_proportional_split_allocations()
    {
        foreach (var name in new[] { new string('x', 100000), new string('/', 100000), "a/a/a/a/a/a/a/a/a", "trace.gua" })
        {
            var store = Store(); Confirm(store);
            var reference = new ArtifactReceipt(ArtifactKind.Trace, ArtifactState.Saved, name, 1, new string('0', 64));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = store.Complete(Outcome(), [reference, new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(name == "trace.gua" ? PersistenceFailure.IoFailure : PersistenceFailure.InvalidEvidence, result.Failure);
            Assert.InRange(allocated, 0, 32768);
        }
    }
    [Theory]
    [InlineData("observation")]
    [InlineData("decision")]
    [InlineData("basedOn")]
    [InlineData("action")]
    [InlineData("reason")]
    public void Newline_reference_tokens_are_invalid_before_snapshot_publication(string field)
    {
        var store = Store();
        var decision = new DecisionReference(field == "decision" ? "decision\n" : "decision",
            field == "basedOn" ? "observation\n" : "observation", field == "action" ? "action\n" : "action",
            field == "reason" ? "Approved\n" : "Approved");
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.ConfirmPrimary(Passed,
            [new(ObservationBoundary.PrimaryDecision, field == "observation" ? "observation\n" : "observation")], [decision], []).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Newline_receipt_names_and_hashes_are_rejected_even_when_source_file_exists(bool name)
    {
        var store = Store(); Confirm(store); byte[] bytes = [1];
        var fileName = name ? "trace.gua\n" : "trace.gua";
        if (!OperatingSystem.IsWindows() || !name)
        {
            File.WriteAllBytes(Path.Combine(store.DirectoryPath, fileName), bytes);
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, fileName)));
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) + (name ? "" : "\n");
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(),
            [new(ArtifactKind.Trace, ArtifactState.Saved, fileName, 1, hash), new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "completion.json")));
    }
    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("1234567890123456789", "1234567890123456789")]
    [InlineData("1.234567890123456789e-123", "e-123")]
    [InlineData("null", "null")]
    public void Scalar_secret_collisions_fail_before_any_input_hash_or_publication(string token, string secret)
    {
        using var source = JsonDocument.Parse("{\"credential\":[" + token + "]}");
        var original = source.RootElement.GetRawText();
        var store = RunArtifactStore.Create(root, Limits, new([secret], []));
        var metadata = new RunArtifactMetadata("public", [], new Dictionary<string, string>(), [new("scenario", source.RootElement)]);
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.BeginPreparation(metadata).Failure);
        Assert.Equal(original, source.RootElement.GetRawText());
        Assert.Equal([".owner"], Directory.GetFiles(store.DirectoryPath).Select(x => Path.GetFileName(x)!).ToArray());
    }
    [Fact]
    public void Safe_scalar_raw_values_are_preserved_and_generated_timestamp_collisions_fail()
    {
        using var source = JsonDocument.Parse("{\"number\":1e-3,\"boolean\":false,\"empty\":null}");
        var store = Store();
        Assert.True(store.BeginPreparation(new("public", [], new Dictionary<string, string>(), [new("scenario", source.RootElement)])).Saved);
        using var readback = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "run.json")));
        var copy = readback.RootElement.GetProperty("inputs")[0].GetProperty("document");
        Assert.Equal("1e-3", copy.GetProperty("number").GetRawText());
        Assert.False(copy.GetProperty("boolean").GetBoolean()); Assert.Equal(JsonValueKind.Null, copy.GetProperty("empty").ValueKind);
        var timestamp = RunArtifactStore.Create(root, Limits, new(["2097-01"], [])); Confirm(timestamp);
        Assert.Equal(PersistenceFailure.InvalidEvidence, timestamp.Complete(Outcome(), Omitted, [], new(2097, 1, 2, 3, 4, 5, TimeSpan.Zero)).Failure);
        Assert.False(File.Exists(Path.Combine(timestamp.DirectoryPath, "result.json")));
        foreach (var file in Directory.GetFiles(timestamp.DirectoryPath)) Assert.DoesNotContain("2097-01", File.ReadAllText(file));
    }
    [Theory]
    [InlineData("status")]
    [InlineData("reason")]
    [InlineData("phase")]
    [InlineData("origin")]
    [InlineData("cause")]
    public void Invalid_primary_enums_are_rejected_before_freezing_the_snapshot(string field)
    {
        var store = Store();
        var invalid = new PrimaryResult(field == "status" ? (ResultStatus)999 : Passed.Status,
            field == "cause" ? null! : new(field == "reason" ? (RunReason)999 : Passed.Cause.Reason,
                field == "phase" ? (RunPhase)999 : Passed.Cause.Phase, field == "origin" ? (RunOrigin)999 : Passed.Cause.Origin));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.ConfirmPrimary(invalid, [], [], []).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        Confirm(store); // Invalid evidence must not freeze an invalid primary in memory.
        Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preparing_cannot_be_published_after_primary_or_completion(bool complete)
    {
        var store = Store(); Confirm(store);
        if (complete) Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        var before = Directory.GetFiles(store.DirectoryPath).ToDictionary(x => Path.GetFileName(x)!, File.ReadAllBytes);
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.BeginPreparation(Metadata).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
        Assert.Equal(before.Count, Directory.GetFiles(store.DirectoryPath).Length);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(store.DirectoryPath, file.Key!)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Filesystem_failure_between_admission_and_validator_read_preserves_classification(bool missing)
    {
        var store = Store(); Confirm(store); Assert.True(store.Complete(Outcome(), Omitted, [], DateTimeOffset.UtcNow).Saved);
        string? observedCode = null;
        var readback = await RunArtifactReader.ReadWithValidatorAsync(store.DirectoryPath, Limits, async (allowedRoot, file, token) =>
        {
            Assert.True(File.Exists(file)); // Reader already admitted this real regular file.
            Assert.True(new FileInfo(file).Length > 0);
            var validator = new StaticContractValidator([allowedRoot]);
            StaticValidationReport report;
            if (missing)
            {
                File.Delete(file);
                report = await validator.ValidateFileAsync(file, token);
            }
            else if (OperatingSystem.IsWindows())
            {
                using var blocked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                report = await validator.ValidateFileAsync(file, token);
            }
            else
            {
                var permissions = File.GetUnixFileMode(file);
                try { File.SetUnixFileMode(file, 0); report = await validator.ValidateFileAsync(file, token); }
                finally { File.SetUnixFileMode(file, permissions); }
            }
            observedCode = report.Code;
            return report; // Genuine filesystem report from the production validator, no invented report.
        });
        Assert.Equal(missing ? "ReferenceMissing" : "ReferenceUnreadable", observedCode);
        Assert.Equal(missing ? ResultReadState.Missing : ResultReadState.Unreadable, readback.State);
        Assert.Null(readback.Result);
    }
    [Theory]
    [InlineData("[true,false]", "true,false")]
    [InlineData("[\"a\",\"b\"]", "a\",\"b")]
    [InlineData("[\"é\",\"b\"]", "\\u00E9\",\"b")]
    public void Encoded_token_boundary_secrets_are_rejected_before_fixed_copy_hash_or_publication(string input, string secret)
    {
        using var source = JsonDocument.Parse(input);
        var store = RunArtifactStore.Create(root, Limits, new([secret], []));
        Assert.Equal(PersistenceFailure.InvalidEvidence,
            store.BeginPreparation(new("public", [], new Dictionary<string, string>(), [new("scenario", source.RootElement)])).Failure);
        Assert.Equal(input, source.RootElement.GetRawText());
        Assert.Equal([".owner"], Directory.GetFiles(store.DirectoryPath).Select(x => Path.GetFileName(x)!).ToArray());
    }
    [Fact]
    public void Final_encoding_checks_generated_boundaries_and_utf8_matches_without_allocating_decoded_buffers()
    {
        var store = RunArtifactStore.Create(root, Limits, new(["GoalSatisfied\",\"phase"], []));
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.ConfirmPrimary(Passed, [], [], []).Failure);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
        var redactor = new PersistenceRedactor(["🔑secret"], []);
        var bytes = Encoding.UTF8.GetBytes(new string('x', 100000) + "🔑secret");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(redactor.ContainsUtf8Secret(bytes));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
        Assert.False(redactor.ContainsUtf8Secret(Encoding.UTF8.GetBytes("unchanged 🔑safe")));
    }
    [Theory]
    [InlineData("run.json")]
    [InlineData("primary.json")]
    public void Summary_hardlink_cannot_be_admitted_as_an_opaque_gua_receipt(string summary)
    {
        var store = Store(); Assert.True(store.BeginPreparation(Metadata).Saved); Confirm(store);
        var source = Path.Combine(store.DirectoryPath, summary); var bytes = File.ReadAllBytes(source);
        CreateHardLink(Path.Combine(store.DirectoryPath, "trace.gua"), source);
        Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(),
            [new(ArtifactKind.Trace, ArtifactState.Saved, "trace.gua", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
             new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "completion.json")));
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "result.json")));
    }
    [Theory]
    [InlineData(ResultStatus.Passed, RunReason.ActionFailed, false)]
    [InlineData(ResultStatus.Passed, RunReason.Cancelled, false)]
    [InlineData(ResultStatus.Passed, RunReason.ExplorationFinished, false)]
    [InlineData(ResultStatus.Failed, RunReason.GoalSatisfied, false)]
    [InlineData(ResultStatus.Failed, RunReason.Cancelled, false)]
    [InlineData(ResultStatus.Invalid, RunReason.MaxDuration, false)]
    [InlineData(ResultStatus.Unverified, RunReason.PreparationTimeout, false)]
    [InlineData(ResultStatus.Failed, RunReason.ActionsExhausted, true)]
    [InlineData(ResultStatus.Unverified, RunReason.ActionsExhausted, true)]
    [InlineData(ResultStatus.Failed, RunReason.DecisionsExhausted, true)]
    [InlineData(ResultStatus.Unverified, RunReason.DecisionsExhausted, true)]
    [InlineData(ResultStatus.Failed, RunReason.RecoveryExhausted, true)]
    [InlineData(ResultStatus.Unverified, RunReason.RecoveryExhausted, true)]
    public async Task Primary_cause_cannot_be_relabelled_success_and_both_budget_condition_modes_remain_valid(
        ResultStatus status, RunReason reason, bool valid)
    {
        var store = Store(); var primary = new PrimaryResult(status, new(reason, RunPhase.Execution, RunOrigin.Budget));
        var confirmation = store.ConfirmPrimary(primary, [], [], []);
        if (!valid)
        {
            Assert.Equal(PersistenceFailure.InvalidEvidence, confirmation.Failure);
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "primary.json")));
            Assert.Equal(PersistenceFailure.InvalidEvidence, store.Complete(Outcome(primary), Omitted, [], DateTimeOffset.UtcNow).Failure);
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "result.json")));
            return;
        }
        Assert.True(confirmation.Saved);
        Assert.True(store.Complete(Outcome(primary), [new(ArtifactKind.Trace, ArtifactState.NotExecuted),
            new(ArtifactKind.Recording, ArtifactState.NotExecuted)], [], DateTimeOffset.UtcNow).Saved);
        var readback = await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits);
        Assert.Equal(ResultReadState.Verified, readback.State); Assert.Equal(status, readback.Result!.Status);
        Assert.Equal(reason.ToString(), readback.Result.Reason);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
