using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.Cli;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Conditions;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class SingleUiCursorLossTests
{
    [Theory]
    [InlineData(false, true, TruthValue.True)]
    [InlineData(true, true, TruthValue.True)]
    [InlineData(true, false, TruthValue.Unknown)]
    public async Task Private_recording_refusal_and_lost_cursor_do_not_replace_independent_point_goal(
        bool disconnect, bool standardUi, TruthValue expected)
    {
        using var host = new GuaRuntime();
        host.BeginFrame("title"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10))); host.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number));
        var source = new MonotonicClock();
        var json = JsonNode.Parse("""{"kind":"assertion","read":{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject();
        if (!standardUi) { json["read"]!["region"] = "observe"; json["read"]!.AsObject().Remove("field"); json["read"]!["name"] = "not-exposed"; }
        var condition = PreparedCondition.Create(json, new AssertionOptions(100, 1024));
        var connection = new GuaObservationConnection(new(host.InspectorBridgeUrl), "fixture-build", "Debug", source,
            condition, null, 100, 100000, TimeSpan.FromSeconds(2), () => { }, (_, _) => { }, enableClick: true);
        try
        {
            await connection.IdentifyAsync(default);
            var initial = await connection.SynchronizeAsync("fixture-initial", default);
            var click = connection.Click!;
            var recording = new GuardedClickRecording(click, "start", text => !text.Contains("private-marker", StringComparison.Ordinal),
                document => document.Clone(), 65536, 1000, 65536, default);
            var sent = recording.Start(() => { }, TimeSpan.FromSeconds(1), default);
            Assert.True(host.TryConsumeAction(GuaActionType.Click, "start", out var request));
            host.BeginFrame("play"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: true)); host.EndFrame();
            host.EmitActionResult(request with { Sensitive = true, Value = "private-marker" }, true);
            var completed = click.Poll(); recording.ObserveReceipt(completed);
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.True(await recording.ReleaseAsync(release.Token));
            Assert.True(recording.Freeze().CaptureFailed); Assert.Null(recording.Freeze().Recording);
            Assert.True(completed.Succeeded); Assert.Equal(sent.RequestId, completed.RequestId);
            Assert.False(host.TryConsumeAction(GuaActionType.Click, "start", out _));

            if (disconnect)
            {
                // Controlled transport loss, not a claimed reproduction of the CI cause.
                // Dispose this fixture's socket only, preserving host/source/epoch and
                // the SDK context. Its next Identify reconnects; the old cursor cannot.
                var observations = (BridgeObservations)typeof(GuaObservationConnection)
                    .GetField("currentObservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var context = (GuaWebSocketContext)typeof(BridgeObservations)
                    .GetField("context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(observations)!;
                ((IDisposable)typeof(GuaWebSocketContext).GetField("socket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!).Dispose();
            }
            var captured = await connection.CaptureAsync(default);
            var evaluation = condition.Start(source, TimeSpan.Zero).EvaluateAt(captured.Success, captured.CapturedAt);
            Assert.Equal(expected, evaluation.Evaluation.Truth);
            Assert.Equal(initial.CapturedIdentity.SourceId, connection.LastIdentity!.SourceId);
            Assert.Equal(initial.CapturedIdentity.Epoch, connection.LastIdentity.Epoch);
            Assert.False(host.TryConsumeAction(GuaActionType.Click, "start", out _));
        }
        finally { Assert.True(await connection.ReleaseAsync(default)); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Fixed_click_point_reads_keep_growth_limits_unknown_and_recover(bool byteLimit)
    {
        using var host = new GuaRuntime();
        void Publish(bool oversized, bool goal)
        {
            host.BeginFrame("fixture");
            host.RegisterNode(new("start", "button", oversized && byteLimit ? new string('x', 8192) : "Start",
                new(0, 0, 10, 10), Checked: goal));
            if (oversized && !byteLimit)
                foreach (var id in new[] { "extra-one", "extra-two" }) host.RegisterNode(new(id, "button", id, new(0, 0, 10, 10)));
            host.EndFrame();
        }
        Publish(false, false);
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number));
        var clock = new MonotonicClock();
        var definition = JsonNode.Parse("""{"kind":"assertion","read":{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":true}}""")!.AsObject();
        var condition = PreparedCondition.Create(definition, new AssertionOptions(100, 1024));
        BridgeReadCollection? collection = null;
        var connection = new GuaObservationConnection(new(host.InspectorBridgeUrl), "fixture-build", "Debug", clock,
            condition, null, byteLimit ? 100 : 2, byteLimit ? 4096 : 65536, TimeSpan.FromSeconds(2),
            () => { }, (captured, _) => Volatile.Write(ref collection, captured), enableClick: true);
        try
        {
            await connection.IdentifyAsync(default);
            var initial = await connection.SynchronizeAsync("limits-initial", default);
            Assert.Equal(TruthValue.False, condition.Start(clock, TimeSpan.Zero)
                .EvaluateAt(initial.Observation.Success, initial.Observation.CapturedAt).Evaluation.Truth);
            Publish(true, true);
            var oversized = await connection.CaptureAsync(default);
            Assert.Equal(ReadAvailability.Truncated, Volatile.Read(ref collection)!.Availability);
            var limit = Assert.Single(collection!.Reads);
            Assert.Equal(ReadAvailability.Truncated, limit.Availability);
            Assert.Null(limit.Identity); Assert.Null(limit.Value); Assert.Null(limit.EnumCatalog);
            Assert.Equal(TruthValue.Unknown, condition.Start(clock, TimeSpan.Zero)
                .EvaluateAt(oversized.Success, oversized.CapturedAt).Evaluation.Truth);
            Publish(false, true);
            var recovered = await connection.CaptureAsync(default);
            Assert.Equal(ReadAvailability.Available, Volatile.Read(ref collection)!.Availability);
            Assert.Equal(TruthValue.True, condition.Start(clock, TimeSpan.Zero)
                .EvaluateAt(recovered.Success, recovered.CapturedAt).Evaluation.Truth);
            Assert.Equal(initial.CapturedIdentity.SourceId, connection.LastIdentity!.SourceId);
            Assert.Equal(initial.CapturedIdentity.Epoch, connection.LastIdentity.Epoch);
            Assert.Null(connection.Click!.Receipt);
            Assert.False(host.TryConsumeAction(GuaActionType.Click, "start", out _));
        }
        finally { Assert.True(await connection.ReleaseAsync(default)); }
    }

    [Fact]
    public void Fixed_click_point_batch_counts_aggregate_serialized_metadata()
    {
        using var host = new GuaRuntime();
        host.BeginFrame("fixture"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: true)); host.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number));
        const int maxBytes = 4096;
        using var observations = new BridgeObservations(host.InspectorBridgeUrl, GuaObservationProfile.Debug,
            TimeSpan.FromSeconds(2), 100, maxBytes);
        var read = JsonNode.Parse("""{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}}""")!.AsObject();
        var single = Assert.Single(observations.ReadUiPointBatch([read]));
        Assert.Equal(ReadAvailability.Available, single.Availability);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(single)) < maxBytes);
        var repeated = Enumerable.Range(0, 32).Select(_ => (JsonObject)read.DeepClone()).ToArray();
        var scalarSnapshot = observations.ReadUiSnapshot(repeated);
        Assert.All(scalarSnapshot.Collections, item => Assert.Equal(ReadAvailability.Available, item.Availability));
        Assert.True(scalarSnapshot.Collections.Sum(item => System.Text.Encoding.UTF8.GetByteCount(
            System.Text.Json.JsonSerializer.Serialize(item))) > maxBytes);
        var bounded = observations.ReadUiPointBatch(repeated);
        Assert.Equal(repeated.Length, bounded.Count);
        Assert.All(bounded, item =>
        {
            Assert.Equal(ReadAvailability.Truncated, item.Availability);
            var failure = Assert.Single(item.Reads);
            Assert.Null(failure.Identity); Assert.Null(failure.Value); Assert.Null(failure.EnumCatalog);
        });
        Assert.Equal(ReadAvailability.Available, Assert.Single(observations.ReadUiPointBatch([read])).Availability);
        Assert.False(host.TryConsumeAction(GuaActionType.Click, "start", out _));
    }

    private sealed class MonotonicClock : IClock
    {
        private readonly Stopwatch time = Stopwatch.StartNew();
        public TimeSpan Elapsed => time.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
}
