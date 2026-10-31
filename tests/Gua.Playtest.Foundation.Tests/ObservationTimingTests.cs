using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;
using Gua.Runtime;
using Gua.Playtest.Cli;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Bridge.Tests;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ObservationTimingTests
{
    private sealed class Clock : IClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public TimeSpan Elapsed => watch.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
    private static JsonElement Snapshot(ObservationTiming timing) => JsonSerializer.SerializeToElement(timing.Snapshot());
    [Fact]
    public void Synthetic_boundaries_correlate_by_reference_and_keep_missing_phases_unknown()
    {
        using var timing = new ObservationTiming(2);
        var first = timing.Reserve(default)!; var second = timing.Reserve(default)!;
        long Tick(int ms) => first.Reserved + Stopwatch.Frequency * ms / 1000;
        first.Worker = Tick(100); first.Gate = Tick(300);
        first.Sdk("synthetic-sdk", Tick(400), 0); first.Sdk("synthetic-sdk", Tick(900), 1);
        first.Returned = Tick(1000); first.Outcome = 1;
        var unit = new ConditionObservationUnit([]);
        first.Observation = new(TimeSpan.Zero, unit, unit);
        second.Observation = new(TimeSpan.Zero, unit, unit);
        Assert.Equal(first.Observation, second.Observation); // value-equal is deliberately insufficient
        timing.OnNext(new KeyValuePair<string, object?>("Evaluation", (second.Observation, Tick(1100), 0)));
        timing.OnNext(new KeyValuePair<string, object?>("Evaluation", (first.Observation, Tick(1300), 0)));
        timing.OnNext(new KeyValuePair<string, object?>("Evaluation", (first.Observation, Tick(1400), 1)));
        var rows = Snapshot(timing).GetProperty("observations");
        Assert.Equal(100, rows[0].GetProperty("queueMs").GetDouble(), 5);
        Assert.Equal(200, rows[0].GetProperty("gateWaitMs").GetDouble(), 5);
        Assert.Equal(500, rows[0].GetProperty("sdkMs").GetDouble(), 5);
        Assert.Equal(200, rows[0].GetProperty("adapterNonSdkMs").GetDouble(), 5);
        Assert.Equal(300, rows[0].GetProperty("deliveryToEvaluationMs").GetDouble(), 5);
        Assert.Equal(100, rows[0].GetProperty("evaluationMs").GetDouble(), 5);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("workerTicks").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("sdkMs").ValueKind);
        Assert.Equal("not-observed-terminal", rows[1].GetProperty("workerOutcome").GetString());
    }
    [Fact]
    public void Sampling_and_sdk_overflow_are_explicit_and_never_imply_zero_wait()
    {
        using var timing = new ObservationTiming(1); var row = timing.Reserve(default)!;
        Assert.Null(timing.Reserve(default));
        for (int i = 0; i < 65; i++) { row.Sdk("test", Stopwatch.GetTimestamp(), 0); row.Sdk("test", Stopwatch.GetTimestamp(), 1); }
        row.Gate = row.Reserved; row.Returned = Stopwatch.GetTimestamp();
        var snapshot = Snapshot(timing); var result = snapshot.GetProperty("observations")[0];
        Assert.Equal(1, snapshot.GetProperty("dropped").GetInt32());
        Assert.Equal(1, result.GetProperty("sdkDropped").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("sdkMs").ValueKind);
        Assert.Contains("not proof", snapshot.GetProperty("missingPhaseMeaning").GetString());
    }
    [Fact]
    public async Task Concurrent_diagnostic_scopes_do_not_mix_sdk_calls()
    {
        using var timing = new ObservationTiming();
        var first = timing.Reserve(default)!; var second = timing.Reserve(default)!;
        async Task Record(ObservationTiming.Sample sample, string operation)
        {
            var previous = timing.Enter(sample);
            try
            {
                timing.OnNext(new KeyValuePair<string, object?>("Call", (operation, Stopwatch.GetTimestamp(), 0)));
                await Task.Yield();
                timing.OnNext(new KeyValuePair<string, object?>("Call", (operation, Stopwatch.GetTimestamp(), 1)));
            }
            finally { timing.Restore(previous); }
        }
        await Task.WhenAll(Task.Run(() => Record(first, "first")), Task.Run(() => Record(second, "second")));
        var rows = Snapshot(timing).GetProperty("observations");
        Assert.Equal("first", Assert.Single(rows[0].GetProperty("sdkCalls").EnumerateArray()).GetProperty("operation").GetString());
        Assert.Equal("second", Assert.Single(rows[1].GetProperty("sdkCalls").EnumerateArray()).GetProperty("operation").GetString());
    }
    [Theory]
    [InlineData("success")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task Real_native_sdk_delay_is_separate_from_gate_and_adapter_delay(string mode)
    {
        using var host = new GuaRuntime(); host.BeginFrame("timing");
        host.RegisterNode(new("start", "button", "secret-game-label", new(0, 0, 10, 10))); host.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number));
        int measuring = 0, delayed = 0;
        await using var proxy = new BridgeFaultProxy(host.InspectorBridgeUrl,
            (request, _) => Volatile.Read(ref measuring) != 0 && mode == "exception" && request.GetProperty("type").GetString() == "get_version",
            delayReply: (request, _) =>
            {
                if (Volatile.Read(ref measuring) == 0 || request.GetProperty("type").GetString() != "get_version") return TimeSpan.Zero;
                Interlocked.Increment(ref delayed); return TimeSpan.FromMilliseconds(80);
            });
        using var timing = new ObservationTiming();
        var condition = PreparedCondition.Create(JsonNode.Parse("""{"kind":"assertion","read":{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":false}}""")!.AsObject(), new AssertionOptions(100, 1024));
        var connection = new GuaObservationConnection(new Uri(proxy.Endpoint), "build", "Debug", new Clock(), condition, null,
            100, 100000, TimeSpan.FromSeconds(2), () => { if (Volatile.Read(ref measuring) != 0) Thread.Sleep(30); }, (_, _) => { }) { Timing = timing };
        try
        {
            await connection.IdentifyAsync(default);
            Volatile.Write(ref measuring, 1);
            using var cancelled = new CancellationTokenSource();
            var gate = typeof(GuaObservationConnection).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
            Task<RunObservation> pending;
            lock (gate)
            {
                pending = connection.CaptureAsync(cancelled.Token).AsTask();
                Assert.True(SpinWait.SpinUntil(() => Snapshot(timing).GetProperty("observations")[0].GetProperty("workerTicks").ValueKind != JsonValueKind.Null, TimeSpan.FromSeconds(2)));
                Thread.Sleep(50); // controlled adapter-gate delay; no product deadline changed
            }
            if (mode == "cancel")
            {
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref delayed) > 0, TimeSpan.FromSeconds(2)));
                cancelled.Cancel();
            }
            if (mode == "success") await pending.WaitAsync(TimeSpan.FromSeconds(5));
            else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else await Assert.ThrowsAnyAsync<Exception>(() => pending);
            var snapshot = Snapshot(timing); var row = snapshot.GetProperty("observations")[0];
            Assert.True(row.GetProperty("gateWaitMs").GetDouble() >= 40, snapshot.ToString());
            Assert.Equal(mode switch { "success" => "returned", "cancel" => "cancelled", _ => "exception" }, row.GetProperty("workerOutcome").GetString());
            if (mode == "success")
            {
                Assert.True(Volatile.Read(ref delayed) >= 2);
                Assert.True(row.GetProperty("sdkMs").GetDouble() >= 140, snapshot.ToString());
                Assert.True(row.GetProperty("adapterNonSdkMs").GetDouble() >= 100, snapshot.ToString());
                Assert.All(row.GetProperty("sdkCalls").EnumerateArray(), call => Assert.Equal("returned", call.GetProperty("outcome").GetString()));
            }
            if (mode == "exception") Assert.Contains(row.GetProperty("sdkCalls").EnumerateArray(), call => call.GetProperty("outcome").GetString() == "exception");
            Assert.DoesNotContain("secret-game-label", snapshot.ToString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("evaluationStartedTicks").ValueKind);
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/tests/runtime-evidence/observation-timing"));
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, mode + "-" + Guid.NewGuid().ToString("N") + ".json"), snapshot.ToString());
        }
        finally { Assert.True(await connection.ReleaseAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(3))); }
    }
    [Fact]
    public async Task Precancelled_capture_has_no_claim_of_a_worker_or_sdk_entry()
    {
        using var timing = new ObservationTiming(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        // Constructor establishes a real connection, so this condition is covered in the native test's
        // existing pressure probe too; here the diagnostic representation stays intentionally unknown.
        var sample = timing.Reserve(cancelled.Token)!;
        sample.Task = Task.FromCanceled(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sample.Task);
        var row = Snapshot(timing).GetProperty("observations")[0];
        Assert.Equal("Canceled", row.GetProperty("taskStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("workerTicks").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("sdkMs").ValueKind);
        Assert.True(row.GetProperty("tokenCancellationRequested").GetBoolean());
    }
}
