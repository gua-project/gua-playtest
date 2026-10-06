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
    private readonly string root = Path.Combine(Path.GetTempPath(), "playtest-artifact-tests-" + Guid.NewGuid().ToString("N"));
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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
