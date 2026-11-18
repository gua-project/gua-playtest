using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed partial class RunTests
{
    // Fixture coordination only. Neither this timeout nor its diagnostic threshold
    // is passed into RunSession, FiniteOperation, cleanup or a provider.
    private Task AwaitFixturePhaseAsync(Task work, string phase, Func<string> capture)
        => AwaitFinalizationWithWatchdogAsync(work, () => $"fixturePhase={phase}; {capture()}");
    private Task<T> AwaitFixturePhaseAsync<T>(Task<T> work, string phase, Func<string> capture)
        => AwaitFinalizationWithWatchdogAsync(work, () => $"fixturePhase={phase}; {capture()}");

    private static async Task JoinFixtureWithoutMaskingAsync(Func<Task> join, Exception? bodyFailure, Action<string> record)
    {
        try { await join(); }
        catch (Exception cleanupFailure) when (bodyFailure is not null)
        {
            // The enclosing catch rethrows its original exception and stack.
            // Retain the secondary failure separately, without exception messages,
            // Data getters or arbitrary provider content in diagnostics.
            try { record($"fixtureJoinFailureType={cleanupFailure.GetType().FullName}; retainedBodyFailureType={bodyFailure.GetType().FullName}"); }
            catch { /* A failed diagnostic sink cannot replace the body failure either. */ }
        }
    }

    [Fact]
    public async Task FixtureJoinPreservesOriginalExceptionAndStackWhenCleanupAlsoFails()
    {
        var primary = new TimeoutException("private-primary-marker");
        var secondary = new InvalidOperationException("private-secondary-marker");
        var records = new List<string>();
        async Task Body()
        {
            Exception? observed = null;
            try { throw primary; }
            catch (Exception error) { observed = error; throw; }
            finally { await JoinFixtureWithoutMaskingAsync(() => Task.FromException(secondary), observed, records.Add); }
        }
        var failure = await Record.ExceptionAsync(Body);
        Assert.Same(primary, failure);
        Assert.Contains("Body", failure!.StackTrace);
        var record = Assert.Single(records);
        Assert.Contains(typeof(InvalidOperationException).FullName!, record);
        Assert.Contains(typeof(TimeoutException).FullName!, record);
        Assert.DoesNotContain("private-", record);
        Assert.True(record.Length < 512);
    }

    [Fact]
    public async Task FixtureJoinFailureIsNotSuppressedWhenTheBodySucceeded()
    {
        var secondary = new InvalidOperationException("join failed");
        var failure = await Record.ExceptionAsync(() => JoinFixtureWithoutMaskingAsync(
            () => Task.FromException(secondary), null, _ => Assert.Fail("Unexpected suppression")));
        Assert.Same(secondary, failure);
    }

    [Fact]
    public async Task FixtureJoinDiagnosticSinkCannotReplaceOriginalFailure()
    {
        var primary = new TimeoutException("original");
        async Task Body()
        {
            Exception? observed = null;
            try { throw primary; }
            catch (Exception error) { observed = error; throw; }
            finally
            {
                await JoinFixtureWithoutMaskingAsync(() => Task.FromException(new IOException()), observed,
                    _ => throw new InvalidOperationException("broken diagnostic sink"));
            }
        }
        Assert.Same(primary, await Record.ExceptionAsync(Body));
    }

    [Fact]
    public async Task FixtureJoinKeepsSuccessfulCleanupSilent()
    {
        await JoinFixtureWithoutMaskingAsync(() => Task.CompletedTask, new TimeoutException(),
            _ => Assert.Fail("Successful join must not report failure"));
        Assert.Equal(42, await AwaitFixturePhaseAsync(Task.FromResult(42), "ready",
            () => "synthetic-completed-work"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FixturePhaseRetainsActualTaskWinnerBeforeObserverResumes(bool workFirst)
    {
        var work = new TaskCompletionSource(); var stop = new TaskCompletionSource();
        var threshold = new TaskCompletionSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        int records = 0;
        var waiting = AwaitFinalizationWatchdogCoreAsync((Task)work.Task, threshold.Task, stop.Task,
            () => "controlled", _ =>
            {
                if (Interlocked.Increment(ref records) != 1) return;
                held.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled observer hold did not release");
            }, () => false);
        var notification = RunBlockingFixture(() => threshold.SetResult());
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The observer is explicitly stopped before it inspects the terminal
            // race. Complete both tasks, then let it inspect the recorded winner.
            if (workFirst) { work.SetResult(); stop.SetResult(); }
            else { stop.SetResult(); work.SetResult(); }
            resume.Set();
            if (workFirst) await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            else await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { resume.Set(); await notification.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
