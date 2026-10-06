using Gua.Playtest.Core;

namespace Gua.Playtest.Foundation.Tests;

// Test-only ports. Never shipped or selected by the product CLI.
internal sealed class FakeClock : IClock
{
    public TimeSpan Elapsed { get; private set; }
    public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Elapsed += duration;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeObservationSource : IObservationSource<int>
{
    public int Captures { get; private set; }
    public ValueTask<int> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(++Captures);
    }
}

internal sealed class FakePlanner : IPlanner<int, int>
{
    public ValueTask<int> DecideAsync(int input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(input);
    }
}

internal sealed class FakeHostSession : IHostSession
{
    public bool OwnedResourceReleased { get; private set; }
    public bool AttachedProcessRunning => true;
    public int CleanupCount { get; private set; }
    public ValueTask CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OwnedResourceReleased) { OwnedResourceReleased = true; CleanupCount++; }
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => CleanupAsync(CancellationToken.None);
}

internal sealed class FakeValidator(Func<string, CancellationToken, ValidationResult> validate) : IStaticValidator
{
    public int Calls { get; private set; }
    public ValueTask<ValidationResult> ValidateAsync(string document, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(validate(document, cancellationToken));
    }
}
