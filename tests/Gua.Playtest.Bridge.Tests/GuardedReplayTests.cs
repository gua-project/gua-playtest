using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class GuardedReplayTests
{
    private static ReplayBatch Batch() => new(0, 1,
        """{"schemaVersion":1,"steps":[{"action":"click","target":{"id":"start"},"relativeMilliseconds":0,"preRevision":1,"postRevision":1,"sensitive":false}]}""",
        "conditionSynchronized", 0, TimeSpan.FromSeconds(2));
    private sealed class Calls(Action? before = null, Action? after = null, Action? boundary = null) : IReplayCalls
    {
        public int Sends;
        public ValueTask<T> ReadAsync<T>(Func<T> callback, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(callback()); }
        public ValueTask<T> SendAsync<T>(int index, Func<Action, ReplaySend<T>> callback, CancellationToken token)
        {
            Assert.Equal(0, index); before?.Invoke();
            var result = callback(() => { token.ThrowIfCancellationRequested(); Sends++; boundary?.Invoke(); });
            after?.Invoke(); return ValueTask.FromResult(result.Value);
        }
    }
    private static string Start(GuaRuntime host)
    {
        host.BeginFrame("title"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10))); host.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number)); return host.InspectorBridgeUrl;
    }
    [Theory]
    [InlineData("recorded")]
    [InlineData("multiple")]
    [InlineData("role")]
    [InlineData("wait")]
    [InlineData("sensitive")]
    [InlineData("offset")]
    [InlineData("focus")]
    [InlineData("modifier")]
    [InlineData("delta")]
    public void Unsupported_inputs_refuse_before_owner_or_send(string mode)
    {
        var batch = Batch(); var json = JsonNode.Parse(batch.RecordingJson)!; var step = json["steps"]![0]!;
        if (mode == "recorded") batch = batch with { Timing = "recorded" };
        if (mode == "multiple") { json["steps"]!.AsArray().Add(step.DeepClone()); batch = batch with { Count = 2 }; }
        if (mode == "role") step["target"] = new JsonObject { ["role"] = "button", ["name"] = "Start" };
        if (mode == "wait") step["waitCondition"] = "visible:start";
        if (mode == "sensitive") { step["sensitive"] = true; step["secretKey"] = "secret"; }
        if (mode == "offset") step["relativeMilliseconds"] = 2000;
        if (mode == "focus") step["target"] = new JsonObject { ["currentFocus"] = true };
        if (mode == "modifier") step["modifiers"] = 1;
        if (mode == "delta") step["deltaX"] = 1;
        batch = batch with { RecordingJson = json.ToJsonString() };
        var replay = new GuaGuardedClickReplay(() => throw new Exception("owner must not be acquired"),
            () => throw new Exception("no unsupported admission"), _ => { }, TimeSpan.FromMilliseconds(5));
        Assert.Equal(ReplayCheck.Unsupported, replay.Check(batch));
    }
    [Fact]
    public void Admission_fault_preserves_original_exception()
    {
        var original = new ArgumentException("owner fault");
        var replay = new GuaGuardedClickReplay(() => throw new Exception(), () => throw original, _ => { }, TimeSpan.FromMilliseconds(5));
        Assert.Same(original, Assert.Throws<ArgumentException>(() => replay.Check(Batch())));
    }
    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    [InlineData("cancel-before")]
    [InlineData("stale")]
    [InlineData("reset-after")]
    public async Task Real_guarded_host_preserves_one_attempt_completion_and_refusal(string mode)
    {
        using var host = new GuaRuntime(); var endpoint = Start(host);
        using var observation = new BridgeObservations(endpoint, GuaObservationProfile.Debug, TimeSpan.FromSeconds(1), 100, 65536);
        using var click = observation.CreateGuardedUiClick();
        using var control = new GuaWebSocketContext(endpoint);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var receipts = new List<GuardedUiReceipt>();
        var calls = new Calls(mode == "cancel-before" ? () => cancel.Cancel() : null, () =>
        {
            if (mode is "success" or "failed" or "reset-after")
            {
                Assert.True(host.TryConsumeAction(GuaActionType.Click, "start", out var request));
                host.EmitActionResult(request, mode != "failed", mode == "failed" ? GuaActionError.InvalidArgument : GuaActionError.None);
                if (mode == "reset-after") control.Reset();
            }
        }, mode == "stale" ? () => control.Reset() : null);
        var replay = new GuaGuardedClickReplay(() => click, () => { },
            receipt => receipts.Add(receipt), TimeSpan.FromMilliseconds(5));
        if (mode == "cancel-before")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.PlayAsync(Batch(), calls, cancel.Token).AsTask());
            Assert.Equal(0, calls.Sends);
        }
        else
        {
            var result = await replay.PlayAsync(Batch(), calls, cancel.Token);
            Assert.Equal(mode == "success" ? ReplayReceiptStatus.Succeeded : mode == "reset-after" ? ReplayReceiptStatus.Unconfirmed : ReplayReceiptStatus.Failed, result.Status);
            Assert.Equal(mode is "success" or "failed" ? 1 : 0, result.CompletedSteps);
            Assert.Equal(1, calls.Sends);
        }
        var sends = calls.Sends;
        Assert.Throws<InvalidOperationException>(() => { _ = replay.PlayAsync(Batch(), calls, default); });
        Assert.Equal(sends, calls.Sends);
        Assert.False(host.TryConsumeAction(GuaActionType.Click, "start", out _));
    }
}
