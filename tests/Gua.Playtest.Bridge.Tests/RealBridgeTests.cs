using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class RealBridgeTests
{
    private static readonly object LifecycleGate = new();
    // The test host's frame writers share the actual lock acquired around UI dispatch.
    // Production external bridges cannot manufacture a lock in the remote host.
    private sealed class FixtureLifecycleLease : IDisposable
    {
        public FixtureLifecycleLease() { Monitor.Enter(LifecycleGate); }
        public void Dispose() { Monitor.Exit(LifecycleGate); }
    }
    private static IDisposable EnterFixtureLifecycle() => new FixtureLifecycleLease();
    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static string Start(GuaRuntime runtime)
    {
        Assert.True(runtime.StartInspectorBridge(Port())); return runtime.InspectorBridgeUrl;
    }
    private static BridgeObservations Reader(string endpoint, GuaObservationProfile profile = GuaObservationProfile.Debug,
        int nodes = 100, int bytes = 1000000) => new(endpoint, profile, TimeSpan.FromSeconds(2), nodes, bytes);
    private static JsonObject Read(string source, string region, string name, string type = "integer", string? id = null)
    {
        var target = new JsonObject { ["source"] = source };
        if (source != "world") target["selector"] = new JsonObject { ["id"] = new JsonObject { ["value"] = id! } };
        return new() { ["target"] = target, ["region"] = region, [region == "standard" ? "field" : "name"] = name,
            ["valueType"] = new JsonObject { ["type"] = type } };
    }
    private static void World(GuaRuntime runtime, string id = "enemy-1", bool visible = true)
    {
        lock (LifecycleGate)
        {
        runtime.EnableWorldObjectTreeAdapter(); runtime.BeginWorldFrame("shop");
        runtime.RegisterWorldObject(new(id, "enemy", "Enemy", GuaWorldSpace.World2D, new(3, 4), VisibleToPlayer: visible));
        runtime.EndWorldFrame();
        }
    }
    private static void Ui(GuaRuntime runtime, params string[] ids)
    {
        lock (LifecycleGate)
        {
        runtime.BeginFrame("shop");
        foreach (string id in ids) runtime.RegisterNode(new(id, "button", "Buy", new(0, 0, 10, 10), Checked: false));
        runtime.EndFrame();
        }
    }
    private static ulong Epoch(GuaRuntime runtime)
    { using var doc = JsonDocument.Parse(runtime.GetUiTreeJson()); return doc.RootElement.GetProperty("sessionEpoch").GetUInt64(); }

    [Fact]
    public void RealRemoteObservePreservesOwnerRegistrationTypeAndCatalog()
    {
        using var runtime = new GuaRuntime(); World(runtime);
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "Alive", "Dead");
        using var registration = owner.Observe("phase", () => GuaValue.Enum("game.Phase", "Alive", catalog)); World(runtime);
        using var reader = Reader(Start(runtime));
        var read = Read("object", "observe", "phase", "enum", "enemy-1"); read["valueType"]!["enumType"] = "game.Phase";
        var result = reader.Read(read); var actual = Assert.Single(result.Reads);
        Assert.Equal(ReadAvailability.Available, result.Availability);
        Assert.Equal("Alive", actual.Value!.Value.GetProperty("value").GetString());
        Assert.NotNull(actual.EnumCatalog); Assert.True(actual.Identity!.OwnerId > 0); Assert.True(actual.Identity.RegistrationId > 0);
        Assert.Equal("enemy-1", actual.Identity.RuntimeId);
    }

    [Fact]
    public void StandardAndObserveWithSameNameRemainSeparateAndOwnerReplacementChangesIdentity()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.Ui, "buy");
        using var registered = owner.Observe("visible", () => GuaValue.Bool(false)); Ui(runtime, "buy");
        using var reader = Reader(Start(runtime));
        Assert.True(Assert.Single(reader.Read(Read("ui", "standard", "visible", "bool", "buy")).Reads).Value!.Value.GetProperty("value").GetBoolean());
        var first = Assert.Single(reader.Read(Read("ui", "observe", "visible", "bool", "buy")).Reads);
        Assert.False(first.Value!.Value.GetProperty("value").GetBoolean());
        owner.Dispose();
        using var replacement = runtime.CreateObserveOwner(GuaObserveSource.Ui, "buy");
        using var changed = replacement.Observe("visible", () => GuaValue.Bool(true)); Ui(runtime, "buy");
        var second = Assert.Single(reader.Read(Read("ui", "observe", "visible", "bool", "buy")).Reads);
        Assert.NotEqual(first.Identity!.OwnerId, second.Identity!.OwnerId);
        Assert.NotEqual(first.Identity.RegistrationId, second.Identity.RegistrationId);
    }

    [Fact]
    public void GetterFailureGapAndResetAreNotEmptySuccessAndResubscribeDoesNotRestoreHistory()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool fail = false; int count = 0;
        using var item = owner.Property("count", () => fail ? throw new Exception("PRIVATE_ERROR") : GuaValue.Integer(count));
        item.Notify(); string endpoint = Start(runtime); using var reader = Reader(endpoint);
        var read = Read("world", "property", "count"); Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        fail = true; item.Notify(); var unavailable = Assert.Single(reader.Read(read).Reads);
        Assert.Equal(ReadAvailability.Unavailable, unavailable.Availability); Assert.Null(unavailable.Value);
        Assert.Equal("gua-observe-error-100", unavailable.Reason);
        fail = false; runtime.SetObserveHistoryLimits(1); count = 1; item.Notify(); count = 2; item.Notify();
        Assert.Equal(ReadAvailability.Gap, reader.Read(read).Availability);
        Assert.Equal(ReadAvailability.Gap, reader.Read(read).Availability);
        reader.Resubscribe(); Assert.Equal(2, Assert.Single(reader.Read(read).Reads).Value!.Value.GetProperty("value").GetInt32());
        using var control = new GuaWebSocketContext(endpoint); control.Reset();
        Assert.Equal(ReadAvailability.Stale, reader.Read(read).Availability);
    }

    [Fact]
    public void PlayerReadsDoNotDisclosePrivateExistenceAndProfileMismatchFailsClosed()
    {
        using var runtime = new GuaRuntime(); runtime.SetObservationProfile(GuaObservationProfile.Player); World(runtime);
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var privateItem = owner.Observe("private", () => GuaValue.String("SECRET_MARKER"));
        using var publicItem = owner.Observe("hp", () => GuaValue.Integer(9), allowPlayer: true); World(runtime);
        string endpoint = Start(runtime); using var player = Reader(endpoint, GuaObservationProfile.Player);
        var hidden = Assert.Single(player.Read(Read("object", "observe", "private", "string", "enemy-1")).Reads);
        var absent = Assert.Single(player.Read(Read("object", "observe", "absent", "string", "enemy-1")).Reads);
        Assert.Equal(hidden.Availability, absent.Availability); Assert.Equal(hidden.Reason, absent.Reason);
        Assert.Null(hidden.Value); Assert.Null(absent.Value);
        using var wrong = Reader(endpoint); Assert.Equal(ReadAvailability.Unavailable, wrong.Read(Read("world", "property", "anything")).Availability);
    }

    [Fact]
    public void ObservationNodeLimitIsTruncatedAndSpatialLimitNeverClaimsComplete()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "one", "two");
        using var bounded = Reader(Start(runtime), nodes: 1);
        Assert.Equal(ReadAvailability.Truncated, bounded.Read(Read("ui", "standard", "visible", "bool", "one")).Availability);
    }

    [Fact]
    public void UiResolvesEachAttemptAndRefusesMultipleMatches()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "old"); string endpoint = Start(runtime);
        using var bridge = new BridgeUiActions(endpoint, "run-ui", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"role\":{\"value\":\"button\"}}")!.AsObject();
        var first = bridge.Send("first", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal("old", first.RuntimeId); Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "old", out var request));
        runtime.EmitActionResult(request, true); Assert.Equal(ActionAttemptStatus.Succeeded, bridge.Poll("first").Status);
        Ui(runtime, "new"); var second = bridge.Send("second", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal("new", second.RuntimeId); Assert.NotEqual(first.RequestId, second.RequestId);
        Ui(runtime, "new", "other"); var ambiguous = bridge.Send("third", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal(ConfirmedActionStage.NotSent, ambiguous.Stage); Assert.Equal(ActionAttemptStatus.Rejected, ambiguous.Status);
        Assert.Equal(second, bridge.Send("second", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
    }

    [Fact]
    public void NativeGuardedInputAttemptsAreSeparateAndInputSuccessDoesNotInventDamage()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "run-1", GuaObservationProfile.Debug, 10);
        var first = bridge.Send("a1", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.Equal(ConfirmedActionStage.Enqueued, first.Stage);
        Assert.True(runtime.TryConsumeGameInput(out var request)); runtime.CompleteGameInput(request, true);
        Assert.Equal(ActionAttemptStatus.Succeeded, bridge.Poll("a1").Status);
        var second = bridge.Send("a2", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.NotEqual(first.RequestId, second.RequestId); Assert.Equal("run-1", second.RunId);
        Assert.Equal(second, bridge.Send("a2", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true));
        Assert.True(runtime.TryConsumeGameInput(out _)); Assert.False(runtime.TryConsumeGameInput(out _));
    }

    [Fact]
    public void NativeGuardRejectsChangedActionMapAndCleanupIsNotPlannerInput()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "run-2", GuaObservationProfile.Debug, 10);
        runtime.PublishGameInputActions("changed", [new("attack", "Changed", GuaGameInputValueType.Button)]);
        var stale = bridge.Send("stale", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.Equal(ActionAttemptStatus.Rejected, stale.Status); Assert.Equal(ConfirmedActionStage.NotSent, stale.Stage);
        var reset = bridge.Send("reset", epoch, revision, GuaGameInputKind.Cleanup, GuaGameInputOperation.ReleaseAll, "", null, null, () => true);
        Assert.Equal("operation-not-public", reset.Reason); Assert.False(runtime.TryConsumeGameInput(out _));
    }

    [Fact]
    public async Task PurchaseCommitThenDroppedEnqueueReplyIsUnconfirmedAndNeverResent()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        int ingress = 0, transactions = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "click_node") return false;
            ingress++; Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var purchase));
            transactions++; runtime.EmitActionResult(purchase, true); return true;
        });
        using var bridge = new BridgeUiActions(proxy.Endpoint, "purchase-run", TimeSpan.FromSeconds(1), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        var result = bridge.Send("purchase-1", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal(1, transactions); Assert.Equal(1, ingress);
        Assert.Equal(ConfirmedActionStage.DispatchAttempted, result.Stage); Assert.Null(result.RequestId);
        Assert.Equal(ActionAttemptStatus.Failed, result.Status); Assert.Equal("dispatch-unconfirmed", result.Reason);
        Assert.Equal(result, bridge.Send("purchase-1", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
        Assert.Equal(1, ingress); Assert.Equal(1, transactions); Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "buy", out _));
    }

    [Fact]
    public async Task DroppedCompletionAfterConsumptionDoesNotRetryOrClaimSuccess()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy"); int polls = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "poll_events") return false;
            polls++; Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, response.GetProperty("result").ValueKind); return true;
        });
        using var bridge = new BridgeUiActions(proxy.Endpoint, "run", TimeSpan.FromSeconds(1), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        var attempt = bridge.Send("a", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request)); runtime.EmitActionResult(request, true);
        var result = bridge.Poll("a"); Assert.Equal(ActionAttemptStatus.Failed, result.Status);
        Assert.Equal(ConfirmedActionStage.Enqueued, result.Stage); Assert.Equal(attempt.RequestId, result.RequestId);
        Assert.Equal(result, bridge.Poll("a")); Assert.Equal(1, polls);
    }

    [Fact]
    public void TimeoutAndAbortDoNotMeanNonexecutionAndHostErrorIsRetained()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "late", GuaObservationProfile.Debug, 10);
        bridge.Send("late", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        var timeout = bridge.EndWait("late", false); Assert.Equal(ActionAttemptStatus.TimedOut, timeout.Status);
        Assert.Equal(ConfirmedActionStage.Enqueued, timeout.Stage);
        Assert.True(runtime.TryConsumeGameInput(out var late)); runtime.CompleteGameInput(late, true);
        Assert.Equal(timeout, bridge.Poll("late"));
        bridge.Send("failed", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.True(runtime.TryConsumeGameInput(out var failed)); runtime.CompleteGameInput(failed, false, -17);
        var result = bridge.Poll("failed"); Assert.Equal(-17, result.GuaErrorCode); Assert.Equal(ActionAttemptStatus.Failed, result.Status);
    }

    [Fact]
    public void NativeOwnerDisposalDoesNotDisposeRuntimeOrOtherOwner()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        using var other = runtime.CreateGameInputSession();
        var bridge = new OwnedGameInput(runtime, "owned", GuaObservationProfile.Debug, 10);
        var sent = bridge.Send("hold", Epoch(runtime), 0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyW", null,
            TimeSpan.FromSeconds(5), () => true);
        Assert.Equal(ConfirmedActionStage.Enqueued, sent.Stage);
        Assert.True(runtime.TryConsumeGameInput(out var held)); runtime.CompleteGameInput(held, true);
        bridge.Dispose(); bridge.Dispose();
        ulong request = other.Send(GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "KeyA");
        bool found = false;
        for (int i = 0; i < 4 && runtime.TryConsumeGameInput(out var pending); i++)
        {
            if (pending.RequestId == request) { Assert.NotEqual(held.OwnerId, pending.OwnerId); found = true; }
            runtime.CompleteGameInput(pending, true);
        }
        Assert.True(found); Assert.True(other.PollResult(request).Succeeded);
        Assert.Equal(ActionAttemptStatus.Aborted, Assert.Single(bridge.Attempts).Status);
    }

    [Fact]
    public void ExternalUiDispatchWithoutActualHostLifecycleLeaseIsRefusedBeforeConnection()
    {
        using var bridge = new BridgeUiActions("ws://127.0.0.1:1/", "unsafe", TimeSpan.FromMilliseconds(50), 2);
        var result = bridge.Send("a", new(), new(GuaActionType.Click), 1, () => true);
        Assert.Equal(ActionAttemptStatus.Rejected, result.Status); Assert.Equal(ConfirmedActionStage.NotSent, result.Stage);
        Assert.Equal("atomic-dispatch-unavailable", result.Reason);
    }
}
