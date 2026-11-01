using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Playtest.Cli;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Dashboard;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;
using Gua.Playtest.Runner.Preparation;
using Gua.Testing;
using Gua.Testing.Recording;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class GuaRunArtifactsTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "real-gua-artifacts-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "resolved-secret-marker";
    private static ArtifactLimits Limits => new(1024 * 1024, 8 * 1024 * 1024, 1000, 65536);
    private static PersistenceRedactor Redactor => new([Secret], ["credential"]);
    private RunArtifactStore Store() => RunArtifactStore.Create(root, Limits, Redactor);
    private static GuaTraceOptions Options(GuaTraceCaptureMode mode = GuaTraceCaptureMode.Recent,
        GuaTraceSavePolicy policy = GuaTraceSavePolicy.Always, int attachmentBytes = 32768,
        int artifactBytes = 1024 * 1024, int eventBytes = 32768) => new()
    {
        CaptureMode = mode, SavePolicy = policy, Profile = "player", Secrets = [Secret], MaxSteps = 100,
        MaxMemoryBytes = 1024 * 1024, MaxQueueItems = 256, MaxArtifactBytes = artifactBytes,
        MaxAttachmentBytes = attachmentBytes, MaxEventBytes = eventBytes, FlushTimeout = TimeSpan.FromSeconds(5)
    };
    private static RunArtifactMetadata Metadata => new("player", [new("token", Secret, "environment")],
        new Dictionary<string, string> { ["Gua.Testing"] = "1.1.1" },
        [new("environment", JsonSerializer.SerializeToElement(new { credential = Secret }))]);
    private static PrimaryResult Primary(bool passed) => passed
        ? new(ResultStatus.Passed, new(RunReason.GoalSatisfied, RunPhase.Execution, RunOrigin.Condition))
        : new(ResultStatus.Failed, new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host));
    private static GuaRecording Recording(string? value = null) => new(1,
        [new(value is null ? GuaRecordedAction.click : GuaRecordedAction.set_value, 0, 1, 2, false,
            RequestId: 17, Target: new(Id: "buy"), Value: value)]);
    private static RunOutcome Outcome(PrimaryResult primary) => new(primary, [], [], [new("OriginalProvider", Secret)]);
    private static Task Confirm(GuaRunArtifacts capture, PrimaryResult primary) =>
        ConfirmCore(capture, new(primary, [], [new("OriginalProvider", Secret)]));
    private static async Task ConfirmCore(GuaRunArtifacts capture, RunSnapshot snapshot)
        => Assert.True(await capture.ConfirmPrimaryAsync(snapshot, CancellationToken.None));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Writer_generated_observation_references_survive_real_data_redaction(bool literal)
    {
        var redactor = new PersistenceRedactor(literal ? ["a"] : [], ["observationId"]);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.Mark("step");
        var id = capture.Observe("private-owner-key", ObservationBoundary.Before, step, "ui", "before", "available", new("host", "1"),
            JsonSerializer.SerializeToElement(new { observationId = "private-input-marker" }));
        Assert.NotEmpty(id);
        Assert.True(capture.RecordPlannerDecision(step, new(capture.RunId, "d1", "private-owner-key", PlannerFeedbackCode.Approved)));
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var original = new RunOutcome(primary, [], [], []);
        var result = await capture.CompleteAsync(original, DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, result.Receipts[0].State); Assert.Equal(0, result.ExitCode(original));
        using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "primary.json")));
        Assert.Equal(id, saved.RootElement.GetProperty("observations")[0].GetProperty("observationId").GetString());
        Assert.Equal(id, saved.RootElement.GetProperty("decisions")[0].GetProperty("observationId").GetString());
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        foreach (var file in Directory.GetFiles(Path.Combine(capture.Trace.ArtifactPath, "snapshots")))
        {
            using var snapshot = JsonDocument.Parse(File.ReadAllBytes(file));
            Assert.Equal(JsonValueKind.Null, Assert.Single(snapshot.RootElement.EnumerateObject()).Value.ValueKind);
        }
    }

    [Theory]
    [InlineData(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.Always, true)]
    [InlineData(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.Always, false)]
    [InlineData(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.OnFailure, true)]
    [InlineData(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.OnFailure, false)]
    [InlineData(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.Always, true)]
    [InlineData(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.Always, false)]
    [InlineData(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.OnFailure, true)]
    [InlineData(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.OnFailure, false)]
    public async Task Real_writer_reader_bundles_references_and_save_policies(GuaTraceCaptureMode mode, GuaTraceSavePolicy policy, bool passed)
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(mode, policy), Redactor);
        Assert.True(capture.PreparationPersistence.Saved);
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
        IPreparationTrace preparation = capture;
        preparation.Record(new(PreparationStage.Launch, PreparationCode.LaunchFailed));
        var request = new GuaTraceRequest("host-source", "1", "17");
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Action, "purchase " + Secret, request);
        Assert.True(capture.Trace.RecordRequest(request, "enqueue", JsonSerializer.SerializeToElement(new { accepted = true })));
        var before = capture.Observe("owner-before", ObservationBoundary.Before, step, "ui", "before", "available",
            new("host-source", "1", Revision: "1"), JsonSerializer.SerializeToElement(new { name = Secret }));
        using var one = GuaValue.Integer(1); using var two = GuaValue.Integer(2);
        Assert.True(capture.Trace.Change(step, "hp", "changed", new("host-source", "1", Revision: "2"), one, two, "continuous"));
        Assert.True(capture.Trace.Evaluate(step, "hp", "equal", two, two, "true", "success", [before], GuaTraceOutcome.Passed));
        Assert.True(capture.RecordPlannerDecision(step, new(capture.RunId, "decision-1", "owner-before", PlannerFeedbackCode.Approved), "purchase-1"));
        Assert.True(capture.Trace.RecordRequest(request, "completion", JsonSerializer.SerializeToElement(new { succeeded = true })));
        capture.Trace.EndStep(step, GuaTraceOutcome.Passed);
        var primary = Primary(passed); var original = Outcome(primary); await Confirm(capture, primary);
        var cleanup = capture.Trace.BeginStep(GuaTraceStepKind.Lifecycle, "cleanup");
        capture.Observe("owner-cleanup", ObservationBoundary.AfterCleanup, cleanup, "ui", "cleanup", "available", new("host-source", "1"));
        capture.Trace.EndStep(cleanup, GuaTraceOutcome.Passed);
        var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow, Recording());
        Assert.True(completed.Persistence.Saved); Assert.Equal(passed ? 0 : 1, completed.ExitCode(original));
        Assert.Same(primary, original.Primary); Assert.Equal(Secret, original.Exceptions[0].StackTrace);
        var omitted = passed && policy == GuaTraceSavePolicy.OnFailure;
        foreach (var receipt in completed.Receipts)
            Assert.Equal(omitted ? ArtifactState.OmittedOnSuccess : ArtifactState.Saved, receipt.State);
        if (omitted)
        {
            Assert.False(Directory.Exists(capture.Trace.ArtifactPath));
            Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        }
        else
        {
            var receipt = completed.Receipts[0];
            var inventory = new[] { new ArtifactFileReference(receipt.FileName!, receipt.Bytes!.Value, receipt.Sha256!) }.Concat(receipt.AdditionalFiles!).ToArray();
            Assert.Equal(Directory.GetFiles(capture.Trace.ArtifactPath, "*", SearchOption.AllDirectories).Length, inventory.Length);
            foreach (var file in inventory)
            {
                var bytes = File.ReadAllBytes(Path.Combine(store.DirectoryPath, file.FileName));
                Assert.Equal(bytes.Length, file.Bytes); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256);
            }
            var read = GuaTraceReader.Read(capture.Trace.ArtifactPath);
            Assert.True(read.Manifest.Finalized); Assert.Empty(read.Issues);
            Assert.Equal(passed ? "passed" : "failed", read.Manifest.PrimaryOutcome);
            Assert.Contains(read.Events, x => x.Type == "runner.preparation" && x.Data.GetProperty("code").GetString() == "LaunchFailed");
            Assert.Contains(read.Events, x => x.Type == "observation.change");
            Assert.Contains(read.Events, x => x.Type == "assertion.evaluation");
            Assert.Equal("true", Assert.Single(read.Events, x => x.Type == "assertion.evaluation").Data.GetProperty("truth").GetString());
            Assert.Contains(read.Events, x => x.Type == "planner.reference" && x.Data.GetProperty("observationId").GetString() == before);
            Assert.Contains(read.Events, x => x.Type == "attachment");
            Assert.Single(GuaRecordingFile.Load(Path.Combine(store.DirectoryPath, completed.Receipts[1].FileName!)).Steps);
            Assert.True(GuaTraceReport.WriteHtml(capture.Trace.ArtifactPath, Path.Combine(root, "report.html")).Succeeded);
        }
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
        using var frozen = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath, "primary.json")));
        Assert.DoesNotContain(frozen.RootElement.GetProperty("observations").EnumerateArray(), x => x.GetProperty("boundary").GetString() == "AfterCleanup");
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, File.ReadAllText(file));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Actual_capture_failure_preserves_primary_and_original_exceptions(bool passed)
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Mark, "capture");
        Assert.NotEmpty(capture.Trace.CaptureTree(step, "ui", "host-source", "before", () => throw new IOException(Secret)));
        capture.Trace.EndStep(step, GuaTraceOutcome.Unknown);
        var primary = Primary(passed); var original = Outcome(primary); await Confirm(capture, primary);
        var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow, recordingCaptureFailed: true);
        Assert.All(completed.Receipts, x => Assert.Equal(ArtifactState.CaptureFailed, x.State));
        Assert.True(completed.Persistence.Saved); Assert.Equal(passed ? 11 : 1, completed.ExitCode(original));
        Assert.True(original.PostProcessingComplete); Assert.Equal(Secret, original.Exceptions[0].StackTrace);
        Assert.False((await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).Result!.PostProcessing.Complete);
    }

    [Fact]
    public async Task Actual_guarded_recording_write_failure_keeps_primary_and_completed_cleanup_facts()
    {
        using var host=new GuaRuntime(); host.BeginFrame("title"); host.RegisterNode(new("start","button","Start",new(0,0,10,10))); host.EndFrame();
        using var port=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); port.Start(); int number=((System.Net.IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number));
        using var observations=new BridgeObservations(host.InspectorBridgeUrl,GuaObservationProfile.Debug,TimeSpan.FromSeconds(2),100,65536);
        using var click=observations.CreateGuardedUiClick();
        var recording=new GuardedClickRecording(click,"start",_=>true,document=>document.Clone(),65536,1000,65536,default);
        recording.Start(()=>{},TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        recording.ObserveReceipt(click.Poll());
        using var share=new CancellationTokenSource(TimeSpan.FromSeconds(2)); Assert.True(await recording.ReleaseAsync(share.Token));
        var recorded=recording.Freeze(); Assert.False(recorded.CaptureFailed); Assert.Single(recorded.Recording!.Steps);
        var store=Store(); await using var capture=new GuaRunArtifacts(store,Metadata,Limits,Options(),Redactor);
        // Real filesystem refusal, after genuine capture and confirmed release; no fault switch in product.
        File.WriteAllText(Path.Combine(Assert.Single(Directory.GetDirectories(root,".gua-pending-*")),"recording"),"blocked");
        var primary=Primary(true); var original=new RunOutcome(primary,[],[],[]); await ConfirmCore(capture,new(primary,[],[]));
        var completed=await ObservationRunCommand.CompletePreservingOutcomeAsync(capture,original,recorded);
        Assert.Equal(ArtifactState.Saved,completed.Receipts[0].State);
        Assert.Equal(ArtifactState.SaveFailed,completed.Receipts[1].State); Assert.Equal(11,completed.ExitCode(original));
        Assert.Same(primary,original.Primary); Assert.Empty(original.PostProcessing); Assert.True(original.PostProcessingComplete);
    }

    [Fact]
    public async Task Actual_flush_write_failure_and_recording_secret_refusal_are_separate()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        // Actual Gua writer cannot create its owned output below this regular file.
        File.WriteAllText(Path.GetDirectoryName(capture.Trace.ArtifactPath)!, "blocked");
        var primary = Primary(true); await Confirm(capture, primary);
        var completed = await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow, Recording(Secret));
        Assert.Equal(ArtifactState.SaveFailed, completed.Receipts[0].State);
        Assert.Contains("write-failed", capture.Trace.Status.Issues);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[1].State);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.Equal(11, completed.ExitCode(Outcome(primary)));
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) Assert.DoesNotContain(Secret, File.ReadAllText(file));
    }

    [Fact]
    public async Task Run_executor_confirms_before_release_and_records_preconnection_failure()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var clock = new ImmediateClock();
        var run = new RunSession(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, 3, 2, 32), clock, clock);
        var released = false;
        var result = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), (cleanup, _) =>
        {
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "run.json")));
            capture.Record(new(PreparationStage.Launch, PreparationCode.LaunchFailed));
            cleanup.Register(CleanupStage.ResourceRelease, _ =>
            { Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "primary.json"))); released = true; return ValueTask.FromResult(true); });
            throw new IOException(Secret);
        }, (_, _) => throw new Exception("Unreachable"), confirmPrimary: capture.ConfirmPrimaryAsync);
        Assert.True(released); Assert.Equal(RunPhase.Preparation, result.Primary.Cause.Phase);
        var completed = await capture.CompleteAsync(result, DateTimeOffset.UtcNow);
        Assert.Equal(10, completed.ExitCode(result)); Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(ArtifactState.NotExecuted, completed.Receipts[1].State);
        Assert.Contains(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type == "runner.preparation" && x.Data.GetProperty("code").GetString() == "LaunchFailed");
    }
    private sealed class ImmediateClock : Gua.Playtest.Core.IClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => new(Task.Delay(Timeout.Infinite, cancellationToken));
    }

    [Fact]
    public async Task Actual_preparation_owner_routes_launch_failure_to_common_gua_trace_before_connect()
    {
        var store = Store(); await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var clock = new ImmediateClock();
        var run = new RunSession(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, 3, 2, 100), clock, clock);
        var launcher = new FailingLauncher(store); var connector = new UnusedConnector();
        var policy = new PreparationPolicy(HostMode.Launch, PlayMode.Replay,
            new Uri("ws://localhost:7777/" + Guid.NewGuid().ToString("N")), "game", "1", "Player", "real",
            ["observe"], true, true, new("explicit.exe", "explicit-directory", []),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), 1, TimeSpan.FromSeconds(1));
        var preparation = new HostPreparation(policy, clock, launcher, connector, artifacts);
        var outcome = await RunExecutor.ExecuteAsync(run, clock, new OwnedCleanup(), async (cleanup, token) =>
        {
            await preparation.PrepareAsync(run, cleanup, null, null, token); return true;
        }, (_, _) => throw new Exception("Unreachable"), confirmPrimary: artifacts.ConfirmPrimaryAsync);
        Assert.Equal(1, launcher.Calls); Assert.Equal(0, connector.Calls);
        Assert.Equal(ResultStatus.Failed, outcome.Primary.Status); Assert.Equal(RunOrigin.Host, outcome.Primary.Cause.Origin);
        Assert.Contains(outcome.Exceptions, x => x.Type == typeof(IOException).FullName);
        var complete = await artifacts.CompleteAsync(outcome, DateTimeOffset.UtcNow);
        Assert.Equal(1, complete.ExitCode(outcome)); Assert.Equal(ArtifactState.Saved, complete.Receipts[0].State);
        var trace = GuaTraceReader.Read(artifacts.Trace.ArtifactPath);
        var lifecycle = trace.Events.Where(x => x.Type == "runner.preparation").ToArray();
        Assert.Equal("Started", lifecycle[0].Data.GetProperty("stage").GetString());
        Assert.Single(lifecycle, x => x.Data.GetProperty("stage").GetString() == "Started");
        Assert.Contains(lifecycle, x => x.Data.GetProperty("stage").GetString() == "Launch" && x.Data.GetProperty("code").GetString() == "LaunchFailed");
        Assert.DoesNotContain(lifecycle, x => x.Data.GetProperty("stage").GetString() == "Connect");
    }
    private sealed class FailingLauncher(RunArtifactStore store) : IProcessLauncher
    {
        public int Calls;
        public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token)
        { Calls++; Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "run.json"))); throw new IOException(Secret); }
    }
    private sealed class UnusedConnector : IPreparationConnector
    {
        public int Calls;
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token)
        { Calls++; throw new Exception("Unreachable"); }
    }

    [Theory]
    [InlineData(ResultStatus.Aborted, RunReason.Cancelled, 4, "interrupted")]
    [InlineData(ResultStatus.TimedOut, RunReason.MaxDuration, 3, "failed")]
    [InlineData(ResultStatus.Invalid, RunReason.InvalidContract, 2, "failed")]
    [InlineData(ResultStatus.Unverified, RunReason.ExplorationFinished, 5, "failed")]
    public async Task Nonpassed_primary_is_not_success_omission_or_replaced_by_cancellation(
        ResultStatus status, RunReason reason, int exit, string traceOutcome)
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits,
            Options(policy: GuaTraceSavePolicy.OnFailure), Redactor);
        var primary = new PrimaryResult(status, new(reason, RunPhase.Execution, RunOrigin.Host));
        await Confirm(capture, primary);
        var outcome = Outcome(primary) with { PostProcessing = [new(PostProcessingReason.ResourceReleaseUnconfirmed)] };
        var completed = await capture.CompleteAsync(outcome, DateTimeOffset.UtcNow);
        Assert.Equal(exit, completed.ExitCode(outcome)); Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(traceOutcome, GuaTraceReader.Read(capture.Trace.ArtifactPath).Manifest.PrimaryOutcome);
        Assert.Null(typeof(GuaRunTrace).GetMethod("SetPrimaryOutcome"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ConfirmPrimaryAsync(new(Primary(true), [], []), default).AsTask());
    }

    [Fact]
    public async Task Primary_cleanup_boundary_foreign_run_and_complete_replacement_are_rejected()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Mark, "basis");
        Assert.Throws<InvalidOperationException>(() => capture.Observe("late", ObservationBoundary.AfterCleanup,
            step, "ui", "cleanup", "available", new("host", "1")));
        capture.Observe("basis", ObservationBoundary.PrimaryDecision, step, "ui", "primary", "available", new("host", "1"));
        Assert.Throws<InvalidOperationException>(() => capture.RecordPlannerDecision(step,
            new("foreign-run", "decision", "basis", PlannerFeedbackCode.Approved)));
        capture.Trace.EndStep(step, GuaTraceOutcome.Passed);
        var primary = Primary(true); await Confirm(capture, primary);
        Assert.Throws<InvalidOperationException>(() => capture.Observe("early", ObservationBoundary.Before,
            step, "ui", "before", "available", new("host", "1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CompleteAsync(Outcome(Primary(false)), DateTimeOffset.UtcNow));
        Assert.True((await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow)).Persistence.Saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Underlying_attachment_limit_cannot_become_a_saved_or_omitted_receipt()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits,
            Options(policy: GuaTraceSavePolicy.OnFailure, attachmentBytes: 32), Redactor);
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Mark, "large");
        Assert.False(capture.Trace.Attach(step, "fixture", JsonSerializer.SerializeToElement(new { text = new string('x', 1000) })));
        capture.Trace.EndStep(step, GuaTraceOutcome.Passed);
        var primary = Primary(true); await Confirm(capture, primary);
        var completed = await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[0].State);
        Assert.Null(completed.Receipts[0].FileName); Assert.Equal(11, completed.ExitCode(Outcome(primary)));
    }

    [Fact]
    public async Task Actual_native_transport_recording_capture_roundtrips_without_a_gameplay_lease()
    {
        // A standalone native/transport fixture. No Runner dispatch or production
        // lifecycle capability is fabricated by this GuaRecorder capture.
        using var runtime = new GuaRuntime();
        runtime.BeginFrame("fixture"); runtime.RegisterNode(new("buy", "button", "Buy", new(0, 0, 10, 10))); runtime.EndFrame();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.True(runtime.StartInspectorBridge(port));
        using var context = new GuaWebSocketContext(runtime.InspectorBridgeUrl);
        var recorder = new GuaRecorder(context);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captureTask = recorder.ClickAsync(new(Id: "buy"), timeout: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromMilliseconds(5), cancellationToken: deadline.Token);
        var consumed = 0;
        while (!captureTask.IsCompleted)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request))
            { consumed++; runtime.EmitActionResult(request, true); }
            await Task.Delay(5, deadline.Token);
        }
        var completion = await captureTask;
        Assert.True(completion.Succeeded); Assert.Equal(1, consumed);
        var recording = recorder.Recording; Assert.Single(recording.Steps);
        Assert.Equal(completion.RequestId, recording.Steps[0].RequestId);
        var store = Store(); await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var primary = Primary(true); await Confirm(artifacts, primary);
        var saved = await artifacts.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow, recording);
        Assert.Equal(ArtifactState.Saved, saved.Receipts[1].State);
        var loaded = GuaRecordingFile.Load(Path.Combine(store.DirectoryPath, saved.Receipts[1].FileName!));
        Assert.Equal(recording.Steps, loaded.Steps);
        Assert.Equal(0, saved.ExitCode(Outcome(primary)));
    }

    [Fact]
    public async Task Earlier_preparation_persistence_failure_and_final_cancellation_keep_exit11_and_missing_result()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor,
            new CancellationToken(true));
        Assert.Equal(PersistenceFailure.Cancelled, capture.PreparationPersistence.Failure);
        var primary = Primary(true); await Confirm(capture, primary);
        var completed = await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.True(completed.Persistence.Saved); Assert.Equal(11, completed.ExitCode(Outcome(primary)));
        Assert.False((await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).Result!.PostProcessing.Complete);

        var interruptedStore = Store(); await using var interrupted = new GuaRunArtifacts(interruptedStore, Metadata, Limits, Options(), Redactor);
        await Confirm(interrupted, primary);
        var cancelled = await interrupted.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow, token: new CancellationToken(true));
        Assert.Equal(PersistenceFailure.Cancelled, cancelled.Persistence.Failure);
        Assert.Equal(11, cancelled.ExitCode(Outcome(primary)));
        Assert.Equal(ResultReadState.Missing, (await RunArtifactReader.ReadResultAsync(interruptedStore.DirectoryPath, Limits)).State);
        Assert.Equal("passed", GuaTraceReader.Read(interrupted.Trace.ArtifactPath).Manifest.PrimaryOutcome);
    }

    [Fact]
    public async Task Gua_retained_uncertainty_on_success_is_not_claimed_as_omitted()
    {
        var store = Store(); await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits,
            Options(policy: GuaTraceSavePolicy.OnFailure), Redactor);
        var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Action, "unconfirmed request");
        artifacts.Trace.EndStep(step, GuaTraceOutcome.Unknown);
        var primary = Primary(true); await Confirm(artifacts, primary);
        var saved = await artifacts.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, saved.Receipts[0].State);
        Assert.Contains("uncertain-step-outcome", GuaTraceReader.Read(artifacts.Trace.ArtifactPath).Manifest.Quality.Issues);
        Assert.Equal("passed", GuaTraceReader.Read(artifacts.Trace.ArtifactPath).Manifest.PrimaryOutcome);
    }

    [Fact]
    public async Task Streaming_cumulative_events_never_exceed_individual_file_ceiling()
    {
        var store = Store();
        var options = new GuaTraceOptions
        {
            CaptureMode = GuaTraceCaptureMode.Streaming, SavePolicy = GuaTraceSavePolicy.Always,
            Profile = "player", Secrets = [Secret], MaxSteps = 100, MaxMemoryBytes = 4 * 1024 * 1024,
            MaxQueueItems = 256, MaxArtifactBytes = 1024 * 1024, MaxEventBytes = 32768,
            MaxAttachmentBytes = 32768, FlushTimeout = TimeSpan.FromSeconds(5)
        };
        await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits, options, Redactor);
        for (var i = 0; i < 70; i++)
        {
            var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, "large");
            artifacts.Trace.Record(step, "fixture.large", JsonSerializer.SerializeToElement(new { text = new string('x', 20000) }));
            artifacts.Trace.EndStep(step, GuaTraceOutcome.Passed);
        }
        var primary = Primary(true); await Confirm(artifacts, primary);
        var completed = await artifacts.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[0].State);
        Assert.Contains("artifact-limit", artifacts.Trace.Status.Issues);
        Assert.Equal(11, completed.ExitCode(Outcome(primary)));
        var events = new FileInfo(Path.Combine(artifacts.Trace.ArtifactPath, "events.jsonl"));
        Assert.True(events.Exists); Assert.InRange(events.Length, 1, Limits.MaxFileBytes);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            Assert.InRange(new FileInfo(file).Length, 0, Limits.MaxFileBytes);
    }

    [Fact]
    public void Unguaranteed_file_ceiling_is_rejected_before_creating_a_writer()
    {
        var store = Store();
        var options = new GuaTraceOptions { Profile = "player", SavePolicy = GuaTraceSavePolicy.Always,
            MaxArtifactBytes = 2 * 1024 * 1024, MaxEventBytes = 32768, MaxAttachmentBytes = 32768 };
        Assert.Throws<ArgumentException>(() => new GuaRunArtifacts(store, Metadata, Limits, options, Redactor));
        Assert.Equal(".owner", Path.GetFileName(Assert.Single(Directory.GetFiles(store.DirectoryPath))));
        Assert.Empty(Directory.GetDirectories(store.DirectoryPath));
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("record")]
    [InlineData("request")]
    [InlineData("attachment")]
    [InlineData("annotation")]
    public async Task Sensitive_named_fields_with_unlisted_values_are_filtered_before_every_trace_port(string port)
    {
        const string privateValue = "UNLISTED_PRIVATE_FIELD_MARKER";
        var redactor = new PersistenceRedactor([Secret], ["credential"]);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var request = new GuaTraceRequest("host", "1", "17");
        var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, "safe", request);
        var original = JsonSerializer.SerializeToElement(new { credential = privateValue, nested = new[] { new { CREDENTIAL = privateValue } } });
        switch (port)
        {
            case "observation": Assert.NotEmpty(artifacts.Observe("basis", ObservationBoundary.Before,
                step, "ui", "before", "available", new("host", "1"), original)); break;
            case "record": Assert.True(artifacts.Trace.Record(step, "fixture", original)); break;
            case "request": Assert.True(artifacts.Trace.RecordRequest(request, "completion", original)); break;
            case "attachment": Assert.True(artifacts.Trace.Attach(step, "fixture", original)); break;
            case "annotation":
                using (var value = GuaValue.String(privateValue)) Assert.True(artifacts.Trace.Annotate(step, "credential", value)); break;
        }
        artifacts.Trace.EndStep(step, GuaTraceOutcome.Passed);
        Assert.Equal(privateValue, original.GetProperty("credential").GetString());
        var primary = Primary(true); await Confirm(artifacts, primary);
        var completed = await artifacts.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(0, completed.ExitCode(Outcome(primary)));
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(privateValue, File.ReadAllText(file));
        Assert.Empty(GuaTraceReader.Read(artifacts.Trace.ArtifactPath).Issues);
    }

    [Fact]
    public async Task Sensitive_recording_field_is_rejected_without_rewriting_replay_identity()
    {
        const string privateValue = "UNLISTED_PRIVATE_RECORDING_MARKER";
        var redactor = new PersistenceRedactor([Secret], ["value"]);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var artifacts = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var primary = Primary(true); await Confirm(artifacts, primary);
        var recording = Recording(privateValue);
        var completed = await artifacts.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow, recording);
        Assert.Equal(privateValue, recording.Steps[0].Value);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[1].State);
        Assert.Equal(11, completed.ExitCode(Outcome(primary)));
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(privateValue, File.ReadAllText(file));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Public_availability_is_trusted_while_snapshot_real_data_is_redacted(bool named)
    {
        var redactor = new PersistenceRedactor(["available"], named ? ["availability"] : []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.Mark("safe");
        Assert.NotEmpty(capture.Trace.Observe(step, "ui", "before", "available", new("host", "1"),
            JsonSerializer.SerializeToElement(new { token = "available" })));
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var result = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, result.Receipts[0].State);
        var read = GuaTraceReader.Read(capture.Trace.ArtifactPath); Assert.Empty(read.Issues);
        Assert.Contains(read.Events, x => x.Type == "observation" && x.Data.GetProperty("availability").GetString() == "available");
    }

    [Theory]
    [InlineData("request")] [InlineData("host")]
    [InlineData("sourceId")] [InlineData("sessionEpoch")] [InlineData("requestId")]
    [InlineData("frame")] [InlineData("revision")] [InlineData("timestamp")] [InlineData("clockId")]
    [InlineData("decisionId")] [InlineData("actionId")]
    public async Task Named_sensitive_actual_identifiers_never_enter_native_capture(string field)
    {
        var redactor = new PersistenceRedactor([], [field]); var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.Mark("safe");
        if (field is "request" or "sourceId" or "sessionEpoch" or "requestId")
            Assert.Empty(capture.Trace.BeginStep(GuaTraceStepKind.Action, "safe", new("private-host", "7135", "9246")));
        else if (field is "decisionId" or "actionId")
            Assert.False(capture.Trace.RecordStructural(step, "planner.reference", JsonSerializer.SerializeToElement(new
                { decisionId = "private-decision", actionId = "private-action", observationId = "generated-observation", code = "Approved" })));
        else
            Assert.Empty(capture.Trace.Observe(step, "ui", "before", "available", new("host", "1", "1234", "5678", "9012", "private-clock")));
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var result = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.CaptureFailed, result.Receipts[0].State);
        Assert.DoesNotContain(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type is "request" or "observation" or "planner.reference");
    }

    [Theory]
    [InlineData("12345")] [InlineData("67890")] [InlineData("12.5")]
    public async Task Actual_recording_numeric_input_secrets_are_refused(string secret)
    {
        var redactor = new PersistenceRedactor([secret], []); var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var recording = new GuaRecording(1, [new(GuaRecordedAction.scroll, 0, 1, 2, false,
            CoordinateFallback: new(12345, 67890), DeltaX: 12.5f)]);
        GuaRecordingFile.Validate(recording);
        var result = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow, recording);
        Assert.Equal(ArtifactState.CaptureFailed, result.Receipts[1].State);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.DoesNotContain(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type == "attachment");
    }

    [Theory]
    [InlineData("coordinateFallback")] [InlineData("x")] [InlineData("y")]
    [InlineData("deltaX")] [InlineData("deltaY")] [InlineData("scrollUnit")] [InlineData("modifiers")]
    [InlineData("requestId")] [InlineData("preRevision")] [InlineData("relativeMilliseconds")]
    public async Task Named_sensitive_recording_input_is_refused_before_attachment(string field)
    {
        var redactor = new PersistenceRedactor([], [field]); var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var recording = field == "modifiers"
            ? new GuaRecording(1, [new(GuaRecordedAction.press_key, 123, 456, 789, false, RequestId: 12345, Target: new(CurrentFocus: true), Value: "Enter", Modifiers: 2)])
            : new GuaRecording(1, [new(GuaRecordedAction.scroll, 123, 456, 789, false, RequestId: 12345, CoordinateFallback: new(12345, 67890), DeltaX: 12, DeltaY: 34, ScrollUnit: 1)]);
        GuaRecordingFile.Validate(recording);
        var result = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow, recording);
        Assert.Equal(ArtifactState.CaptureFailed, result.Receipts[1].State);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.DoesNotContain(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type == "attachment");
    }

    [Fact]
    public void Mandatory_report_slot_is_reserved_before_writer_creation()
    {
        var store = Store(); var limits = new ArtifactLimits(1024 * 1024, 8 * 1024 * 1024, 2, 65536);
        Assert.Throws<ArgumentException>(() => new GuaRunArtifacts(store, Metadata, limits, Options(), Redactor));
        Assert.Equal(".owner", Path.GetFileName(Assert.Single(Directory.GetFiles(store.DirectoryPath))));
        Assert.Empty(Directory.GetDirectories(store.DirectoryPath));
    }

    [Fact]
    public async Task Minimum_gua_bundle_has_exactly_manifest_events_and_report()
    {
        Directory.CreateDirectory(root);
        await using var writer = new GuaRunArtifactWriter(root, Options(), Limits.MaxFileBytes, Limits.MaxRunBytes,
            3, _ => true, _ => true, document => document, _ => false, text => text, Limits.MaxStringChars, Limits.MaxItems);
        writer.Trace.Mark("safe"); Assert.True(writer.ConfirmPrimary(GuaTraceOutcome.Passed));
        var completed = await writer.CompleteAsync();
        Assert.Equal(GuaArtifactState.Saved, completed.Trace.State);
        Assert.Equal(3, Directory.GetFiles(writer.Trace.ArtifactPath, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(2, completed.Trace.AdditionalFiles!.Count);
        Assert.Empty(GuaTraceReader.Read(writer.Trace.ArtifactPath).Issues);
    }

    [Fact]
    public void Sensitive_null_projection_still_charges_every_retained_node()
    {
        var redactor = new PersistenceRedactor([], ["one", "two", "three", "four"]);
        var limits = new ArtifactLimits(1024 * 1024, 8 * 1024 * 1024, 3, 65536);
        var document = JsonSerializer.SerializeToElement(new { one = 1, two = 2, three = 3, four = 4 });
        Assert.Throws<ArtifactLimitException>(() => redactor.RedactDocument(document, limits));
        Assert.Equal(1, document.GetProperty("one").GetInt32());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void External_review_whole_mask_counts_generated_null_and_boolean_nodes(bool sensitiveMarker)
    {
        var redactor = new PersistenceRedactor([], sensitiveMarker ? ["redacted"] : []);
        using var document = JsonDocument.Parse("[{\"sensitive\":true},{\"mask\":true}]");
        Assert.Throws<ArtifactLimitException>(() => redactor.RedactDocument(document.RootElement,
            new(65536, 1024 * 1024, 3, 20000)));
        var permitted = redactor.RedactDocument(document.RootElement, new(65536, 1024 * 1024, 5, 20000));
        Assert.Equal(2, permitted.GetArrayLength());
    }

    [Fact]
    public void External_review_projection_bounds_escaped_encoding_before_returning_element()
    {
        using var document = JsonDocument.Parse("\"" + new string('é', 20000) + "\"");
        Assert.Throws<ArtifactLimitException>(() => Redactor.RedactDocument(document.RootElement,
            new(65536, 1024 * 1024, 3, 20000)));
        Assert.Equal(20000, document.RootElement.GetString()!.Length);
    }

    [Fact]
    public async Task External_review_capture_port_cannot_preempt_primary_confirmation()
    {
        var store = Store(); await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), Redactor);
        var authority = typeof(GuaRunTrace).GetMethod("SetPrimaryOutcome");
        if (authority is not null) authority.Invoke(capture.Trace, [GuaTraceOutcome.Passed]);
        var primary = Primary(true); await Confirm(capture, primary);
        var completed = await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(0, completed.ExitCode(Outcome(primary)));
        Assert.Null(authority);
    }

    [Theory]
    [InlineData(GuaTraceCaptureMode.Recent, true)] [InlineData(GuaTraceCaptureMode.Recent, false)]
    [InlineData(GuaTraceCaptureMode.Streaming, true)] [InlineData(GuaTraceCaptureMode.Streaming, false)]
    public async Task Fixed_public_html_text_matching_a_secret_does_not_refuse_report(GuaTraceCaptureMode mode, bool passed)
    {
        var redactor = new PersistenceRedactor(["Gua Trace Viewer v1"], []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(mode), redactor);
        var primary = Primary(passed); var original = new RunOutcome(primary, [], [], []);
        await ConfirmCore(capture, new(primary, [], []));
        var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(passed ? 0 : 1, completed.ExitCode(original));
        Assert.Same(primary, original.Primary);
        Assert.Contains("Gua Trace Viewer v1", File.ReadAllText(Path.Combine(capture.Trace.ArtifactPath, "report.html")));
        Assert.Equal(ResultReadState.Verified, (await RunArtifactReader.ReadResultAsync(store.DirectoryPath, Limits)).State);
    }

    [Fact]
    public async Task Gua_report_Trace_title_is_trusted_public_renderer_text()
    {
        Directory.CreateDirectory(root);
        var redactor = new PersistenceRedactor(["Trace"], []);
        await using var writer = new GuaRunArtifactWriter(root, Options(), Limits.MaxFileBytes, Limits.MaxRunBytes,
            Limits.MaxItems, text => redactor.Redact(text) == text, bytes => redactor.IsSafeUtf8(bytes.Span),
            document => redactor.RedactDocument(document, Limits), redactor.IsSensitiveField, redactor.Redact, Limits.MaxStringChars, Limits.MaxItems);
        Assert.NotEmpty(writer.Trace.Mark("safe"));
        Assert.True(writer.ConfirmPrimary(GuaTraceOutcome.Passed));
        Assert.Equal(GuaArtifactState.Saved, (await writer.CompleteAsync()).Trace.State);
        Assert.Contains("Gua Trace Viewer v1", File.ReadAllText(Path.Combine(writer.Trace.ArtifactPath, "report.html")));
    }

    [Fact]
    public void Review3_sensitive_replacement_at_depth32_is_rejected_and_depth31_is_admitted()
    {
        var redactor = new PersistenceRedactor([], ["credential"]);
        using var tooDeep = JsonDocument.Parse(new string('[', 32) + "{\"credential\":1}" + new string(']', 32));
        Assert.Throws<ArtifactLimitException>(() => redactor.RedactDocument(tooDeep.RootElement, Limits));
        using var admitted = JsonDocument.Parse(new string('[', 31) + "{\"credential\":1}" + new string(']', 31));
        Assert.DoesNotContain("1", redactor.RedactDocument(admitted.RootElement, Limits).GetRawText());
    }

    [Theory]
    [InlineData("label")] [InlineData("sourceFile")] [InlineData("request")]
    [InlineData("eventType")] [InlineData("phase")] [InlineData("schema")]
    [InlineData("reason")] [InlineData("host")] [InlineData("annotation")]
    [InlineData("evaluation")] [InlineData("change")] [InlineData("tree")]
    public async Task Review3_direct_gua_strings_are_bounded_before_queue_or_capture(string lane)
    {
        var limits = new ArtifactLimits(Limits.MaxFileBytes, Limits.MaxRunBytes, Limits.MaxItems, 16);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, limits, Options(), Redactor);
        var trace = capture.Trace; var step = trace.Mark("safe"); Assert.NotEmpty(step);
        var request = new GuaTraceRequest("host", "1", "1");
        Assert.True(trace.Correlate(step, request));
        const string oversized = "too-long-string-field";
        var empty = JsonSerializer.SerializeToElement(new { });
        using var one = GuaValue.Integer(1);
        switch (lane)
        {
            case "label": Assert.Empty(trace.Mark(oversized)); break;
            case "sourceFile": Assert.Empty(trace.BeginStep(GuaTraceStepKind.Mark, "safe", sourceFile: oversized)); break;
            case "request": Assert.Empty(trace.BeginStep(GuaTraceStepKind.Action, "safe", request with { SourceId = oversized })); break;
            case "eventType": Assert.False(trace.Record(step, oversized, empty)); break;
            case "phase": Assert.False(trace.RecordRequest(request, oversized, empty)); break;
            case "schema": Assert.False(trace.Attach(step, oversized, empty)); break;
            case "reason": Assert.Empty(trace.Observe(step, "ui", oversized, "available", new("host", "1"))); break;
            case "host": Assert.Empty(trace.Observe(step, "ui", "safe", "available", new("host", "1", Revision: oversized))); break;
            case "annotation": Assert.False(trace.Annotate(step, oversized, one)); break;
            case "evaluation": Assert.False(trace.Evaluate(step, oversized, "equal", one, one, "true", "success", [], GuaTraceOutcome.Passed)); break;
            case "change": Assert.False(trace.Change(step, oversized, "changed", new("host", "1"), one, one)); break;
            case "tree":
                var calls = 0;
                Assert.Empty(trace.CaptureTree(step, "ui", oversized, "safe", () => { calls++; return "{}"; }));
                Assert.Equal(0, calls); break;
        }
        Assert.False(trace.Status.DetailStopped); // host admission precedes Gua's own byte-limit shutdown
    }

    [Theory]
    [InlineData("target")] [InlineData("value")]
    public async Task Review3_recording_strings_use_configured_ceiling_before_attachment_or_file(string lane)
    {
        var limits = new ArtifactLimits(Limits.MaxFileBytes, Limits.MaxRunBytes, Limits.MaxItems, 4);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, limits, Options(), Redactor);
        var primary = Primary(true);
        await capture.ConfirmPrimaryAsync(new(primary, [], []), default);
        var recording = lane == "value" ? Recording("too-long") : new GuaRecording(1,
            [new(GuaRecordedAction.click, 0, 1, 2, false, RequestId: 17, Target: new(Id: "too-long"))]);
        var completed = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow, recording);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[1].State);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.DoesNotContain(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events,
            x => x.Type == "attachment" && x.Data.GetRawText().Contains("recording"));
    }

    [Theory]
    [InlineData(3, false)] [InlineData(5, false)] [InlineData(6, true)]
    public async Task Review4_recording_reserves_shared_slots_before_attachment_and_file(int slots, bool extraAttachment)
    {
        Directory.CreateDirectory(root);
        await using var capture = new GuaRunArtifactWriter(root, Options(), Limits.MaxFileBytes, Limits.MaxRunBytes,
            slots, _ => true, _ => true, document => document, _ => false, text => text, Limits.MaxStringChars, Limits.MaxItems);
        var step = slots == 5 ? string.Empty : capture.Trace.Mark("safe");
        if (extraAttachment) Assert.True(capture.Trace.Attach(step, "extra", JsonSerializer.SerializeToElement(new { extra = 1 })));
        Assert.True(capture.ConfirmPrimary(GuaTraceOutcome.Passed));
        var completed = await capture.CompleteAsync(Recording());
        Assert.Equal(extraAttachment ? GuaArtifactState.Saved : GuaArtifactState.SaveFailed, completed.Recording.State);
        Assert.Equal(extraAttachment || slots == 5 ? GuaArtifactState.SaveFailed : GuaArtifactState.Saved, completed.Trace.State);
        Assert.InRange(new[] { completed.Trace, completed.Recording }.Where(x => x.State == GuaArtifactState.Saved)
            .Sum(x => 1 + x.AdditionalFiles!.Count), 0, slots);
        if (!extraAttachment)
        {
            Assert.False(Directory.Exists(Path.Combine(root, "recording")));
            if (slots == 3) Assert.Equal(3, Directory.GetFiles(capture.Trace.ArtifactPath, "*", SearchOption.AllDirectories).Length);
        }
        else Assert.False(File.Exists(Path.Combine(capture.Trace.ArtifactPath, "report.html")));
        if (slots == 5)
        {
            Directory.CreateDirectory(Path.Combine(root, "six-slots"));
            await using var control = new GuaRunArtifactWriter(Path.Combine(root, "six-slots"), Options(),
                Limits.MaxFileBytes, Limits.MaxRunBytes, 6, _ => true, _ => true, document => document,
                _ => false, text => text, Limits.MaxStringChars, Limits.MaxItems);
            Assert.True(control.ConfirmPrimary(GuaTraceOutcome.Passed));
            var accepted = await control.CompleteAsync(Recording());
            Assert.True(accepted.Trace.State == GuaArtifactState.Saved, JsonSerializer.Serialize(new
                { accepted, status = control.Trace.Status, files = Directory.GetFiles(control.Trace.ArtifactPath, "*", SearchOption.AllDirectories),
                    read = GuaTraceReader.Read(control.Trace.ArtifactPath) }));
            Assert.Equal(GuaArtifactState.Saved, accepted.Recording.State);
            Assert.Equal(6, new[] { accepted.Trace, accepted.Recording }.Sum(x => 1 + x.AdditionalFiles!.Count));
        }
    }

    [Theory]
    [InlineData(65536, 11000)] [InlineData(1048576, 180000)]
    public async Task Review4_safe_ascii_recording_uses_actual_encoded_byte_size(int fileBytes, int characters)
    {
        var limits = new ArtifactLimits(fileBytes, Limits.MaxRunBytes, Limits.MaxItems, fileBytes);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        var options = Options(attachmentBytes: Math.Min(fileBytes, 300000), artifactBytes: fileBytes,
            eventBytes: Math.Min(fileBytes, 131072));
        await using var capture = new GuaRunArtifacts(store, Metadata, limits, options, Redactor);
        var primary = Primary(true); var original = new RunOutcome(primary, [], [], []); await ConfirmCore(capture, new(primary, [], []));
        var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow, Recording(new string('x', characters)));
        Assert.Equal(ArtifactState.Saved, completed.Receipts[1].State);
        Assert.InRange(completed.Receipts[1].Bytes!.Value, 1, fileBytes);
        Assert.Equal(characters, Assert.Single(GuaRecordingFile.Load(Path.Combine(store.DirectoryPath, completed.Receipts[1].FileName!)).Steps).Value!.Length);
        Assert.NotEqual(ArtifactState.CaptureFailed, completed.Receipts[0].State);
        if (fileBytes == 1048576) { Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State); Assert.Equal(0, completed.ExitCode(original)); }
    }

    [Fact]
    public async Task Review4_exact_html_that_fits_is_not_rejected_by_a_larger_estimate()
    {
        Directory.CreateDirectory(root);
        using var viewer = typeof(GuaTraceReport).Assembly.GetManifestResourceStream("Gua.Trace.Viewer.js")!;
        var payload = JsonSerializer.SerializeToElement(new { payload = new string('p', checked((int)Math.Max(0, 65536 - viewer.Length)) + 20000) });
        var baselineOptions = Options(eventBytes: 131072);
        await using var baseline = new GuaRunArtifactWriter(Path.Combine(root, "baseline"), baselineOptions,
            Limits.MaxFileBytes, Limits.MaxRunBytes, Limits.MaxItems, _ => true, _ => true, document => document,
            _ => false, text => text, Limits.MaxFileBytes, Limits.MaxItems);
        var step = baseline.Trace.Mark("safe"); Assert.True(baseline.Trace.Record(step, "payload", payload));
        Assert.True(baseline.ConfirmPrimary(GuaTraceOutcome.Passed));
        var saved = await baseline.CompleteAsync(); Assert.Equal(GuaArtifactState.Saved, saved.Trace.State);
        var ceiling = checked((int)saved.Trace.Entry!.Bytes + 1024);
        Assert.InRange(ceiling, 65536, Limits.MaxFileBytes);
        await using var bounded = new GuaRunArtifactWriter(Path.Combine(root, "bounded"),
            Options(artifactBytes: ceiling, eventBytes: Math.Min(ceiling, 131072)),
            ceiling, Limits.MaxRunBytes, Limits.MaxItems, _ => true, _ => true, document => document,
            _ => false, text => text, ceiling, Limits.MaxItems);
        var boundedStep = bounded.Trace.Mark("safe"); Assert.True(bounded.Trace.Record(boundedStep, "payload", payload));
        Assert.True(bounded.ConfirmPrimary(GuaTraceOutcome.Passed));
        var completed = await bounded.CompleteAsync(); Assert.Equal(GuaArtifactState.Saved, completed.Trace.State);
        Assert.InRange(completed.Trace.Entry!.Bytes, 1, ceiling);
        Assert.Empty(GuaTraceReader.Read(bounded.Trace.ArtifactPath).Issues);
    }

    [Theory]
    [InlineData("Trace")] [InlineData("schema")] [InlineData("event")]
    [InlineData("click")] [InlineData("passed")] [InlineData("123456")]
    [InlineData("a")]
    public async Task Public_format_vocabulary_is_not_rejected_as_a_secret_policy(string secret)
    {
        var redactor = new PersistenceRedactor([secret], []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.Mark("public marker"); Assert.NotEmpty(step);
        Assert.True(capture.Trace.Record(step, "probe", JsonSerializer.SerializeToElement(new { token = secret, safe = "visible" })));
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var original = new RunOutcome(primary, [], [], []);
        var result = await capture.CompleteAsync(original, DateTimeOffset.UtcNow, Recording());
        Assert.All(result.Receipts, x => Assert.Equal(ArtifactState.Saved, x.State));
        var recording = GuaRecordingFile.Load(Path.Combine(store.DirectoryPath, result.Receipts[1].FileName!));
        Assert.Equal(GuaRecordedAction.click, Assert.Single(recording.Steps).Action);
        Assert.Same(primary, original.Primary); Assert.Equal(0, result.ExitCode(original));
        var read = GuaTraceReader.Read(capture.Trace.ArtifactPath); Assert.Empty(read.Issues);
        var payload = Assert.Single(read.Events, x => x.Type == "probe");
        Assert.Equal("", payload.Data.GetProperty("token").GetString());
        Assert.Contains("Gua Trace Viewer v1", File.ReadAllText(Path.Combine(capture.Trace.ArtifactPath, "report.html")));
    }

    [Theory]
    [InlineData("source")] [InlineData("epoch")] [InlineData("request")] [InlineData("host")]
    [InlineData("schema")] [InlineData("channel")] [InlineData("availability")] [InlineData("references")]
    public async Task Review5_host_policy_rejects_pass_through_strings_missing_from_native_options(string lane)
    {
        var secret = lane is "epoch" or "request" ? "71357924" : "private-identity-marker";
        var redactor = new PersistenceRedactor([secret], []); var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var trace = capture.Trace; var step = trace.Mark("safe"); var empty = JsonSerializer.SerializeToElement(new { });
        var request = new GuaTraceRequest(lane == "source" ? secret : "host", lane == "epoch" ? secret : "1", lane == "request" ? secret : "1");
        using var one = GuaValue.Integer(1);
        switch (lane)
        {
            case "source": case "epoch": case "request":
                Assert.Empty(trace.BeginStep(GuaTraceStepKind.Action, "safe", request)); break;
            case "host": Assert.Empty(trace.Observe(step, "ui", "safe", "available", new(secret, "1"))); break;
            case "schema": Assert.True(trace.Attach(step, secret, empty)); break;
            case "channel": Assert.Empty(trace.Observe(step, secret, "safe", "available", new("host", "1"))); break;
            case "availability": Assert.NotEmpty(trace.Observe(step, "ui", "safe", secret, new("host", "1"))); break;
            case "references": Assert.True(trace.Evaluate(step, "safe", "equal", one, one, "true", "success", [secret], GuaTraceOutcome.Passed)); break;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Typed_preparation_facts_are_trusted_structure_not_private_input(bool sensitiveField)
    {
        var redactor = new PersistenceRedactor(sensitiveField ? [] : ["Started"], sensitiveField ? ["stage"] : []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        capture.Record(new(PreparationStage.Started, PreparationCode.Started));
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var original = new RunOutcome(primary, [], [], []); var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(0, completed.ExitCode(original)); Assert.Same(primary, original.Primary);
        Assert.Contains(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type == "runner.preparation" && x.Data.GetProperty("stage").GetString() == "Started");
    }

    [Fact]
    public async Task Review5_tree_raw_byte_ceiling_precedes_DOM_allocation()
    {
        var limits = new ArtifactLimits(65536, Limits.MaxRunBytes, Limits.MaxItems, 4096);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, limits, Options(artifactBytes: 65536), Redactor);
        var step = capture.Trace.Mark("safe"); var text = new string(' ', limits.MaxFileBytes * 40) + "{}";
        var before = GC.GetAllocatedBytesForCurrentThread();
        capture.Trace.CaptureTree(step, "ui", "host", "before", () => text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, limits.MaxFileBytes * 8L);
        Assert.Contains("tree-failed", capture.Trace.Status.Issues);
    }

    [Fact]
    public async Task Review5_recording_counts_complete_retained_nodes_before_attachment()
    {
        var limits = new ArtifactLimits(Limits.MaxFileBytes, Limits.MaxRunBytes, 100, Limits.MaxStringChars);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, limits, Options(), Redactor);
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var recording = new GuaRecording(1, Enumerable.Range(0, 10).Select(i =>
            new GuaRecordingStep(GuaRecordedAction.click, i, 1, 2, false, RequestId: (ulong)(i + 1), Target: new(Id: "buy"))).ToArray());
        var completed = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow, recording);
        Assert.Equal(ArtifactState.CaptureFailed, completed.Receipts[1].State);
        Assert.False(Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.DoesNotContain(GuaTraceReader.Read(capture.Trace.ArtifactPath).Events, x => x.Type == "attachment");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Review5_shared_run_budget_reserves_existing_and_mandatory_summaries(bool large)
    {
        var limits = new ArtifactLimits(1024 * 1024, 2 * 1024 * 1024 + 65536 + 20000, 1000, 1024 * 1024);
        var store = RunArtifactStore.Create(root, limits, Redactor);
        var text = new string('x', large ? 500000 : 16);
        var metadata = new RunArtifactMetadata("player", [], new Dictionary<string, string>(),
            [new("input", JsonSerializer.SerializeToElement(new { payload = text }))]);
        await using var capture = new GuaRunArtifacts(store, metadata, limits,
            Options(attachmentBytes: 650000), Redactor);
        Assert.True(capture.PreparationPersistence.Saved);
        var primary = Primary(true);
        await ConfirmCore(capture, new(primary, [], [new("Provider", text)]));
        var original = new RunOutcome(primary, [], [], []);
        var completed = await capture.CompleteAsync(original, DateTimeOffset.UtcNow, Recording(text));
        Assert.True(completed.Persistence.Saved);
        Assert.Equal(large ? 11 : 0, completed.ExitCode(original));
        Assert.Equal(large ? ArtifactState.SaveFailed : ArtifactState.Saved, completed.Receipts[0].State);
        Assert.Equal(large ? ArtifactState.SaveFailed : ArtifactState.Saved, completed.Receipts[1].State);
        Assert.Equal(!large, Directory.Exists(Path.Combine(store.DirectoryPath, "trace")));
        Assert.Equal(!large, Directory.Exists(Path.Combine(store.DirectoryPath, "recording")));
        Assert.True(Directory.GetFiles(store.DirectoryPath, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) <= limits.MaxRunBytes);
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "completion.json")));
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "result.json")));
        Assert.Same(primary, original.Primary);
    }

    [Theory]
    [InlineData("profile")] [InlineData("mode")] [InlineData("steps")]
    public void Review6_native_option_failure_removes_constructor_staging(string lane)
    {
        Directory.CreateDirectory(root);
        var options = new GuaTraceOptions
        {
            Profile = lane == "profile" ? "invalid" : "player",
            CaptureMode = lane == "mode" ? (GuaTraceCaptureMode)999 : GuaTraceCaptureMode.Recent,
            MaxSteps = lane == "steps" ? 0 : 100, Secrets = [Secret], MaxMemoryBytes = 1024 * 1024,
            MaxQueueItems = 256, MaxArtifactBytes = 1024 * 1024, MaxAttachmentBytes = 32768, MaxEventBytes = 32768
        };
        Assert.ThrowsAny<ArgumentException>(() => new GuaRunArtifactWriter(Path.Combine(root, "native"), options,
            Limits.MaxFileBytes, Limits.MaxRunBytes, Limits.MaxItems, text => Redactor.Redact(text) == text,
            bytes => Redactor.IsSafeUtf8(bytes.Span), document => Redactor.RedactDocument(document, Limits),
            Redactor.IsSensitiveField, Redactor.Redact, Limits.MaxStringChars, Limits.MaxItems));
        Assert.Empty(Directory.GetDirectories(root, ".gua-pending-*"));
    }

    [Theory]
    [InlineData(42)] [InlineData(23456789)]
    public async Task Source_line_real_secret_is_omitted_without_rejecting_writer(int line)
    {
        var redactor = new PersistenceRedactor([line.ToString(System.Globalization.CultureInfo.InvariantCulture)], []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Action, "safe", sourceLine: line);
        Assert.NotEmpty(step);
        capture.Trace.EndStep(step, GuaTraceOutcome.Passed);
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        var result = await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow);
        Assert.Equal(ArtifactState.Saved, result.Receipts[0].State);
        using var lines = JsonDocument.Parse(File.ReadLines(Path.Combine(capture.Trace.ArtifactPath, "events.jsonl")).First());
        Assert.DoesNotContain("\"line\":" + line, File.ReadAllText(Path.Combine(capture.Trace.ArtifactPath, "events.jsonl")));
    }

    [Theory]
    [InlineData("label")] [InlineData("sourceFile")] [InlineData("schema")]
    public async Task Json_escaping_is_owned_by_Gua_and_not_a_literal_input_secret(string lane)
    {
        const string encodedSecret = "\\u003C";
        var redactor = new PersistenceRedactor([encodedSecret], []);
        var store = RunArtifactStore.Create(root, Limits, redactor);
        await using var capture = new GuaRunArtifacts(store, Metadata, Limits, Options(), redactor);
        var step = capture.Trace.BeginStep(GuaTraceStepKind.Action, lane == "label" ? "<" : "safe",
            sourceFile: lane == "sourceFile" ? "<" : null);
        Assert.NotEmpty(step);
        if (lane == "schema") Assert.True(capture.Trace.Attach(step, "<", JsonSerializer.SerializeToElement(new { })));
        capture.Trace.EndStep(step, GuaTraceOutcome.Passed);
        var primary = Primary(true); await ConfirmCore(capture, new(primary, [], []));
        await capture.CompleteAsync(new(primary, [], [], []), DateTimeOffset.UtcNow);
        Assert.Empty(GuaTraceReader.Read(capture.Trace.ArtifactPath).Issues);
    }

    [Fact]
    public async Task Saved_dashboard_reads_real_complete_bundle_and_renders_gua_report()
    {
        Directory.CreateDirectory(root);
        var scenario = new ScenarioDocument("s1", "v1", "Scenario", new("Goal", null, null), new(1000, 2, null), null);
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(scenario)); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(root, "scenario.json"), bytes);
        var registry = Path.Combine(root, "registry.json");
        File.WriteAllText(registry, ContractJson.Serialize(new ScenarioRegistryDocument("registry", [new("s1", "v1", new("scenario.json", hash))], ["runs"])));
        var store = RunArtifactStore.Create(Path.Combine(root, "runs"), Limits, Redactor);
        using var document = JsonDocument.Parse(bytes);
        var metadata = new RunArtifactMetadata("player", [], new Dictionary<string, string>(), [new("scenario", document.RootElement)],
            new("s1", "v1", hash, "build", new string('a', 64)), DateTimeOffset.UtcNow);
        await using var capture = new GuaRunArtifacts(store, metadata, Limits, Options(), Redactor);
        var primary = Primary(true); await Confirm(capture, primary);
        Assert.True((await capture.CompleteAsync(Outcome(primary), DateTimeOffset.UtcNow)).Persistence.Saved);
        var index = new DashboardIndex(registry, [Path.Combine(root, "runs")], [root]);
        var snapshot = await index.ReadAsync(); var run = Assert.Single(snapshot.Runs);
        Assert.Equal("Passed", run.State); var detail = Assert.Single(run.Details, x => x.Kind == "Trace");
        Assert.Equal("Saved", detail.State); Assert.NotNull(detail.Report);
        Assert.Contains("Trace", Encoding.UTF8.GetString(index.ReadReport(detail.Report!)));
    }
    public void Dispose()
    { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
