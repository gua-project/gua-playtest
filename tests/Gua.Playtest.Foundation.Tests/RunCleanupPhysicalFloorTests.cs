using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    // No timer is created: the physical deadline cannot depend on delivery of a
    // hard-wake hint. This is controlled getter delay, not the natural CI schedule.
    [Theory]
    [InlineData("stalled")]
    [InlineData("advancing")]
    [InlineData("throws")]
    [InlineData("negative")]
    [InlineData("backwards")]
    public void CleanupPhysicalFloorIncludesBlockedProviderTime(string mode)
    {
        var source = new HeldCleanupFloorClock(mode);
        var run = new RunSession(Limits(), source, new Clock());
        Assert.Equal(TimeSpan.FromSeconds(1), run.Limits.CleanupTimeout);
        var errors = new List<Exception>();
        var type = typeof(OwnedCleanup).GetNestedType("CleanupClock", BindingFlags.NonPublic)!;
        var cleanup = (IClock)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [run, (Action<Exception>)errors.Add], null)!;
        var origin = (TimeSpan)type.GetProperty("Origin")!.GetValue(cleanup)!;
        var due = type.GetField("hardWakeAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(0L, due.GetValue(cleanup));
        source.Armed = true;
        var wall = Stopwatch.StartNew();
        var first = cleanup.Elapsed;
        wall.Stop();
        int firstErrors = errors.Count, firstReads = source.Reads;
        var second = cleanup.Elapsed;
        int secondReads = source.Reads;
        var third = cleanup.Elapsed;
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../artifacts/tests/runtime-evidence/cleanup-physical-floor"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, mode + "-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new
            {
                kind = "controlled-getter-delay-no-timer", mode, budgetMs = run.Limits.CleanupTimeout.TotalMilliseconds,
                holdRequestedMs = 1100, wallMs = wall.Elapsed.TotalMilliseconds,
                firstElapsedMs = (first - origin).TotalMilliseconds,
                secondElapsedMs = (second - origin).TotalMilliseconds,
                thirdElapsedMs = (third - origin).TotalMilliseconds,
                firstErrors, totalErrors = errors.Count, firstReads, secondReads, finalReads = source.Reads,
                delayCalls = source.DelayCalls, hardWakeAt = (long)due.GetValue(cleanup)!
            }));
        Assert.True(wall.Elapsed >= run.Limits.CleanupTimeout, "The controlled getter must span the unchanged budget.");
        Assert.True(first - origin >= run.Limits.CleanupTimeout,
            $"A completed getter must include its physical wait: wall={wall.Elapsed.TotalMilliseconds}ms, floor={(first - origin).TotalMilliseconds}ms.");
        Assert.True(second >= first); Assert.True(third >= second);
        Assert.Equal(0, source.DelayCalls); Assert.Equal(0L, due.GetValue(cleanup));
        if (mode == "advancing") Assert.Empty(errors);
        else
        {
            Assert.Single(errors);
            Assert.Equal(mode == "stalled" ? 0 : 1, firstErrors);
            Assert.Equal(secondReads, source.Reads); // fallback never re-invokes the broken provider
            if (mode != "stalled") Assert.Equal(firstReads, secondReads);
            if (mode == "throws") Assert.IsType<IOException>(errors[0]);
        }
    }

    private sealed class HeldCleanupFloorClock(string mode) : IClock
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly TimeSpan initial = mode is "negative" or "backwards" ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        internal bool Armed;
        internal int Reads, DelayCalls;
        public TimeSpan Elapsed
        {
            get
            {
                Reads++;
                if (Armed)
                {
                    Armed = false;
                    Thread.Sleep(1100);
                    if (mode == "throws") throw new IOException("ControlledCleanupClockRead");
                    if (mode == "negative") return TimeSpan.FromTicks(-1);
                    if (mode == "backwards") return initial - TimeSpan.FromSeconds(1);
                }
                return mode == "advancing" ? initial + elapsed.Elapsed : initial;
            }
        }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token)
        {
            DelayCalls++;
            throw new InvalidOperationException("This regression must not create a timer.");
        }
    }
}
