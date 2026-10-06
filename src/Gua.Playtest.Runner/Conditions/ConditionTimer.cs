using Gua.Playtest.Core;

namespace Gua.Playtest.Runner.Conditions;

/// <summary>Timer wake-up only. The owner races this with notifications/cancellation/global deadlines,
/// cancels obsolete waits, and captures fresh continuity evidence before evaluating.</summary>
public static class ConditionTimer
{
    public static ValueTask WaitAsync(IClock clock, ConditionEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        if (evaluation.NextEvaluationAt is not { } next) throw new InvalidOperationException("ConditionHasNoTimer");
        var now = clock.Elapsed;
        if (now < evaluation.EvaluatedAt) throw new InvalidOperationException("ConditionClockRegressed");
        return clock.DelayAsync(next > now ? next - now : TimeSpan.Zero, cancellationToken);
    }
}
