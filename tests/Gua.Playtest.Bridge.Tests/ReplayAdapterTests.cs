using System.Net;
using System.Net.Sockets;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
using System.Text.Json;
using Gua.Testing.Recording;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

// Real native runtime and public Gua playback; this fixture does not establish engine acceptance.
public sealed class ReplayAdapterTests
{
    private readonly object lifecycle = new();
    private sealed class Lease(object gate) : IDisposable
    {
        private readonly object gate = Enter(gate);
        private static object Enter(object gate) { Monitor.Enter(gate); return gate; }
        public void Dispose() => Monitor.Exit(gate);
    }
    private sealed class Calls(Action? afterSend = null) : IReplayCalls
    {
        public int Sends;
        public ValueTask<T> ReadAsync<T>(Func<T> callback, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(callback()); }
        public ValueTask<T> SendAsync<T>(int index, Func<Action, ReplaySend<T>> callback, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(Sends, index);
            var result = callback(() => { token.ThrowIfCancellationRequested(); Sends++; });
            afterSend?.Invoke(); return ValueTask.FromResult(result.Value);
        }
    }
    private static ReplayBatch Batch(string timing = "conditionSynchronized", string? wait = null, string target = "\"id\":\"buy\"")
        => new(0, 1, "{\"schemaVersion\":1,\"steps\":[{\"action\":\"click\",\"target\":{" + target +
            "},\"relativeMilliseconds\":0,\"preRevision\":1,\"postRevision\":2,\"sensitive\":false" +
            (wait is null ? "" : ",\"waitCondition\":\"" + wait + "\"") + "}]}", timing, 0, TimeSpan.FromSeconds(2));
    private static string Start(GuaRuntime runtime)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.True(runtime.StartInspectorBridge(port)); return runtime.InspectorBridgeUrl;
    }
    private void Ui(GuaRuntime runtime, params string[] ids)
    {
        lock (lifecycle)
        {
            runtime.BeginFrame("shop");
            foreach (var id in ids) runtime.RegisterNode(new(id, "button", "Buy", new(0, 0, 10, 10)));
            runtime.EndFrame();
        }
    }
    private GuaUiReplay Playback(IGuaContext context) => new(context, () => new Lease(lifecycle),
        (_, _) => ReplayCheck.Approved, TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task PublishedPlaybackPreservesWaitAndConfirmsRealNativeAction()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        using var context = new GuaWebSocketContext(Start(runtime)); var replay = Playback(context);
        var batch = Batch(wait: "visible:buy"); Assert.Equal(ReplayCheck.Approved, replay.Check(batch));
        var calls = new Calls(() =>
        {
            lock (lifecycle)
            {
                Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request));
                runtime.EmitActionResult(request, true);
            }
        });
        var receipt = await replay.PlayAsync(batch, calls, default);
        Assert.Equal(ReplayReceiptStatus.Succeeded, receipt.Status); Assert.Equal(1, receipt.CompletedSteps);
        Assert.Equal(1, calls.Sends); Assert.True(receipt.NeutralConfirmed);
    }
    [Fact]
    public void RecordedTimingCannotSilentlyDropExistingWait()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        using var context = new GuaWebSocketContext(Start(runtime));
        Assert.Equal(ReplayCheck.Unsupported, Playback(context).Check(Batch("recorded", "visible:buy")));
    }
    [Fact]
    public async Task MultipleSameSelectorMatchesRejectWithoutRepairOrEnqueue()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy", "another");
        using var context = new GuaWebSocketContext(Start(runtime)); var calls = new Calls();
        await Assert.ThrowsAnyAsync<Exception>(() => Playback(context).PlayAsync(Batch(target: "\"role\":\"button\",\"name\":\"Buy\""), calls, default).AsTask());
        Assert.Equal(0, calls.Sends); Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "buy", out _));
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "another", out _));
    }
    [Fact]
    public async Task SessionResetAfterNativeCompletionDoesNotConfirmOldAction()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy"); var endpoint = Start(runtime);
        using var context = new GuaWebSocketContext(endpoint); using var control = new GuaWebSocketContext(endpoint);
        var calls = new Calls(() =>
        {
            lock (lifecycle)
            {
                Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request));
                runtime.EmitActionResult(request, true); control.Reset();
            }
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Playback(context).PlayAsync(Batch(), calls, default).AsTask());
        Assert.Equal("ReplaySessionChanged", error.Message); Assert.Equal(1, calls.Sends);
    }

    private sealed class TimedHost : IGuaTimedSegmentHost
    {
        public bool OrderedApplication => true;
        public bool ApplicationTimes => false;
        public bool SameTickApplication => false;
        public string? SimulationScope => null;
        public double SimulationMilliseconds => 0;
        public string? ExecutionFailureCode => null;
        public bool IsNeutral => true;
        public GuaTimedSegment? Segment;
        public int Releases, Ends;
        public Exception? SendFault { get; set; }
        public Action? OnSend { get; set; }
        public List<GuaTimedInput> Sent { get; } = [];
        public void Begin(GuaTimedSegment segment) => Segment = segment;
        public ulong Send(GuaTimedInput input, JsonElement? secret, Action boundary)
        {
            boundary(); if (SendFault is { } fault) throw fault;
            Sent.Add(input); OnSend?.Invoke(); return (ulong)Sent.Count;
        }
        public GuaTimedCompletion? Poll(ulong id) => new(true);
        public ulong ReleaseAll() { Releases++; return 100; }
        public void End() => Ends++;
    }
    private static ReplayBatch GameBatch() => new(0, 2,
        """{"schemaVersion":2,"steps":[{"action":"game_input","operation":"key_down","arguments":{"code":"KeyA","leaseMs":5000},"relativeMilliseconds":100,"sensitive":false},{"action":"game_input","operation":"key_up","arguments":{"code":"KeyA"},"relativeMilliseconds":100,"sensitive":false}]}""",
        "recorded", 100, TimeSpan.FromSeconds(2));
    private static GuaReplayTimingPolicy Timing(bool strict = false) => new(1000, 100, 100, GuaSegmentClock.Realtime, strict, strict);

    [Fact]
    public async Task PublicTimedImportKeepsEqualOffsetOrderAndNormalRelease()
    {
        var host = new TimedHost(); var calls = new Calls();
        var replay = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved, Timing());
        Assert.Equal(ReplayCheck.Approved, replay.Check(GameBatch()));
        var receipt = await replay.PlayAsync(GameBatch(), calls, default);
        Assert.Equal(ReplayReceiptStatus.Succeeded, receipt.Status); Assert.Equal(2, receipt.CompletedSteps);
        Assert.Equal("legacy-unknown", host.Segment!.TimingProvenance);
        Assert.Equal(new long[] { 0, 0 }, host.Sent.Select(x => x.OffsetMilliseconds));
        Assert.Equal(new[] { GuaGameInputOperation.Down, GuaGameInputOperation.Up }, host.Sent.Select(x => x.Operation));
        Assert.Equal(1, host.Releases); Assert.Equal(1, host.Ends); Assert.True(receipt.NeutralConfirmed);
    }
    [Fact]
    public void StrictTimingRejectsHostWithoutMeasuredCapability()
    {
        var host = new TimedHost(); var replay = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved, Timing(true));
        Assert.Equal(ReplayCheck.Unsupported, replay.Check(GameBatch()));
        Assert.Null(host.Segment); Assert.Empty(host.Sent);
    }
    [Fact]
    public async Task PublicTimedExceptionRetainsOriginalAndStillRunsFreshSafetyRelease()
    {
        var original = new IOException("host dispatch fault"); var host = new TimedHost { SendFault = original };
        var replay = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved, Timing());
        var receipt = await replay.PlayAsync(GameBatch(), new Calls(), default);
        Assert.Equal(ReplayReceiptStatus.Unconfirmed, receipt.Status); Assert.Same(original, receipt.OriginalException);
        // A dispatch fault without a request ID cannot settle the ordinary request or prove neutral.
        Assert.Equal(1, host.Releases); Assert.Equal(1, host.Ends); Assert.False(receipt.NeutralConfirmed);
    }
    [Fact]
    public async Task CancellationAfterHoldStillRunsIndependentGuaSafetyRelease()
    {
        using var cancellation = new CancellationTokenSource(); var host = new TimedHost { OnSend = cancellation.Cancel };
        var replay = new GuaTimedReplay(host, (_, _) => ReplayCheck.Approved, Timing());
        var receipt = await replay.PlayAsync(GameBatch(), new Calls(), cancellation.Token);
        Assert.NotEqual(ReplayReceiptStatus.Succeeded, receipt.Status);
        Assert.Equal(GuaGameInputOperation.Down, Assert.Single(host.Sent).Operation);
        Assert.Equal(2, host.Releases); Assert.Equal(1, host.Ends); Assert.True(receipt.NeutralConfirmed);
    }
}
