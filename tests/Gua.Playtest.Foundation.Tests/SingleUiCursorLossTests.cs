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

    private sealed class MonotonicClock : IClock
    {
        private readonly Stopwatch time = Stopwatch.StartNew();
        public TimeSpan Elapsed => time.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
}
