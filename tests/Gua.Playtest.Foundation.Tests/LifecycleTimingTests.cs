using System.Diagnostics;
using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class LifecycleTimingTests
{
    private static JsonElement Snapshot(LifecycleTiming timing) => JsonSerializer.SerializeToElement(timing.Snapshot());

    [Fact]
    public void Numeric_tail_filters_owners_retains_zero_and_correlates_operations()
    {
        var run = new object(); var store = new object(); long origin = Stopwatch.GetTimestamp();
        using var timing = new LifecycleTiming(run, store, origin, 3);
        void Send(object owner, string phase, long op, long? budget, long? remaining)
            => timing.OnNext(new KeyValuePair<string, object?>(phase, (owner, origin, op, budget, remaining, 0, false)));
        Send(new object(), "foreign", 9, null, null);
        Send(run, "controller.create", 1, 0, null);
        Send(run, "controller.resume", 1, null, 0);
        Send(store, "primary.write", -1, null, -2);
        var snapshot = Snapshot(timing);
        var rows = snapshot.GetProperty("events");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(0, rows[0].GetProperty("ticks").GetInt64());
        Assert.Equal(0, rows[0].GetProperty("budgetTicks").GetInt64());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("remainingTicks").ValueKind);
        Assert.Equal(rows[0].GetProperty("operation").GetInt64(), rows[1].GetProperty("operation").GetInt64());
        Assert.Equal(-2, rows[2].GetProperty("remainingTicks").GetInt64());
        Send(run, "controller.last", 2, null, null);
        snapshot = Snapshot(timing);
        Assert.Equal(1, snapshot.GetProperty("overwritten").GetInt64());
        Assert.Equal(2, snapshot.GetProperty("events")[0].GetProperty("number").GetInt64());
        Assert.Contains("not proof", snapshot.GetProperty("missingPhaseMeaning").GetString());
        Assert.DoesNotContain("owner", snapshot.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("primary.save.entry", "primary.save.exit")]
    [InlineData("controller.wait.create", "controller.wait.resume")]
    public async Task Controlled_boundary_delay_is_visible_and_does_not_invent_unreached_phases(string before, string after)
    {
        var owner = new object(); long origin = Stopwatch.GetTimestamp();
        using var timing = new LifecycleTiming(owner, new object(), origin);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long op = RuntimeTiming.NextOperation();
        var work = Task.Factory.StartNew(() =>
        {
            RuntimeTiming.Mark(owner, before, op); entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("controlled release not delivered");
            RuntimeTiming.Mark(owner, after, op);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var during = Snapshot(timing).GetProperty("events");
            Assert.Single(during.EnumerateArray());
            Assert.Equal(before, during[0].GetProperty("phase").GetString());
            await Task.Delay(20); // controlled injection only; no product deadline changes
            release.Set(); await work.WaitAsync(TimeSpan.FromSeconds(3));
            var rows = Snapshot(timing).GetProperty("events");
            Assert.Equal(2, rows.GetArrayLength());
            Assert.Equal(op, rows[1].GetProperty("operation").GetInt64());
            double elapsed = (rows[1].GetProperty("ticks").GetInt64() - rows[0].GetProperty("ticks").GetInt64()) * 1000d / Stopwatch.Frequency;
            Assert.True(elapsed >= 15, $"controlled interval {elapsed}ms");
        }
        finally { release.Set(); await work.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
