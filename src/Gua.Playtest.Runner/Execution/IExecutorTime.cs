namespace Gua.Playtest.Runner.Execution;

/// <summary>Per-execution test boundary for independent monotonic time and wake hints.
/// Public entrypoints never select it; a null boundary preserves the native clock/timers.
/// It is independent of the provider IClock and must not derive elapsed time from that provider.</summary>
internal interface IExecutorTime
{
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}
