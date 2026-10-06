using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class RealBridgeTests
{
    [Fact]
    public async Task FaultProxyDisposalJoinsCancelledUnacceptedConnection()
    {
        var proxy = new BridgeFaultProxy("ws://127.0.0.1:1/", (_, _) => false);
        await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FaultProxyDisposalPreservesUnexpectedForwardingFault()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        var expected = new InvalidOperationException("Injected forwarding fault.");
        var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => throw expected);
        using var reader = Reader(proxy.Endpoint);
        reader.Read(Read("ui", "standard", "visible", "bool", "buy"));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.DisposeAsync().AsTask());
        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionResetAfterUiHostCompletionKeepsUnknownCompletionPending(bool cancelled)
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy"); string endpoint = Start(runtime); int polls = 0;
        await using var proxy = new BridgeFaultProxy(endpoint, (command, _) =>
        { if (command.GetProperty("type").GetString() == "poll_events") polls++; return false; });
        using var bridge = new BridgeUiActions(proxy.Endpoint, "reset", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        ulong epoch = Epoch(runtime); var sent = bridge.Send("once", selector, new(GuaActionType.Click), epoch, () => true);
        Assert.Equal(ConfirmedActionStage.Enqueued, sent.Stage);
        Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request)); runtime.EmitActionResult(request, true);
        using var control = new GuaWebSocketContext(endpoint); control.Reset();
        var pending = bridge.Poll("once");
        Assert.Equal(ActionAttemptStatus.Pending, pending.Status); Assert.Equal(ConfirmedActionStage.Enqueued, pending.Stage);
        Assert.Equal(sent.RequestId, pending.RequestId); Assert.Equal("stale-session-unconfirmed", pending.Reason);
        Assert.Equal(pending, bridge.Poll("once"));
        Assert.Equal(pending, bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
        Assert.Equal(0, polls); Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "buy", out _));
        var ended = bridge.EndWait("once", cancelled);
        Assert.Equal(cancelled ? ActionAttemptStatus.Aborted : ActionAttemptStatus.TimedOut, ended.Status);
        Assert.Equal(sent.RequestId, ended.RequestId); Assert.Equal(ConfirmedActionStage.Enqueued, ended.Stage);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("sessionEpoch")]
    public async Task OversizedSchemaValidUiTreeMetadataRejectsBeforeActionEnqueue(string counter)
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy"); int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "get_ui_tree") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject();
            root["result"]![counter] = JsonNode.Parse("18446744073709551616"); injected++;
            Assert.True(GuaDistribution.ValidateJson("ui-tree.schema.json", root["result"]!.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var bridge = new BridgeUiActions(proxy.Endpoint, "oversized", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        var result = bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal(1, injected); Assert.Equal(ActionAttemptStatus.Rejected, result.Status);
        Assert.Equal(ConfirmedActionStage.NotSent, result.Stage); Assert.Equal("preflight-rejected", result.Reason);
        Assert.Equal(result, bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "buy", out _));
    }

    [Theory]
    [InlineData("exact", "Buy")]
    [InlineData("contains", "uy")]
    [InlineData("regex", "^B.y$")]
    public void CurrentUiTreeUsesPublishedSelectorSemanticsIncludingScope(string match, string pattern)
    {
        using var runtime = new GuaRuntime(); runtime.BeginFrame("shop");
        runtime.RegisterNode(new("parent", "panel", "Panel", new(0, 0, 10, 10)));
        runtime.RegisterNode(new("child", "button", "Buy", new(0, 0, 10, 10), ParentId: "parent")); runtime.EndFrame();
        using var reader = Reader(Start(runtime)); var read = Read("ui", "standard", "visible", "bool", "child");
        read["target"]!["selector"] = new JsonObject { ["name"] = new JsonObject { ["value"] = pattern, ["match"] = match },
            ["scope"] = new JsonObject { ["parentId"] = "parent", ["directChild"] = true }, ["visible"] = true, ["enabled"] = true };
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
    }

    [Fact]
    public void CallerCancellationBeforeUiDispatchIsAbortedAndNotSent()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy");
        using var bridge = new BridgeUiActions(Start(runtime), "cancel", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        var result = bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true, cancellation.Token);
        Assert.Equal(ConfirmedActionStage.NotSent, result.Stage); Assert.Equal(ActionAttemptStatus.Aborted, result.Status);
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "buy", out _));
    }
    [Theory]
    [InlineData("standard")]
    [InlineData("observe")]
    [InlineData("action")]
    public async Task CachedUiQueryCannotMatchChangedNodeWithSameId(string operation)
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "one");
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.Ui, "one");
        using var observed = owner.Observe("count", () => GuaValue.Integer(1)); Ui(runtime, "one");
        string? cached = null; bool armed = false; int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "query_nodes") return null;
            if (!armed) { cached = response.GetProperty("result").GetRawText(); return null; }
            var root = JsonNode.Parse(response.GetRawText())!.AsObject();
            Assert.Empty(root["result"]!["matches"]!.AsArray()); root["result"] = JsonNode.Parse(cached!); injected++;
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        var selector = JsonNode.Parse("{\"role\":{\"value\":\"button\"},\"name\":{\"value\":\"Buy\"}}")!.AsObject();
        using var reader = operation == "action" ? null : Reader(proxy.Endpoint);
        using var actions = operation == "action" ? new BridgeUiActions(proxy.Endpoint, "cached", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle) : null;
        var read = Read("ui", operation == "observe" ? "observe" : "standard", operation == "observe" ? "count" : "visible", operation == "observe" ? "integer" : "bool", "one");
        read["target"]!["selector"] = selector.DeepClone();
        if (reader is not null) Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        else { Assert.Equal(ConfirmedActionStage.Enqueued, actions!.Send("first", selector, new(GuaActionType.Click), Epoch(runtime), () => true).Stage); Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "one", out _)); }
        runtime.BeginFrame("shop"); runtime.RegisterNode(new("one", "text", "Other", new(0, 0, 10, 10))); runtime.EndFrame(); armed = true;
        if (reader is not null) { var result = reader.Read(read); Assert.Equal(ReadAvailability.Stale, result.Availability); Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes); }
        else { var result = actions!.Send("second", selector, new(GuaActionType.Click), Epoch(runtime), () => true); Assert.Equal(ConfirmedActionStage.NotSent, result.Stage); Assert.Equal(ActionAttemptStatus.Rejected, result.Status); Assert.Equal("stale-query", result.Reason); Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "one", out _)); }
        Assert.Equal(1, injected);
    }

    [Fact]
    public async Task ActualUiReceiptTimeoutRemainsPendingUntilEndWait()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "buy"); int committed = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, delayReply: (command, _) =>
        {
            if (command.GetProperty("type").GetString() != "click_node") return TimeSpan.Zero;
            Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "buy", out var request)); committed++; runtime.EmitActionResult(request, true);
            return TimeSpan.FromSeconds(1);
        });
        using var bridge = new BridgeUiActions(proxy.Endpoint, "timeout", TimeSpan.FromMilliseconds(250), 10, EnterFixtureLifecycle);
        var selector = JsonNode.Parse("{\"id\":{\"value\":\"buy\"}}")!.AsObject();
        var result = bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal(1, committed); Assert.Equal(ConfirmedActionStage.DispatchAttempted, result.Stage); Assert.Equal(ActionAttemptStatus.Pending, result.Status);
        Assert.Equal(result, bridge.Send("once", selector, new(GuaActionType.Click), Epoch(runtime), () => true)); Assert.Equal(result, bridge.Poll("once"));
        Assert.Equal(ActionAttemptStatus.TimedOut, bridge.EndWait("once", false).Status); Assert.Equal(1, committed);
    }

    [Theory]
    [InlineData("sequence", false)] [InlineData("revision", false)] [InlineData("uiFrame", false)]
    [InlineData("uiRevision", false)] [InlineData("worldFrame", false)] [InlineData("worldRevision", false)]
    [InlineData("sequence", true)] [InlineData("revision", true)] [InlineData("uiFrame", true)]
    [InlineData("uiRevision", true)] [InlineData("worldFrame", true)] [InlineData("worldRevision", true)]
    public async Task EventCountersCannotExceedPollOrRegress(string counter, bool regress)
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "one"); World(runtime);
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.World); int value = 0;
        using var property = owner.Property("count", () => GuaValue.Integer(value)); property.Notify(); bool armed = false; int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (!armed || command.GetProperty("type").GetString() != "poll_observations") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var transport = root["result"]!.AsObject(); var document = transport["document"]!;
            var events = document["events"]!.AsArray(); Assert.Equal(2, events.Count);
            if (regress) { Assert.True(document[counter]!.GetValue<ulong>() > 0); events[0]![counter] = document[counter]!.DeepClone(); events[1]![counter] = 0UL; }
            else events[0]![counter] = document[counter]!.GetValue<ulong>() + 1;
            Assert.True(GuaDistribution.ValidateJson("observe-transport-v1.schema.json", transport.ToJsonString())); injected++;
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "count"); Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        value++; property.Notify(); value++; property.Notify(); armed = true;
        var result = reader.Read(read); Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Unavailable, result.Availability); Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }
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
        var approved = EnumCatalogSnapshot.Create(JsonNode.Parse("{\"schemaVersion\":1,\"enums\":[{\"enumType\":\"game.Phase\",\"members\":[\"Alive\",\"Dead\"]}]}")!.AsObject());
        var assertion = new JsonObject { ["kind"] = "assertion", ["quantifier"] = "one", ["read"] = read.DeepClone(), ["operator"] = "equals",
            ["expected"] = new JsonObject { ["type"] = "enum", ["enumType"] = "game.Phase", ["value"] = "Alive" } };
        var comparison = PreparedAssertion.Create(assertion, new(10, 1000), approved);
        var observedCatalog = EnumCatalogSnapshot.Create(JsonNode.Parse(actual.EnumCatalog!.Value.GetRawText())!.AsObject());
        var evaluated = comparison.EvaluateJson(actual.Value.Value.GetRawText(), observedCatalog);
        Assert.Equal(TruthValue.True, evaluated.Truth); Assert.Equal(EvaluationError.None, evaluated.Error);
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
    public void ObservationNodeLimitIsTruncatedRatherThanEmptySuccess()
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
        Assert.Equal(ActionAttemptStatus.Pending, result.Status); Assert.Equal("dispatch-unconfirmed", result.Reason);
        Assert.Equal(result, bridge.Send("purchase-1", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
        var timeout = bridge.EndWait("purchase-1", false);
        Assert.Equal(ActionAttemptStatus.TimedOut, timeout.Status); Assert.Equal(ConfirmedActionStage.DispatchAttempted, timeout.Stage);
        Assert.Equal(timeout, bridge.Send("purchase-1", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
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
        var result = bridge.Poll("a"); Assert.Equal(ActionAttemptStatus.Pending, result.Status);
        Assert.Equal(ConfirmedActionStage.Enqueued, result.Stage); Assert.Equal(attempt.RequestId, result.RequestId);
        Assert.Equal(result, bridge.Poll("a")); Assert.Equal(1, polls);
        var timeout = bridge.EndWait("a", false); Assert.Equal(ActionAttemptStatus.TimedOut, timeout.Status);
        Assert.Equal(ConfirmedActionStage.Enqueued, timeout.Stage); Assert.Equal(attempt.RequestId, timeout.RequestId);
        Assert.Equal(timeout, bridge.Poll("a")); Assert.Equal(1, polls);
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

    [Fact]
    public void ResetAndResubscribeReResolveNewEpochAndKeepNewRegistrationIdentity()
    {
        using var runtime = new GuaRuntime(); World(runtime);
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var item = owner.Observe("hp", () => GuaValue.Integer(9)); World(runtime);
        string endpoint = Start(runtime); using var reader = Reader(endpoint);
        var read = Read("object", "observe", "hp", id: "enemy-1");
        var original = Assert.Single(reader.Read(read).Reads);
        using var control = new GuaWebSocketContext(endpoint); control.Reset(); World(runtime);
        using var replacement = runtime.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var second = replacement.Observe("hp", () => GuaValue.Integer(3)); World(runtime);
        Assert.Equal(ReadAvailability.Stale, reader.Read(read).Availability);
        reader.Resubscribe(); var current = Assert.Single(reader.Read(read).Reads);
        Assert.Equal(ReadAvailability.Available, current.Availability);
        Assert.NotEqual(original.Identity!.SessionEpoch, current.Identity!.SessionEpoch);
        Assert.NotEqual(original.Identity.RegistrationId, current.Identity.RegistrationId);
        Assert.Equal(3, current.Value!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public void NativeGuardRechecksMapAtConsumptionAfterSuccessfulEnqueue()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "race", GuaObservationProfile.Debug, 10);
        var sent = bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.Equal(ConfirmedActionStage.Enqueued, sent.Stage);
        runtime.PublishGameInputActions("changed", [new("attack", "Changed", GuaGameInputValueType.Button)]);
        Assert.False(runtime.TryConsumeGameInput(out _));
        var result = bridge.Poll("a"); Assert.Equal(ActionAttemptStatus.Failed, result.Status);
        Assert.Equal(ConfirmedActionStage.HostCompleted, result.Stage); Assert.NotEqual(0, result.GuaErrorCode);
    }

    [Fact]
    public void ValueTypeChangeNeverReturnsOldValue()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool change = false; using var item = owner.Property("phase", () => change ? GuaValue.String("two") : GuaValue.Integer(1));
        item.Notify(); using var reader = Reader(Start(runtime)); var read = Read("world", "property", "phase");
        Assert.Equal(1, Assert.Single(reader.Read(read).Reads).Value!.Value.GetProperty("value").GetInt32());
        change = true; item.Notify(); var result = Assert.Single(reader.Read(read).Reads);
        Assert.Equal(ReadAvailability.Unavailable, result.Availability); Assert.Null(result.Value);
        Assert.Equal("value-type-changed", result.Reason);
    }

    [Fact]
    public void BatchPreservesIntermediateChangesForEveryActiveReadEvenWhenFinalValueReturns()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        int first = 0, second = 0;
        using var one = owner.Property("first", () => GuaValue.Integer(first));
        using var two = owner.Property("second", () => GuaValue.Integer(second));
        one.Notify(); two.Notify(); using var reader = Reader(Start(runtime));
        var a = Read("world", "property", "first"); var b = Read("world", "property", "second");
        var initial = reader.ReadBatch([a, b]); Assert.All(initial, r => Assert.Equal(ReadAvailability.Available, r.Availability));
        first = 1; one.Notify(); second = 2; two.Notify(); first = 0; one.Notify();
        var result = reader.ReadBatch([a, b]);
        Assert.Equal(0, Assert.Single(result[0].Reads).Value!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, result[0].Changes!.Count);
        Assert.Equal(1, result[0].Changes![0].Event.GetProperty("after").GetProperty("value").GetInt32());
        Assert.Equal(0, result[0].Changes![1].Event.GetProperty("after").GetProperty("value").GetInt32());
        Assert.Equal(2, Assert.Single(result[1].Changes!).Event.GetProperty("after").GetProperty("value").GetInt32());
        Assert.All(reader.ReadBatch([a, b]), r => Assert.Empty(r.Changes!));
    }

    [Fact]
    public void StandardTagsPreserveNonemptyAndEmptyStringListValues()
    {
        using var runtime = new GuaRuntime(); runtime.EnableWorldObjectTreeAdapter();
        runtime.BeginWorldFrame("tags");
        runtime.RegisterWorldObject(new("tagged", "item", "Tagged", GuaWorldSpace.World2D, new(0, 0),
            VisibleToPlayer: true, Tags: ["rare", "quest"]));
        runtime.RegisterWorldObject(new("empty", "item", "Empty", GuaWorldSpace.World2D, new(1, 0),
            VisibleToPlayer: true, Tags: []));
        runtime.EndWorldFrame(); using var reader = Reader(Start(runtime));
        var tagged = Read("object", "standard", "tags", "list", "tagged"); tagged["valueType"]!["elementType"] = "string";
        var empty = Read("object", "standard", "tags", "list", "empty"); empty["valueType"]!["elementType"] = "string";
        var result = reader.ReadBatch([tagged, empty]);
        Assert.All(result, r => Assert.Equal(ReadAvailability.Available, r.Availability));
        var list = Assert.Single(result[0].Reads).Value!.Value;
        Assert.Equal("list", list.GetProperty("type").GetString()); Assert.Equal("string", list.GetProperty("elementType").GetString());
        Assert.Equal(new[] { "rare", "quest" }, list.GetProperty("value").EnumerateArray().Select(v => v.GetString()));
        var noTags = Assert.Single(result[1].Reads).Value!.Value;
        Assert.Equal("list", noTags.GetProperty("type").GetString()); Assert.Equal("string", noTags.GetProperty("elementType").GetString());
        Assert.Empty(noTags.GetProperty("value").EnumerateArray());
    }

    [Fact]
    public void InvalidUiSelectorIsRejectedBeforeDispatchAndNeverRetried()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "button");
        using var bridge = new BridgeUiActions(Start(runtime), "invalid-selector", TimeSpan.FromSeconds(2), 10, EnterFixtureLifecycle);
        var selector = new JsonObject { ["unsupported"] = true };
        var result = bridge.Send("invalid", selector, new(GuaActionType.Click), Epoch(runtime), () => true);
        Assert.Equal(ActionAttemptStatus.Rejected, result.Status); Assert.Equal(ConfirmedActionStage.NotSent, result.Stage);
        Assert.Equal("preflight-rejected", result.Reason);
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "button", out _));
        Assert.Equal(result, bridge.Send("invalid", selector, new(GuaActionType.Click), Epoch(runtime), () => true));
    }

    [Fact]
    public void IntermediateTypeChangeCannotBeHiddenByRestoredFinalType()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool changed = false;
        using var property = owner.Property("phase", () => changed ? GuaValue.String("temporary") : GuaValue.Integer(1));
        property.Notify(); using var reader = Reader(Start(runtime)); var read = Read("world", "property", "phase");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        changed = true; property.Notify(); changed = false; property.Notify();
        var result = reader.Read(read);
        Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Equal("value-type-changed", Assert.Single(result.Reads).Reason);
        Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }

    [Fact]
    public void PreviousAvailableEventValueMustMatchTheDeclaredReadType()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool changed = false;
        using var property = owner.Property("phase", () => changed ? GuaValue.String("now") : GuaValue.Integer(1));
        property.Notify(); using var reader = Reader(Start(runtime));
        Assert.Equal(ReadAvailability.Available, reader.Read(Read("world", "property", "phase")).Availability);
        changed = true; property.Notify();
        var result = reader.Read(Read("world", "property", "phase", "string"));
        Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Equal("value-type-changed", Assert.Single(result.Reads).Reason);
        Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }

    [Fact]
    public async Task SnapshotNewerThanPollCannotHideAnIntermediateViolation()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool value = true, armed = false; int injected = 0;
        using var property = owner.Property("ready", () => GuaValue.Bool(value)); property.Notify();
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, response) =>
        {
            if (armed && command.GetProperty("type").GetString() == "poll_observations")
            {
                Assert.True(response.GetProperty("ok").GetBoolean());
                value = false; property.Notify(); value = true; property.Notify(); armed = false; injected++;
            }
            return false;
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "ready", "bool");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); armed = true;
        var stale = reader.Read(read); Assert.Equal(1, injected);
        Assert.Equal(ReadAvailability.Stale, stale.Availability); Assert.Equal("changed-since-poll", Assert.Single(stale.Reads).Reason);
        Assert.Null(Assert.Single(stale.Reads).Value); Assert.Null(stale.Changes);
        var next = reader.Read(read); Assert.Equal(ReadAvailability.Available, next.Availability);
        Assert.Equal(2, next.Changes!.Count);
        Assert.False(next.Changes[0].Event.GetProperty("after").GetProperty("value").GetBoolean());
    }

    [Fact]
    public void DuplicateSelectorsShareOneBatchNodeBudget()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "one", "two"); using var reader = Reader(Start(runtime), nodes: 4);
        var read = Read("ui", "standard", "visible", "bool", "one");
        read["target"]!["selector"] = new JsonObject { ["role"] = new JsonObject { ["value"] = "button" } };
        var result = reader.ReadBatch([read, read, read]);
        Assert.All(result, item => Assert.Equal(ReadAvailability.Truncated, item.Availability));
        Assert.All(result, item => Assert.Null(Assert.Single(item.Reads).Value));
    }

    [Fact]
    public void DuplicateChangesShareOneBatchNodeBudget()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        int value = 0; using var property = owner.Property("count", () => GuaValue.Integer(value)); property.Notify();
        using var reader = Reader(Start(runtime), nodes: 4); var read = Read("world", "property", "count");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); value++; property.Notify();
        Assert.All(reader.ReadBatch([read, read, read]), item => Assert.Equal(ReadAvailability.Truncated, item.Availability));
    }

    [Fact]
    public void DuplicateValuesShareOneBatchByteBudget()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        using var property = owner.Property("text", () => GuaValue.String(new string('x', 4000))); property.Notify();
        using var reader = Reader(Start(runtime), bytes: 10000); var read = Read("world", "property", "text", "string");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        Assert.All(reader.ReadBatch([read, read, read]), item => Assert.Equal(ReadAvailability.Truncated, item.Availability));
    }

    [Fact]
    public async Task CompliantUiTreeWithoutOptionalEpochStillReadsStandardFields()
    {
        using var runtime = new GuaRuntime(); Ui(runtime, "one"); int omitted = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "get_ui_tree") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var tree = root["result"]!.AsObject();
            Assert.True(tree.Remove("sessionEpoch")); omitted++;
            Assert.True(GuaDistribution.ValidateJson("ui-tree.schema.json", tree.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var result = reader.Read(Read("ui", "standard", "visible", "bool", "one"));
        Assert.Equal(1, omitted); Assert.Equal(ReadAvailability.Available, result.Availability);
        Assert.True(Assert.Single(result.Reads).Value!.Value.GetProperty("value").GetBoolean());
    }

    [Theory]
    [InlineData("enum", "valid")]
    [InlineData("list", "valid")]
    [InlineData("set", "valid")]
    [InlineData("enum", "missing")]
    [InlineData("list", "missing")]
    [InlineData("set", "missing")]
    [InlineData("enum", "identity")]
    [InlineData("list", "identity")]
    [InlineData("set", "identity")]
    [InlineData("enum", "members")]
    [InlineData("list", "members")]
    [InlineData("set", "members")]
    public async Task EnumSnapshotRequiresItsMatchingMembershipCatalog(string kind, string fault)
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "Alive", "Dead");
        string json = kind == "enum" ? "{\"type\":\"enum\",\"enumType\":\"game.Phase\",\"value\":\"Alive\"}" :
            "{\"type\":\"" + kind + "\",\"elementType\":\"enum\",\"enumType\":\"game.Phase\",\"value\":[\"Alive\"]}";
        using var property = owner.Property("phase", () => GuaValue.FromJson(json, catalog)); property.Notify(); int rewrites = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (fault == "valid" || command.GetProperty("type").GetString() != "get_observe_snapshot") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var transport = root["result"]!.AsObject();
            CorruptCatalog(transport["catalogs"]![0]!.AsObject(), "value", fault); rewrites++;
            Assert.True(GuaDistribution.ValidateJson("observe-transport-v1.schema.json", transport.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "phase", kind);
        read["valueType"]!["enumType"] = "game.Phase"; if (kind != "enum") read["valueType"]!["elementType"] = "enum";
        var result = reader.Read(read);
        Assert.Equal(fault == "valid" ? ReadAvailability.Available : ReadAvailability.Unavailable, result.Availability);
        if (fault != "valid") { Assert.Equal(1, rewrites); Assert.Equal("enum-catalog-unavailable", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value); }
    }

    [Theory]
    [InlineData("before", "missing")]
    [InlineData("after", "missing")]
    [InlineData("before", "identity")]
    [InlineData("after", "identity")]
    [InlineData("before", "members")]
    [InlineData("after", "members")]
    public async Task EnumChangeRequiresBothPairedMembershipCatalogs(string side, string fault)
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "Alive", "Dead");
        string member = "Alive"; using var property = owner.Property("phase", () => GuaValue.Enum("game.Phase", member, catalog)); property.Notify();
        bool armed = false; int rewrites = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (!armed || command.GetProperty("type").GetString() != "poll_observations") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var transport = root["result"]!.AsObject();
            CorruptCatalog(transport["catalogs"]![0]!.AsObject(), side, fault); rewrites++;
            Assert.True(GuaDistribution.ValidateJson("observe-transport-v1.schema.json", transport.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "phase", "enum"); read["valueType"]!["enumType"] = "game.Phase";
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); armed = true; member = "Dead"; property.Notify();
        var result = reader.Read(read); Assert.Equal(1, rewrites); Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Equal("enum-catalog-unavailable", Assert.Single(result.Reads).Reason); Assert.Null(result.Changes);
    }

    private static void CorruptCatalog(JsonObject catalogs, string side, string fault)
    {
        if (fault == "missing") { Assert.True(catalogs.Remove(side)); return; }
        var definition = catalogs[side]!["enums"]![0]!;
        if (fault == "identity") definition["enumType"] = "game.Other";
        else definition["members"] = new JsonArray("Unknown");
    }

    [Fact]
    public async Task WorldTreeWithoutRequiredEpochNeverProducesAnAvailableRead()
    {
        using var runtime = new GuaRuntime(); World(runtime); int omitted = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "get_world_object_tree") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var tree = root["result"]!.AsObject();
            Assert.True(tree.Remove("sessionEpoch")); omitted++;
            Assert.False(GuaDistribution.ValidateJson("world-object-tree.schema.json", tree.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var result = reader.Read(Read("object", "standard", "label", "string", "enemy-1"));
        Assert.Equal(1, omitted); Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }

    [Theory]
    [InlineData("sourceId")]
    [InlineData("sessionEpoch")]
    [InlineData("profile")]
    public async Task PlayerReadRefusesSchemaValidEventWithForeignIdentity(string field)
    {
        using var runtime = new GuaRuntime(); runtime.SetObservationProfile(GuaObservationProfile.Player);
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        int value = 0; using var property = owner.Property("count", () => GuaValue.Integer(value), allowPlayer: true); property.Notify();
        bool armed = false; int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (!armed || command.GetProperty("type").GetString() != "poll_observations") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var transport = root["result"]!.AsObject();
            var change = transport["document"]!["events"]![0]!;
            if (field == "sourceId") change[field] = "different-host";
            else if (field == "sessionEpoch") change[field] = change[field]!.GetValue<ulong>() + 1;
            else change[field] = "debug";
            injected++; Assert.True(GuaDistribution.ValidateJson("observe-transport-v1.schema.json", transport.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint, GuaObservationProfile.Player); var read = Read("world", "property", "count");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); value++; property.Notify(); armed = true;
        var result = reader.Read(read); Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("object")]
    public async Task CachedTreeFromTheSameEpochCannotReturnStaleStandardValues(string source)
    {
        using var runtime = new GuaRuntime(); string cached;
        if (source == "ui")
        {
            Ui(runtime, "one"); cached = runtime.GetUiTreeJson();
            runtime.BeginFrame("shop"); runtime.RegisterNode(new("one", "button", "Buy", new(0, 0, 10, 10), Checked: true)); runtime.EndFrame();
        }
        else
        {
            World(runtime, "one"); cached = runtime.GetWorldObjectTreeJson();
            runtime.BeginWorldFrame("shop"); runtime.RegisterWorldObject(new("one", "enemy", "New", GuaWorldSpace.World2D, new(3, 4), VisibleToPlayer: true)); runtime.EndWorldFrame();
        }
        int delivered = 0; string commandType = source == "ui" ? "get_ui_tree" : "get_world_object_tree";
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != commandType) return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var old = JsonNode.Parse(cached)!.AsObject();
            Assert.True(root["result"]!["revision"]!.GetValue<ulong>() > old["revision"]!.GetValue<ulong>());
            Assert.Equal(root["result"]!["sessionEpoch"]!.GetValue<ulong>(), old["sessionEpoch"]!.GetValue<ulong>());
            Assert.True(GuaDistribution.ValidateJson(source == "ui" ? "ui-tree.schema.json" : "world-object-tree.schema.json", old.ToJsonString()));
            root["result"] = old; delivered++; return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint);
        var read = Read(source, "standard", source == "ui" ? "state.checked" : "label", source == "ui" ? "bool" : "string", "one");
        var result = reader.Read(read); Assert.Equal(1, delivered); Assert.Equal(ReadAvailability.Stale, result.Availability);
        Assert.Equal("stale-tree", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value);
    }

    [Theory]
    [InlineData("snapshot", "sessionEpoch")]
    [InlineData("snapshot", "revision")]
    [InlineData("snapshot", "worldFrame")]
    [InlineData("snapshot", "ownerId")]
    [InlineData("snapshot", "registrationId")]
    [InlineData("snapshot", "sequence")]
    [InlineData("snapshot", "uiFrame")]
    [InlineData("snapshot", "uiRevision")]
    [InlineData("snapshot", "worldRevision")]
    [InlineData("subscribe", "sessionEpoch")]
    [InlineData("poll", "sessionEpoch")]
    [InlineData("poll", "sequence")]
    [InlineData("poll", "revision")]
    [InlineData("poll", "uiFrame")]
    [InlineData("poll", "uiRevision")]
    [InlineData("poll", "worldFrame")]
    [InlineData("poll", "worldRevision")]
    [InlineData("poll", "ownerId")]
    [InlineData("poll", "registrationId")]
    public async Task SchemaValidOversizedCountersBecomeUnavailable(string mode, string field)
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        int value = 0; using var property = owner.Property("count", () => GuaValue.Integer(value)); property.Notify();
        bool armed = mode != "poll"; int injected = 0;
        string commandType = mode == "snapshot" ? "get_observe_snapshot" : mode == "subscribe" ? "subscribe_observations" : "poll_observations";
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (!armed || command.GetProperty("type").GetString() != commandType) return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject();
            var transport = (mode == "subscribe" ? root["result"]!["snapshot"] : root["result"])!.AsObject();
            JsonNode destination = mode == "poll" ? transport["document"]!["events"]![0]! :
                field is "ownerId" or "registrationId" ? transport["document"]!["entries"]![0]! : transport["document"]!;
            destination[field] = JsonNode.Parse("18446744073709551616"); injected++;
            Assert.True(GuaDistribution.ValidateJson("observe-transport-v1.schema.json", transport.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "count");
        if (mode == "poll") { Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); value++; property.Notify(); armed = true; }
        var result = reader.Read(read); Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
    }

    [Theory]
    [InlineData("ui", false)]
    [InlineData("ui", true)]
    [InlineData("object", false)]
    [InlineData("object", true)]
    public async Task InconsistentQueriedTargetRemainsStaleAtCollectionLevel(string source, bool duplicate)
    {
        using var runtime = new GuaRuntime();
        if (source == "ui") Ui(runtime, "one"); else World(runtime, "one");
        int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != (source == "ui" ? "get_ui_tree" : "get_world_object_tree")) return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var tree = root["result"]!.AsObject();
            var nodes = tree[source == "ui" ? "nodes" : "objects"]!.AsArray();
            if (duplicate) nodes.Add(nodes[0]!.DeepClone()); else nodes.Clear();
            Assert.True(GuaDistribution.ValidateJson(source == "ui" ? "ui-tree.schema.json" : "world-object-tree.schema.json", tree.ToJsonString()));
            injected++; return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint);
        var result = reader.Read(Read(source, "standard", "label", "string", "one"));
        Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Stale, result.Availability);
        var item = Assert.Single(result.Reads); Assert.Equal(ReadAvailability.Stale, item.Availability);
        Assert.Equal("target-changed", item.Reason); Assert.Null(item.Value); Assert.Empty(result.Changes!);
    }

    [Fact]
    public void WorldStateSuffixIsOneFlatKeyIncludingDots()
    {
        using var runtime = new GuaRuntime(); runtime.EnableWorldObjectTreeAdapter(); runtime.BeginWorldFrame("combat");
        runtime.RegisterWorldObject(new("one", "enemy", "Enemy", GuaWorldSpace.World2D, new(3, 4),
            State: new Dictionary<string, object?> { ["combat.phase"] = "attack", ["combat..phase"] = "defend", ["combat"] = "idle" }));
        runtime.EndWorldFrame(); using var reader = Reader(Start(runtime));
        foreach (var (key, expected) in new[] { ("combat.phase", "attack"), ("combat..phase", "defend"), ("combat", "idle") })
        {
            var result = reader.Read(Read("object", "standard", "state." + key, "string", "one"));
            Assert.Equal(ReadAvailability.Available, result.Availability);
            Assert.Equal(expected, Assert.Single(result.Reads).Value!.Value.GetProperty("value").GetString());
        }
    }

    [Fact]
    public async Task NotifyAfterInitialSnapshotCannotExposePreChangeEvidence()
    {
        using var runtime = new GuaRuntime(); using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        bool value = true; using var property = owner.Property("ready", () => GuaValue.Bool(value)); property.Notify();
        bool armed = false; int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, _) =>
        {
            if (armed && command.GetProperty("type").GetString() == "get_observe_snapshot")
            { armed = false; value = false; property.Notify(); injected++; }
            return false;
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("world", "property", "ready", "bool");
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability); armed = true;
        var result = reader.Read(read); Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Stale, result.Availability);
        Assert.Equal("changed-after-snapshot", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value); Assert.Null(result.Changes);
        var next = reader.Read(read); Assert.Equal(ReadAvailability.Available, next.Availability);
        Assert.False(Assert.Single(next.Reads).Value!.Value.GetProperty("value").GetBoolean());
        Assert.False(Assert.Single(next.Changes!).Event.GetProperty("after").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task CachedWorldQueryCannotMatchAnObjectThatNoLongerSatisfiesSelector()
    {
        using var runtime = new GuaRuntime(); World(runtime, "one"); string? cached = null; bool armed = false; int delivered = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "query_world_objects") return null;
            if (!armed) { cached = response.GetProperty("result").GetRawText(); return null; }
            var root = JsonNode.Parse(response.GetRawText())!.AsObject();
            Assert.Empty(root["result"]!["matches"]!.AsArray());
            root["result"] = JsonNode.Parse(cached!); delivered++;
            Assert.True(GuaDistribution.ValidateJson("world-query-result.schema.json", root["result"]!.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("object", "standard", "label", "string", "one");
        read["target"]!["selector"] = new JsonObject { ["kind"] = new JsonObject { ["value"] = "enemy" } };
        Assert.Equal(ReadAvailability.Available, reader.Read(read).Availability);
        runtime.BeginWorldFrame("shop"); runtime.RegisterWorldObject(new("one", "friend", "Friend", GuaWorldSpace.World2D, new(3, 4))); runtime.EndWorldFrame(); armed = true;
        var result = reader.Read(read); Assert.Equal(1, delivered); Assert.Equal(ReadAvailability.Stale, result.Availability);
        Assert.Equal("stale-query", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value);
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("-9007199254740993")]
    [InlineData("18446744073709551615")]
    [InlineData("9.007199254740993e15")]
    [InlineData("-9.007199254740993E+15")]
    [InlineData("9007199254740993.0")]
    [InlineData("9007199254740993000e-3")]
    public async Task InexactWorldStateIntegerCannotMatchItsRoundedNeighbor(string token)
    {
        using var runtime = new GuaRuntime(); runtime.EnableWorldObjectTreeAdapter(); runtime.BeginWorldFrame("numbers");
        using var number = JsonDocument.Parse(token);
        // This host can store only ABI doubles. Deliberately publish the neighboring
        // representable value, proving that an inexact query cannot select it.
        object numericState = number.RootElement.GetDouble();
        runtime.RegisterWorldObject(new("exact", "item", "Exact", GuaWorldSpace.World2D, new(0, 0), State: new Dictionary<string, object?> { ["n"] = numericState }));
        runtime.EndWorldFrame(); int queries = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, _) =>
        {
            if (command.GetProperty("type").GetString() == "query_world_objects")
            { queries++; }
            return false;
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("object", "standard", "label", "string", "exact");
        read["target"]!["selector"] = new JsonObject { ["state"] = new JsonObject { ["key"] = "n", ["value"] = JsonNode.Parse(token) } };
        var result = reader.Read(read); Assert.Equal(0, queries); Assert.Equal(ReadAvailability.Unavailable, result.Availability);
        Assert.Null(Assert.Single(result.Reads).Value);
    }

    [Theory]
    [InlineData("9007199254740991", "9007199254740991")]
    [InlineData("-9007199254740992", "-9007199254740992")]
    [InlineData("18446744073709549568", "18446744073709549568")]
    [InlineData("9.007199254740992e15", "9007199254740992")]
    [InlineData("9007199254740992.0", "9007199254740992")]
    [InlineData("1.208925819614629174706176e24", "1208925819614629174706176")]
    public async Task ExactWorldStateIntegersRetainNumericWireValue(string token, string expectedInteger)
    {
        using var runtime = new GuaRuntime(); runtime.EnableWorldObjectTreeAdapter(); runtime.BeginWorldFrame("numbers");
        using var number = JsonDocument.Parse(token);
        object numericState = number.RootElement.GetDouble();
        runtime.RegisterWorldObject(new("exact", "item", "Exact", GuaWorldSpace.World2D, new(0, 0), State: new Dictionary<string, object?> { ["n"] = numericState }));
        runtime.EndWorldFrame(); int queries = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (command, _) =>
        {
            if (command.GetProperty("type").GetString() == "query_world_objects")
            { Assert.Equal(System.Numerics.BigInteger.Parse(expectedInteger), new System.Numerics.BigInteger(command.GetProperty("stateNumber").GetDouble())); queries++; }
            return false;
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("object", "standard", "label", "string", "exact");
        read["target"]!["selector"] = new JsonObject { ["state"] = new JsonObject { ["key"] = "n", ["value"] = JsonNode.Parse(token) } };
        var result = reader.Read(read); Assert.Equal(1, queries); Assert.Equal(ReadAvailability.Available, result.Availability);
        Assert.Equal("exact", Assert.Single(result.Reads).Identity!.RuntimeId);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("throws")]
    [InlineData("cancelled")]
    public void LocalEnqueueBoundaryRefusalIsKnownNotSent(string mode)
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "boundary", GuaObservationProfile.Debug, 10);
        using var cancellation = new CancellationTokenSource(); int checks = 0;
        bool Authorize()
        {
            if (++checks != 2) return true;
            if (mode == "throws") throw new InvalidOperationException("private authorization detail");
            if (mode == "cancelled") cancellation.Cancel();
            return mode != "denied";
        }
        var result = bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, Authorize, cancellation.Token);
        Assert.Equal(2, checks); Assert.Equal(ConfirmedActionStage.NotSent, result.Stage); Assert.Null(result.RequestId);
        Assert.Equal(mode == "cancelled" ? ActionAttemptStatus.Aborted : ActionAttemptStatus.Rejected, result.Status);
        Assert.False(runtime.TryConsumeGameInput(out _));
        Assert.Equal(result, bridge.EndWait("a", false));
        Assert.Equal(result, bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, Authorize));
        Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("object")]
    public async Task DuplicateQueryMatchIdsDoNotCreateDuplicateWitnesses(string source)
    {
        using var runtime = new GuaRuntime(); if (source == "ui") Ui(runtime, "one"); else World(runtime, "one"); int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != (source == "ui" ? "query_nodes" : "query_world_objects")) return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); var matches = root["result"]!["matches"]!.AsArray();
            matches.Add(matches[0]!.DeepClone()); injected++;
            if (source == "object") Assert.True(GuaDistribution.ValidateJson("world-query-result.schema.json", root["result"]!.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var result = reader.Read(Read(source, "standard", "label", "string", "one"));
        Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Stale, result.Availability);
        Assert.Equal("duplicate-target", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LimitedWorldQueryWithoutCompletenessMetadataCannotExposeMatches(bool limited)
    {
        using var runtime = new GuaRuntime(); World(runtime, "one"); int injected = 0;
        await using var proxy = new BridgeFaultProxy(Start(runtime), (_, _) => false, (command, response) =>
        {
            if (command.GetProperty("type").GetString() != "query_world_objects") return null;
            var root = JsonNode.Parse(response.GetRawText())!.AsObject(); root["result"]!.AsObject().Remove("spatial"); injected++;
            Assert.True(GuaDistribution.ValidateJson("world-query-result.schema.json", root["result"]!.ToJsonString()));
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var reader = Reader(proxy.Endpoint); var read = Read("object", "standard", "label", "string", "one");
        read["target"]!["selector"]!["near"] = new JsonObject { ["relativeToObjectId"] = "one", ["maxDistance"] = 10 };
        if (limited) read["target"]!["selector"]!["limit"] = 1;
        var result = reader.Read(read); Assert.Equal(1, injected); Assert.Equal(ReadAvailability.Truncated, result.Availability);
        Assert.Equal("query-completeness-unconfirmed", Assert.Single(result.Reads).Reason); Assert.Null(Assert.Single(result.Reads).Value);
    }

    [Fact]
    public void NativeLimitedWorldQueryPreservesTruncationAndCompleteMatches()
    {
        using var runtime = new GuaRuntime(); runtime.EnableWorldObjectTreeAdapter(); runtime.BeginWorldFrame("range");
        runtime.RegisterWorldObject(new("anchor", "landmark", "Anchor", GuaWorldSpace.World2D, new(0, 0)));
        runtime.RegisterWorldObject(new("first", "enemy", "First", GuaWorldSpace.World2D, new(1, 0)));
        runtime.RegisterWorldObject(new("second", "enemy", "Second", GuaWorldSpace.World2D, new(2, 0)));
        runtime.EndWorldFrame(); using var reader = Reader(Start(runtime)); var read = Read("object", "standard", "label", "string", "unused");
        read["target"]!["selector"] = new JsonObject { ["kind"] = new JsonObject { ["value"] = "enemy" },
            ["near"] = new JsonObject { ["relativeToObjectId"] = "anchor", ["maxDistance"] = 10 }, ["limit"] = 1 };
        var truncated = reader.Read(read); Assert.Equal(ReadAvailability.Truncated, truncated.Availability);
        Assert.Null(Assert.Single(truncated.Reads).Value);
        read["target"]!["selector"]!["limit"] = 2;
        var complete = reader.Read(read); Assert.Equal(ReadAvailability.Available, complete.Availability);
        Assert.Equal(new[] { "first", "second" }, complete.Reads.Select(r => r.Identity!.RuntimeId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalDispatchExceptionRetainsAttemptUntilExplicitEndWait(bool cancelled)
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "dispatch-exception", GuaObservationProfile.Debug, 10); int checks = 0;
        bool Authorize()
        {
            if (++checks == 2) runtime.PublishGameInputActions("changed", [new("attack", "Changed", GuaGameInputValueType.Button)]);
            return true;
        }
        var pending = bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, Authorize);
        Assert.Equal(2, checks); Assert.Equal(ActionAttemptStatus.Pending, pending.Status);
        Assert.Equal("dispatch-unconfirmed", pending.Reason); Assert.Equal(ConfirmedActionStage.DispatchAttempted, pending.Stage);
        Assert.Null(pending.RequestId); Assert.False(runtime.TryConsumeGameInput(out _));
        Assert.Equal(pending, bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, Authorize));
        Assert.Equal(2, checks); Assert.Equal(pending, bridge.Poll("a"));
        Assert.Equal(cancelled ? ActionAttemptStatus.Aborted : ActionAttemptStatus.TimedOut, bridge.EndWait("a", cancelled).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalCompletionOwnerLossRemainsPendingUntilExplicitEndWait(bool cancelled)
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("fight", [new("attack", "Attack", GuaGameInputValueType.Button)]);
        ulong epoch = Epoch(runtime), revision = runtime.FindGameInputActionsV2(new(Id: "attack")).Revision;
        using var bridge = new OwnedGameInput(runtime, "lost-owner", GuaObservationProfile.Debug, 10);
        var sent = bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true);
        Assert.True(runtime.TryConsumeGameInput(out var consumed)); runtime.CompleteGameInput(consumed, true);
        // Trusted fault removes the actual native owner after host completion; no fake result.
        var nativeOwner = (GuaGameInputSession)typeof(OwnedGameInput).GetField("owner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(bridge)!;
        nativeOwner.Dispose();
        var pending = bridge.Poll("a"); Assert.Equal(ActionAttemptStatus.Pending, pending.Status);
        Assert.Equal("completion-unconfirmed", pending.Reason); Assert.Equal(ConfirmedActionStage.Enqueued, pending.Stage); Assert.Equal(sent.RequestId, pending.RequestId);
        Assert.Equal(pending, bridge.Poll("a")); Assert.Equal(pending, bridge.Send("a", epoch, revision, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "attack", null, null, () => true));
        var ended = bridge.EndWait("a", cancelled); Assert.Equal(cancelled ? ActionAttemptStatus.Aborted : ActionAttemptStatus.TimedOut, ended.Status);
        Assert.Equal(ended, bridge.Poll("a")); Assert.False(runtime.TryConsumeGameInput(out _));
    }

    [Fact]
    public void BroadSelectorPreservesEveryMatchedValueAndChange()
    {
        using var runtime = new GuaRuntime(); var ids = Enumerable.Range(0, 50).Select(i => "node-" + i).ToArray(); Ui(runtime, ids);
        int[] values = Enumerable.Range(0, 50).ToArray();
        var owners = ids.Select(id => runtime.CreateObserveOwner(GuaObserveSource.Ui, id)).ToArray();
        var registrations = owners.Select((owner, index) => owner.Observe("hp", () => GuaValue.Integer(values[index]))).ToArray();
        try
        {
            Ui(runtime, ids); using var reader = Reader(Start(runtime), nodes: 200); var read = Read("ui", "observe", "hp", "integer", "unused");
            read["target"]!["selector"] = new JsonObject { ["role"] = new JsonObject { ["value"] = "button" } };
            Assert.Equal(50, reader.Read(read).Reads.Count);
            for (int i = 0; i < values.Length; i++) values[i]++;
            Ui(runtime, ids); var result = reader.Read(read); Assert.Equal(ReadAvailability.Available, result.Availability);
            Assert.Equal(50, result.Reads.Count); Assert.Equal(50, result.Changes!.Count);
            Assert.Equal(ids.Order(StringComparer.Ordinal), result.Changes.Select(c => c.Event.GetProperty("runtimeId").GetString()!).Order(StringComparer.Ordinal));
            Assert.All(result.Changes, c => Assert.Equal(c.Event.GetProperty("before").GetProperty("value").GetInt32() + 1, c.Event.GetProperty("after").GetProperty("value").GetInt32()));
        }
        finally { foreach (var registration in registrations) registration.Dispose(); foreach (var owner in owners) owner.Dispose(); }
    }
}
