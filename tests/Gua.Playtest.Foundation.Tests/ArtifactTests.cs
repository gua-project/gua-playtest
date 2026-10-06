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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
