using System.Net;
using System.Net.Sockets;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
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
}
