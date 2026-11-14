using System.Diagnostics;
using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.TestSupport;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ReplayFailureDiagnosticsTests
{
    static string DirectoryForTest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "diagnostic-controls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    sealed class SecretOwner
    {
        public override string ToString() => throw new InvalidOperationException("secret-token-never-render");
    }
    [Fact]
    public void Failure_snapshot_is_bounded_numeric_and_success_creates_no_public_file()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-only shared test transport.
        string directory = DirectoryForTest();
        using var timing = new ReplayFailureDiagnostics(Path.Combine(directory, "private-token-authority-recording"), 2);
        var owner = new SecretOwner();
        for (int i = 0; i < 20; i++) RuntimeTiming.Mark(owner, "replay.queue.request", i);
        timing.OnNext(new KeyValuePair<string, object?>("password-private-game-path", (owner, Stopwatch.GetTimestamp(), 1L, (long?)0, (long?)0, 0, false)));
        timing.OnNext(new KeyValuePair<string, object?>("Replay", ("credentials-raw-recording", 31, 1UL, 1)));
        Assert.False(timing.SaveFailure(directory, false));
        Assert.False(File.Exists(Path.Combine(directory, "replay-timing.json")));
        Assert.True(timing.SaveFailure(directory, true));
        string text = File.ReadAllText(Path.Combine(directory, "replay-timing.json"));
        foreach (var secret in new[] { "private-token", "authority", "recording", "credentials", "password", directory })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        var json = JsonSerializer.SerializeToElement(timing.Snapshot()).GetProperty("journal");
        Assert.Equal(2, json.GetProperty("rows").GetArrayLength());
        Assert.True(json.GetProperty("segments")[0].GetProperty("dropped").GetBoolean());
        Assert.True(json.GetProperty("segments")[1].GetProperty("missing").GetBoolean());
        Assert.Contains("not proof", json.GetProperty("missingMeaning").GetString());
        Assert.True(text.Length < 4096);
    }
    [Fact]
    public void Shared_host_journal_uses_common_clock_and_separate_trial_and_adopted_segments()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pid = Path.Combine(DirectoryForTest(), "pid");
        using var parent = ReplayTimingJournal.Create(pid, 8);
        parent.Mark(20, 7);
        using (var trial = ReplayTimingJournal.OpenHost(pid)) { Assert.NotNull(trial); trial.Mark(50, 1); trial.Mark(54, 1); }
        parent.NextHost();
        using (var adopted = ReplayTimingJournal.OpenHost(pid)) { Assert.NotNull(adopted); adopted.Mark(50, 1); adopted.Mark(54, 1); }
        var json = JsonSerializer.SerializeToElement(parent.Snapshot());
        var segments = json.GetProperty("segments");
        Assert.Equal(1, segments[0].GetProperty("committed").GetInt32());
        Assert.Equal(3, segments[1].GetProperty("committed").GetInt32());
        Assert.Equal(3, segments[2].GetProperty("committed").GetInt32());
        foreach (var row in json.GetProperty("rows").EnumerateArray()) Assert.True(row.GetProperty("ticks").GetInt64() >= 0);
        Assert.Null(ReplayTimingJournal.OpenHost(pid + "missing"));
    }
    [Fact]
    public async Task Controlled_queue_delay_and_sdk_state_are_separate_and_new_phases_stay_out_of_command_tail()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = DirectoryForTest(); var owner = new object();
        using var timing = new ReplayFailureDiagnostics(Path.Combine(directory, "pid"));
        using var lifecycle = new LifecycleTiming(owner, new object(), Stopwatch.GetTimestamp());
        using var sdk = new DiagnosticListener("Gua.Playtest.ObservationSdk");
        RuntimeTiming.MarkAt(owner, "replay.approval.before", Stopwatch.GetTimestamp(), 100, 10000100);
        RuntimeTiming.MarkAt(owner, "replay.approval.after", Stopwatch.GetTimestamp(), 100, 10000100);
        RuntimeTiming.Mark(owner, "replay.queue.request", 8);
        RuntimeTiming.Mark(owner, "replay.queue.admitted", 8);
        await Task.Delay(20); // Explicit control only; no product delay or changed deadline.
        RuntimeTiming.Mark(owner, "replay.queue.invoke", 8);
        sdk.Write("Replay", (Stopwatch.GetTimestamp(), 32, 1UL, -1));
        sdk.Write("Replay", (Stopwatch.GetTimestamp(), 33, 1UL, 2));
        RuntimeTiming.Mark(owner, "replay.queue.return", 8);
        var rows = JsonSerializer.SerializeToElement(timing.Snapshot()).GetProperty("journal").GetProperty("rows")
            .EnumerateArray().Where(row => row.GetProperty("phase").GetInt32() != 2).ToArray();
        Assert.Equal(8, rows.Length);
        long start = rows[3].GetProperty("ticks").GetInt64(), end = rows[4].GetProperty("ticks").GetInt64();
        Assert.True((end - start) * 1000d / Stopwatch.Frequency >= 15);
        Assert.Equal(33, rows[6].GetProperty("phase").GetInt32());
        Assert.Equal(2, rows[6].GetProperty("a").GetInt32());
        Assert.Empty(JsonSerializer.SerializeToElement(lifecycle.Snapshot()).GetProperty("events").EnumerateArray());
        Assert.False(timing.SaveFailure(directory, false));
    }
    [Fact]
    public void Throwing_runtime_observers_are_contained()
    {
        using var throwing = new SubscribeThrowing();
        using var all = DiagnosticListener.AllListeners.Subscribe(throwing);
        RuntimeTiming.Mark(new object(), "replay.test.throw", 1);
        RuntimeTiming.MarkAt(new object(), "replay.test.throw", Stopwatch.GetTimestamp(), 0, 1);
    }
    sealed class ThrowObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item) => throw new Exception("observer-only-secret");
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
    sealed class SubscribeThrowing : IObserver<DiagnosticListener>, IDisposable
    {
        readonly List<IDisposable> subscriptions = [];
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name == "Gua.Playtest.RuntimeTiming") subscriptions.Add(listener.Subscribe(new ThrowObserver(), phase => phase == "replay.test.throw")); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}
