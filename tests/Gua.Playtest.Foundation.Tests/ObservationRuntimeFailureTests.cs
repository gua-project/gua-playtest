using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Bridge.Tests;
using Gua.Playtest.Cli;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ObservationRuntimeFailureTests
{
    [Fact]
    public async Task Exhausted_real_trace_reference_capacity_cannot_escape_into_goal_evaluation()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "runtime-reference-" + Guid.NewGuid().ToString("N"));
        var limits = new ArtifactLimits(65536, 524288, 64, 16384);
        var redactor = new PersistenceRedactor([], []);
        var store = RunArtifactStore.Create(root, limits, redactor);
        await using var artifacts = new GuaRunArtifacts(store, new("debug", [], new Dictionary<string, string>(), []), limits,
            new GuaTraceOptions { Profile = "debug", MaxArtifactBytes = 65536, MaxMemoryBytes = 65536,
                MaxAttachmentBytes = 32768, MaxEventBytes = 32768 }, redactor);
        Assert.True(artifacts.PreparationPersistence.Saved);
        var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, "capacity");
        for (int i = 0; i < 64; i++) Assert.NotEmpty(artifacts.Observe("capture-" + i, ObservationBoundary.Before,
            step, "gua", "fresh-read", "Available", new("source", "1")));
        Assert.Throws<InvalidOperationException>(() => artifacts.Observe("overflow", ObservationBoundary.Before,
            step, "gua", "fresh-read", "Available", new("source", "1")));
        bool marked = false;
        Assert.False(ObservationRunCommand.CaptureWithoutPrimaryAuthority(() => artifacts.Observe("overflow", ObservationBoundary.Before,
            step, "gua", "fresh-read", "Available", new("source", "1")), () => { artifacts.MarkCaptureFailure(); marked = true; }));
        Assert.True(marked);
        // The owner can continue with the actual valid Goal; no capture exception escaped.
        var primary = new PrimaryResult(ResultStatus.Passed, new(RunReason.GoalSatisfied, RunPhase.Execution, RunOrigin.Condition));
        var outcome = new RunOutcome(primary, [], [], []);
        Assert.Equal(ResultStatus.Passed, outcome.Primary.Status);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Tcp_table_growth_is_bounded_and_preserves_matching_owner(int failure)
    {
        int calls = 0;
        uint Read(IntPtr buffer, ref int bytes)
        {
            calls++;
            if (buffer == IntPtr.Zero) { bytes = 4; return 122; }
            if (calls == 2 || failure != 0)
            { bytes = failure == 2 ? 1048577 : bytes + 24; return 122; }
            Marshal.WriteInt32(buffer, 1);
            Marshal.WriteInt32(buffer, 4 + 4, unchecked((int)0x0100007f));
            Marshal.WriteInt32(buffer, 4 + 8, (ushort)IPAddress.HostToNetworkOrder((short)19441));
            Marshal.WriteInt32(buffer, 4 + 20, 12345); return 0;
        }
        if (failure == 0) Assert.Equal([12345], WindowsBridgeOwner.ListenerOwners(19441, Read));
        else Assert.Throws<InvalidOperationException>(() => WindowsBridgeOwner.ListenerOwners(19441, Read));
        Assert.InRange(calls, 2, 4);
    }

    [Fact]
    public async Task Effective_identity_includes_launch_arguments_and_explicit_save_paths()
    {
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var report = await StaticPathAuthority.Validator([repository]).ValidateFileAsync(Path.Combine(repository, "fixtures/contracts/environment.json"));
        var environment = Assert.IsType<EnvironmentDocument>(report.Document);
        var limits = new ArtifactLimits(65536, 524288, 1000, 65536);
        var redactor = new PersistenceRedactor([], ["password", "token", "authorization", "secretValue"]);
        JsonElement Configuration(string argument, string save) => ObservationRunCommand.EffectiveConfiguration(new string('b', 64),
            new { maxDurationMilliseconds = 3000 }, new("game.exe", "work", [argument],
                new Dictionary<string, string> { ["APPDATA"] = save }), new string('a', 64), redactor, limits);
        string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        var original = Configuration("--headless", "private-a");
        Assert.NotEqual(Hash(original), Hash(Configuration("--other", "private-a")));
        Assert.NotEqual(Hash(original), Hash(Configuration("--headless", "private-b")));
        Assert.Equal(Hash(original), Hash(Configuration("--headless", "private-a")));
        Assert.Equal("--headless", original.GetProperty("arguments")[0].GetString());
        Assert.Equal("private-a", original.GetProperty("childEnvironment").GetProperty("APPDATA").GetString());
        Assert.Throws<InvalidDataException>(() => Configuration("--token=private", "private-a"));
    }

    [Theory]
    [InlineData(true, 11)] [InlineData(false, 4)]
    public async Task Missing_primary_persistence_does_not_replace_the_primary_outcome(bool passed, int exit)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "runtime-completion-" + Guid.NewGuid().ToString("N"));
        var limits = new ArtifactLimits(65536, 524288, 1000, 16384);
        var redactor = new PersistenceRedactor([], []);
        var store = RunArtifactStore.Create(root, limits, redactor);
        await using var artifacts = new GuaRunArtifacts(store, new("debug", [], new Dictionary<string, string>(), []), limits,
            new GuaTraceOptions { Profile = "debug", MaxArtifactBytes = 65536, MaxMemoryBytes = 65536, MaxAttachmentBytes = 32768, MaxEventBytes = 32768 }, redactor);
        var primary = new PrimaryResult(passed ? ResultStatus.Passed : ResultStatus.Aborted,
            new(passed ? RunReason.GoalSatisfied : RunReason.Cancelled, RunPhase.Execution, passed ? RunOrigin.Condition : RunOrigin.User));
        var outcome = new RunOutcome(primary, [], [new(PostProcessingReason.PrimarySnapshotFailed)], []);
        var result = await ObservationRunCommand.CompletePreservingOutcomeAsync(artifacts, outcome);
        Assert.Equal(exit, result.ExitCode(outcome));
        Assert.Equal(PersistenceFailure.InvalidEvidence, result.Persistence.Failure);
        Assert.True(result.Persistence.PostProcessingIncomplete);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "primary.json"));
    }

    private sealed class Clock : IClock
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        public TimeSpan Elapsed => timer.Elapsed;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken token) => new(Task.Delay(delay, token));
    }
    [Fact]
    public async Task Owned_connection_release_interrupts_a_pending_real_transport_reply()
    {
        using var runtime = new GuaRuntime();
        runtime.BeginFrame("test"); runtime.RegisterNode(new("ready", "button", "Ready", new(0, 0, 10, 10))); runtime.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(runtime.StartInspectorBridge(number));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new BridgeFaultProxy($"ws://127.0.0.1:{number}/", (_, _) => false,
            delayReply: (_, _) => { pending.TrySetResult(); return TimeSpan.FromSeconds(10); });
        // Listener-PID attestation is tested separately. HttpListener is OS-owned;
        // this seam tests the same real connection's pending-read cleanup only.
        var connection = new GuaObservationConnection(new(proxy.Endpoint), "test", "Debug", new Clock(), null, null,
            100, 65536, TimeSpan.FromSeconds(15), () => { }, (_, _) => { });
        var reading = connection.IdentifyAsync(CancellationToken.None).AsTask();
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await connection.ReleaseAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<Exception>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await connection.ReleaseAsync(CancellationToken.None));
    }
}
