using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Cli;

/// <summary>Offline reductions only. Host policy/capability intersection must be applied before execution.</summary>
public static class EffectiveExecution
{
    public sealed record Ceiling(long Value, long Scenario, long Environment, long? Cli);
    public sealed record Policy(ResourceLimits Limits, Ceiling MaxActions, Ceiling MaxDurationMilliseconds,
        string[] ActionIds, string[] DeniedActionIds, bool HostVerified);

    public static Policy Resolve(ScenarioDocument scenario, EnvironmentDocument environment,
        long? maxActions = null, long? maxDurationMilliseconds = null, IEnumerable<string>? deniedActionIds = null)
    {
        if (maxActions <= 0 || maxDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(maxActions));
        var actions = new Ceiling(Math.Min(Math.Min(scenario.Constraints.MaxActions, environment.Limits.MaxActions), maxActions ?? long.MaxValue),
            scenario.Constraints.MaxActions, environment.Limits.MaxActions, maxActions);
        var duration = new Ceiling(Math.Min(Math.Min(scenario.Constraints.MaxDurationMilliseconds, environment.Limits.MaxDurationMilliseconds), maxDurationMilliseconds ?? long.MaxValue),
            scenario.Constraints.MaxDurationMilliseconds, environment.Limits.MaxDurationMilliseconds, maxDurationMilliseconds);
        var deny = (deniedActionIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var allowed = environment.Permissions.ActionIds.Where(id => scenario.Constraints.AllowedActionIds is null ||
            scenario.Constraints.AllowedActionIds.Contains(id, StringComparer.Ordinal)).Except(deny, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(environment.Limits with { MaxActions = actions.Value, MaxDurationMilliseconds = duration.Value },
            actions, duration, allowed, deny, false);
    }
}
