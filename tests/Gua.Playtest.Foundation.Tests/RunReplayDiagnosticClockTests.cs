using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    sealed class DiagnosticCountingClock : IClock
    {
        public int Reads;
        public TimeSpan Elapsed { get { Reads++; return TimeSpan.Zero; } }
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => ValueTask.CompletedTask;
    }
    [Fact]
    public void Replay_diagnostic_clock_brackets_do_not_read_the_provider_again_or_change_deadlines()
    {
        if (!OperatingSystem.IsWindows()) return;
        int? baseline = null;
        foreach (bool enabled in new[] { false, true })
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "diagnostic-controls", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var timing = enabled ? new ReplayFailureDiagnostics(Path.Combine(directory, "pid")) : null;
            var clock = new DiagnosticCountingClock();
            var run = new RunSession(Limits(), clock, clock, Condition()); run.BeginPreparation();
            run.BeginRunning();
            int before = clock.Reads;
            var operation = run.ApproveOperation(1, TimeSpan.FromMilliseconds(1000));
            Assert.NotNull(operation);
            int reads = clock.Reads - before;
            if (enabled) Assert.Equal(baseline, reads); else baseline = reads;
            Assert.Equal(TimeSpan.FromMilliseconds(1000), operation.ResultDeadline);
            Assert.Equal(TimeSpan.FromMilliseconds(1000), operation.Deadline);
            Assert.Equal(enabled, operation.DiagnosticClock.Before != 0);
            Assert.True(operation.DiagnosticClock.After >= operation.DiagnosticClock.Before);
        }
    }
}
