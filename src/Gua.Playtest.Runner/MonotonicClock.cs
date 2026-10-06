using System.Diagnostics;
using Gua.Playtest.Core;

namespace Gua.Playtest.Runner;

public sealed class MonotonicClock : IClock
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public TimeSpan Elapsed => _clock.Elapsed;
    public async ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        await Task.Delay(duration, cancellationToken);
    }
}
