using System.Diagnostics;
using System.Text.Json;
using Gua.Playtest.Runner.Execution;
using Xunit;
using Xunit.Abstractions;

namespace Gua.Playtest.Foundation.Tests;

// Owns copied test evidence only. Reporting cannot call RunSession, its clocks,
// reservation getters, worker callbacks or waits. Samples are not an atomic Run snapshot.
internal sealed class GoalSuffixRaceDiagnostics(BudgetSnapshot initialBudget)
{
    private readonly long origin = Stopwatch.GetTimestamp();
    internal readonly Worker Goal = new();
    internal readonly Worker Cancel = new();

    internal sealed class Worker
    {
        private sealed record Checkpoint(string Phase, long At, long? Started = null, long? Exited = null,
            BudgetSnapshot? Budget = null, long? BudgetAt = null, string? FaultType = null);
        private Checkpoint latest = new("not-started", 0);
        internal void Start()
        {
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref latest, new("started", now, Started: now));
        }
        internal void Mark(string phase, BudgetSnapshot? budget = null)
        {
            var now = Stopwatch.GetTimestamp(); var prior = Volatile.Read(ref latest);
            Volatile.Write(ref latest, prior with { Phase = phase, At = now,
                Budget = budget ?? prior.Budget, BudgetAt = budget is null ? prior.BudgetAt : now });
        }
        internal void Fault(Exception error)
        {
            var prior = Volatile.Read(ref latest);
            Volatile.Write(ref latest, prior with { FaultType = error.GetType().FullName });
        }
        internal void Exit()
        {
            var prior = Volatile.Read(ref latest);
            Volatile.Write(ref latest, prior with { Exited = Stopwatch.GetTimestamp() });
        }
        internal object Capture(long origin, Task? task)
        {
            var point = Volatile.Read(ref latest);
            var captured = Stopwatch.GetTimestamp();
            double? Milliseconds(long? at) => at is null ? null : Stopwatch.GetElapsedTime(origin, at.Value).TotalMilliseconds;
            return new
            {
                taskStatus = task?.Status.ToString() ?? "not-created",
                capturedMilliseconds = Milliseconds(captured),
                bodyState = point.Started is null ? "start-not-observed" : point.Exited is null ? "in-progress" : "exited",
                phase = point.Phase,
                startedMilliseconds = Milliseconds(point.Started),
                phaseMilliseconds = point.At == 0 ? (double?)null : Milliseconds(point.At),
                exitedMilliseconds = Milliseconds(point.Exited),
                bodyElapsedMilliseconds = point.Started is null ? (double?)null : Stopwatch.GetElapsedTime(point.Started.Value, point.Exited ?? captured).TotalMilliseconds,
                cachedBudget = point.Budget, budgetCheckpointMilliseconds = Milliseconds(point.BudgetAt), point.FaultType
            };
        }
    }

    internal void TryWrite(Action<string> output, int iteration, string waitSite, Task? goal, Task? cancel, Exception error)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            output("goal-suffix-race-diagnostic " + JsonSerializer.Serialize(new
            {
                iterationZeroBased = iteration, iterationOneBased = iteration + 1, repetitionCount = 32,
                configuredWaitMilliseconds = 1000, waitSite,
                measurementOrigin = "initial-reservation-sent-before-workers",
                elapsedFromMeasurementOriginMilliseconds = Stopwatch.GetElapsedTime(origin, now).TotalMilliseconds,
                exceptionType = error.GetType().FullName, initialBudget,
                goal = Goal.Capture(origin, goal), cancel = Cancel.Capture(origin, cancel),
                interpretation = "Test checkpoints and copied budgets only; TaskStatus does not identify scheduler or lock waits."
            }));
        }
        catch (Exception) { /* Diagnostics must not replace the original test exception. */ }
    }
}

public sealed partial class RunTests
{
    private readonly ITestOutputHelper raceOutput;
    public RunTests(ITestOutputHelper output) => raceOutput = output;

    [Fact]
    public void GoalSuffixDiagnosticUsesCopiedEvidenceAndCannotReplaceOriginalFailure()
    {
        var evidence = new GoalSuffixRaceDiagnostics(new(1, 999, 0, 1, 0));
        var unstarted = new Task(() => throw new Exception("Must not run"));
        evidence.Cancel.Start(); evidence.Cancel.Mark("refund-observed", new(1, 998, 0, 1, 0)); evidence.Cancel.Exit();
        string? output = null;
        evidence.TryWrite(value => output = value, 7, "goal-and-cancel", unstarted, Task.CompletedTask, new TimeoutException());
        Assert.NotNull(output);
        using var json = JsonDocument.Parse(output![(output!.IndexOf(' ') + 1)..]);
        var root = json.RootElement;
        Assert.Equal(7, root.GetProperty("iterationZeroBased").GetInt32());
        Assert.Equal(8, root.GetProperty("iterationOneBased").GetInt32());
        Assert.Equal(1000, root.GetProperty("configuredWaitMilliseconds").GetInt32());
        Assert.Equal("Created", root.GetProperty("goal").GetProperty("taskStatus").GetString());
        Assert.Equal("start-not-observed", root.GetProperty("goal").GetProperty("bodyState").GetString());
        Assert.Equal("exited", root.GetProperty("cancel").GetProperty("bodyState").GetString());
        Assert.Equal(998, root.GetProperty("cancel").GetProperty("cachedBudget").GetProperty("ReservedActions").GetInt32());
        Assert.True(output.Length < 4096);
        evidence.TryWrite(_ => throw new InvalidOperationException("output failed"), 7, "goal-and-cancel", unstarted, Task.CompletedTask, new TimeoutException());
        Assert.Equal(TaskStatus.Created, unstarted.Status);
    }
}
