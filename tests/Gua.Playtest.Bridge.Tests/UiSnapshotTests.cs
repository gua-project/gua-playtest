using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

[CollectionDefinition("UI snapshot native", DisableParallelization = true)]
public sealed class UiSnapshotCollection { }
[Collection("UI snapshot native")]
public sealed class UiSnapshotTests
{
    private static void Frame(GuaRuntime host, string text = "before", bool enabled = true)
    { host.BeginFrame("snapshot"); host.RegisterNode(new("target", "button", "Target", new(0,0,10,10), Text: text, Enabled: enabled)); host.EndFrame(); }
    private static string Start(GuaRuntime host)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Assert.True(host.StartInspectorBridge(port)); return host.InspectorBridgeUrl;
    }
    private static JsonObject Read(string field = "text", string type = "string", string id = "target") => new()
    {
        ["target"] = new JsonObject { ["source"] = "ui", ["selector"] = new JsonObject { ["id"] = new JsonObject { ["value"] = id } } },
        ["region"] = "standard", ["field"] = field, ["valueType"] = new JsonObject { ["type"] = type }
    };
    [Fact]
    public void Point_snapshot_keeps_values_from_one_tree_when_later_UI_changes()
    {
        using var host = new GuaRuntime(); Frame(host); using var reader = new BridgeObservations(Start(host), GuaObservationProfile.Debug, TimeSpan.FromSeconds(2), 100, 65536);
        using var hook = new AfterTree(() => Frame(host, "after", false));
        var result = reader.ReadUiSnapshot([Read(), Read("enabled", "bool")]);
        Assert.Equal(1, hook.Calls); Assert.Null(hook.Error);
        Assert.Equal("before", Assert.Single(result.Collections[0].Reads).Value!.Value.GetProperty("value").GetString());
        Assert.True(Assert.Single(result.Collections[1].Reads).Value!.Value.GetProperty("value").GetBoolean());
        Assert.All(result.Collections, collection => Assert.Equal(ReadAvailability.Available, collection.Availability));
        Assert.All(result.Collections.SelectMany(x => x.Reads), read => Assert.Equal(result.Identity.Revision, read.Identity!.Revision));
    }
    [Fact]
    public void Existing_cross_call_read_still_rejects_the_same_changed_tree()
    {
        using var host = new GuaRuntime(); Frame(host); using var reader = new BridgeObservations(Start(host), GuaObservationProfile.Debug, TimeSpan.FromSeconds(2), 100, 65536);
        using var hook = new AfterTree(() => Frame(host, "after", false));
        Assert.NotEqual(ReadAvailability.Available, reader.Read(Read()).Availability);
        Assert.Equal(1, hook.Calls); Assert.Null(hook.Error);
    }
    [Fact]
    public void Epoch_reset_after_snapshot_is_refused_instead_of_relabelling_values()
    {
        using var host = new GuaRuntime(); Frame(host); var endpoint = Start(host);
        using var reader = new BridgeObservations(endpoint, GuaObservationProfile.Debug, TimeSpan.FromSeconds(2), 100, 65536);
        using var hook = new AfterTree(() => { using var control = new GuaWebSocketContext(endpoint); control.Reset(); Frame(host, "new-epoch"); });
        var error = Assert.Throws<InvalidOperationException>(() => reader.ReadUiSnapshot([Read()]));
        Assert.Equal("UiSnapshotIdentityChanged", error.Message); Assert.Equal(1, hook.Calls); Assert.Null(hook.Error);
    }
    [Fact]
    public void Actual_profile_mismatch_is_refused_before_any_projection()
    {
        using var host = new GuaRuntime(); Frame(host);
        using var reader = new BridgeObservations(Start(host), GuaObservationProfile.Player, TimeSpan.FromSeconds(2), 100, 65536);
        Assert.Throws<InvalidOperationException>(() => reader.ReadUiSnapshot([Read()]));
    }
    [Fact]
    public void Missing_target_and_wrong_value_type_remain_unavailable()
    {
        using var host = new GuaRuntime(); Frame(host); using var reader = new BridgeObservations(Start(host), GuaObservationProfile.Debug, TimeSpan.FromSeconds(2), 100, 65536);
        var result = reader.ReadUiSnapshot([Read(id:"missing"), Read(type:"bool")]);
        Assert.Equal(ReadAvailability.Unavailable, result.Collections[0].Availability);
        Assert.Equal(ReadAvailability.Unavailable, Assert.Single(result.Collections[1].Reads).Availability);
        Assert.All(result.Collections.SelectMany(x => x.Reads), read => Assert.Null(read.Value));
        var world = Read(); world["target"]!["source"] = "world";
        Assert.Throws<ArgumentException>(() => reader.ReadUiSnapshot([world]));
    }
    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    { public void OnNext(T value) => next(value); public void OnError(Exception error) { } public void OnCompleted() { } }
    private sealed class AfterTree : IDisposable
    {
        private readonly IDisposable all; private IDisposable? calls; private readonly int thread = Environment.CurrentManagedThreadId;
        public int Calls; public Exception? Error;
        public AfterTree(Action change)
        {
            all = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Gua.Playtest.ObservationSdk") return;
                calls = listener.Subscribe(new Observer<KeyValuePair<string, object?>>(item =>
                {
                    if (Environment.CurrentManagedThreadId != thread || item.Value is not ValueTuple<string,long,int> value ||
                        value.Item1 != "ui-tree" || value.Item3 != 1 || Interlocked.CompareExchange(ref Calls, 1, 0) != 0) return;
                    try { change(); } catch (Exception error) { Error = error; }
                }));
            }));
        }
        public void Dispose() { calls?.Dispose(); all.Dispose(); }
    }
}
