using System.Diagnostics;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    // Test-only tolerance: both timers start together; the diagnostic threshold
    // never grants another five seconds. Delivery can be late under scheduling
    // stalls, so snapshots are observation times, not exact one-second samples.
    private async Task<T> AwaitFinalizationWithWatchdogAsync<T>(Task<T> execution, Func<string> capture)
    {
        var elapsed = Stopwatch.StartNew();
        using var timers = new CancellationTokenSource();
        var diagnostic = Task.Delay(TimeSpan.FromSeconds(1), timers.Token);
        var watchdog = Task.Delay(TimeSpan.FromSeconds(5), timers.Token);
        try
        {
            return await AwaitFinalizationWatchdogCoreAsync(execution, diagnostic, watchdog, capture,
                message => raceOutput.WriteLine($"Finalization elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}: {message}"),
                () => elapsed.Elapsed >= TimeSpan.FromSeconds(1));
        }
        finally { timers.Cancel(); }
    }

    private static async Task<T> AwaitFinalizationWatchdogCoreAsync<T>(Task<T> execution,
        Task diagnostic, Task watchdog, Func<string> capture, Action<string> record, Func<bool> diagnosticDue)
    {
        // Register the terminal race before observing diagnostics, so delayed
        // snapshot delivery cannot rebase the stop or replace its recorded winner.
        var terminal = Task.WhenAny(execution, watchdog);
        var first = await Task.WhenAny(terminal, diagnostic);
        var slow = first == diagnostic || diagnosticDue();
        if (slow) record($"one-second threshold observed: {capture()}");
        var winner = await terminal;
        if (winner == watchdog)
            throw new Xunit.Sdk.XunitException($"Finalization did not finish within five-second outer test watchdog: {capture()}");
        var result = await execution;
        if (slow) record($"completed after threshold: {capture()}");
        return result;
    }

    [Fact]
    public async Task FinalizationWatchdogRetainsThresholdAndCompletionSnapshots()
    {
        var execution = new TaskCompletionSource<int>();
        var threshold = new TaskCompletionSource(); var stop = new TaskCompletionSource();
        var records = new List<string>(); var captures = 0;
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = AwaitFinalizationWatchdogCoreAsync(execution.Task, threshold.Task, stop.Task,
            () => $"capture={++captures}; task={execution.Task.Status}", message => { records.Add(message); recorded.TrySetResult(); }, () => false);
        threshold.SetResult();
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted); Assert.Single(records);
        execution.SetResult(42);
        Assert.Equal(42, await waiting.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(2, records.Count);
        Assert.Contains("WaitingForActivation", records[0]); Assert.Contains("RanToCompletion", records[1]);
    }

    [Fact]
    public async Task FinalizationWatchdogStopsUnfinishedWorkWithoutReleasingProvider()
    {
        var execution = new TaskCompletionSource<int>();
        var threshold = new TaskCompletionSource(); var stop = new TaskCompletionSource();
        var records = new List<string>();
        var waiting = AwaitFinalizationWatchdogCoreAsync(execution.Task, threshold.Task, stop.Task,
            () => $"task={execution.Task.Status}", records.Add, () => false);
        threshold.SetResult(); stop.SetResult();
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => waiting);
        Assert.Contains("outer test watchdog", failure.Message);
        Assert.False(execution.Task.IsCompleted); Assert.Single(records);
    }

    [Fact]
    public async Task FinalizationWatchdogDoesNotReplaceTimeoutWithLateCompletion()
    {
        var execution = new TaskCompletionSource<int>(); var stop = new TaskCompletionSource();
        var waiting = AwaitFinalizationWatchdogCoreAsync(execution.Task, new TaskCompletionSource().Task,
            stop.Task, () => "late", _ => { }, () => false);
        stop.SetResult(); execution.SetResult(9);
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => waiting);
    }

    [Fact]
    public async Task FinalizationWatchdogFastCompletionDoesNotCaptureDiagnostics()
    {
        Assert.Equal(3, await AwaitFinalizationWatchdogCoreAsync(Task.FromResult(3),
            new TaskCompletionSource().Task, new TaskCompletionSource().Task,
            () => throw new InvalidOperationException("unexpected capture"), _ => Assert.Fail("unexpected output"), () => false));
    }

    [Fact]
    public async Task FinalizationWatchdogRetainsSlowCompletionWhenThresholdDeliveryLoses()
    {
        var records = new List<string>();
        Assert.Equal(7, await AwaitFinalizationWatchdogCoreAsync(Task.FromResult(7),
            new TaskCompletionSource().Task, new TaskCompletionSource().Task, () => "completed", records.Add, () => true));
        Assert.Equal(2, records.Count);
    }
}
