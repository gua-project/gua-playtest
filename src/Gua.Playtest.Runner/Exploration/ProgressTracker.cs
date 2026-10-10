using System.Globalization;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;

namespace Gua.Playtest.Runner.Exploration;

/// <summary>One Run's private history. Recovery and target replacement never recreate certified best values.
/// Call on the serialized owner, after whole approved work and its observation/release boundary.</summary>
public sealed class ProgressTracker
{
    private sealed class MilestoneState(MilestoneDefinition definition, IClock clock, TimeSpan origin)
    {
        public readonly MilestoneDefinition Definition = definition;
        public readonly ConditionSession Session = definition.Condition.Start(clock, origin);
        public bool Reached, SeenFalse;
    }
    private sealed class MetricState(MetricDefinition definition)
    {
        public readonly MetricDefinition Definition = definition;
        public double? Best;
    }
    private sealed record Repetition(JsonObject Before, JsonObject Decision, JsonObject After);
    private readonly MilestoneState[] milestones;
    private readonly MetricState[] metrics;
    private readonly long actionLimit, repeatLimit;
    private readonly int maxHistory, maxObservationBytes, maxObservationNodes;
    private readonly List<Repetition> history = [];
    private readonly IClock clock;
    private TimeSpan lastCapture;
    private long sinceProgress;
    private bool pendingImprovement;
    private JsonObject? situation;
    public ExplorePhase Phase { get; private set; }
    public StallSignal Signal { get; private set; }

    public ProgressTracker(ProgressDefinitions definitions, ResourceLimits limits, IClock conditionClock,
        ProgressObservation initial)
    {
        if (limits.StagnationActionLimit <= 0 || limits.StagnationRepeatLimit <= 0 ||
            limits.StagnationActionLimit > 100000 || limits.StagnationRepeatLimit > 100000 ||
            limits.MaxObservationBytes is <= 0 or > 1048576 || limits.MaxObservationNodes is <= 0 or > 100000)
            throw new ArgumentException("ExploreLimitsInvalid");
        clock = conditionClock; lastCapture = initial.CapturedAt;
        actionLimit = limits.StagnationActionLimit; repeatLimit = limits.StagnationRepeatLimit;
        maxHistory = 1000;
        maxObservationBytes = (int)limits.MaxObservationBytes; maxObservationNodes = (int)limits.MaxObservationNodes;
        milestones = definitions.Milestones.Select(x => new MilestoneState(x, clock, initial.CapturedAt)).ToArray();
        metrics = definitions.Metrics.Select(x => new MetricState(x)).ToArray();
        Initial = Evaluate(initial, null, initial: true);
    }
    public ProgressSummary Initial { get; }

    public ProgressSummary ObserveBoundary(ProgressObservation observation, JsonObject? confirmedAction = null,
        bool plannerReported = false)
    {
        var summary = Evaluate(observation, confirmedAction, initial: false);
        if (plannerReported && !summary.Improved) Suspect(StallSignal.PlannerReported);
        return summary with { Signal = Signal, Phase = Phase };
    }
    public ProgressSummary ObserveSample(ProgressObservation observation) => Evaluate(observation, null, false, sampleOnly: true);

    public void BeginRecovery()
    {
        if (Phase == ExplorePhase.SuspectedStall) Phase = ExplorePhase.Recovering;
    }

    private ProgressSummary Evaluate(ProgressObservation observation, JsonObject? action, bool initial, bool sampleOnly = false)
    {
        if (observation.CapturedAt < lastCapture || observation.CapturedAt > clock.Elapsed)
            throw new ArgumentException("ProgressCaptureInvalid");
        lastCapture = observation.CapturedAt;
        bool defined = milestones.Length + metrics.Length != 0, known = defined, improved = false, violation = false;
        foreach (var state in milestones)
        {
            var unit = observation.Milestones.GetValueOrDefault(state.Definition.Id) ?? new ConditionObservationUnit([]);
            var result = state.Session.EvaluateAt(unit, observation.CapturedAt).Evaluation;
            violation |= result.Error != EvaluationError.None;
            known &= result.Error == EvaluationError.None && result.Truth != TruthValue.Unknown;
            if (result.Error != EvaluationError.None || result.Truth == TruthValue.Unknown) continue;
            if (result.Truth == TruthValue.False) state.SeenFalse = true;
            if (result.Truth == TruthValue.True && !state.Reached)
            {
                improved |= !initial && state.SeenFalse;
                // An initially true or first unknown-to-true milestone is a baseline, not proven forward motion.
                state.Reached = true;
            }
        }
        foreach (var state in metrics)
        {
            var leaf = observation.Metrics.GetValueOrDefault(state.Definition.Id);
            if (leaf is not null)
            {
                violation |= string.IsNullOrWhiteSpace(leaf.ScopeIdentity) || leaf.Targets.Count > 1 || leaf.Targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.Identity)) ||
                    leaf.Targets.Select(x => x?.Identity).Distinct(StringComparer.Ordinal).Count() != leaf.Targets.Count ||
                    (leaf.Unavailable is { } unavailable && (leaf.Targets.Count != 0 || !IsUnavailable(unavailable)));
                // Even an incomplete search cannot hide a malformed returned Value.
                foreach (var sample in leaf.Targets)
                    if (sample is null) violation = true;
                    else if (sample.ValueJson is not null)
                        violation |= state.Definition.Validator.EvaluateJson(sample.ValueJson, sample.Catalog).Error != EvaluationError.None;
                    else violation |= !IsUnavailable(sample.Unavailable);
            }
            if (leaf is null || leaf.Unavailable.HasValue || !leaf.SearchComplete || leaf.Targets.Count != 1 ||
                string.IsNullOrWhiteSpace(leaf.ScopeIdentity) || leaf.Targets[0] is null || string.IsNullOrWhiteSpace(leaf.Targets[0].Identity) ||
                leaf.Targets[0].ValueJson is null)
            { known = false; continue; }
            var target = leaf.Targets[0];
            var result = state.Definition.Validator.EvaluateJson(target.ValueJson!, target.Catalog);
            violation |= result.Error != EvaluationError.None;
            if (result.Error != EvaluationError.None) { known = false; continue; }
            var json = JsonNode.Parse(target.ValueJson!)!.AsObject(); // original Value already validated by Core
            var value = double.Parse(json["value"]!.ToJsonString(), CultureInfo.InvariantCulture);
            if (state.Best is not { } best) state.Best = value;
            else if ((state.Definition.Direction == MetricDirection.Minimize ? best - value : value - best)
                     >= state.Definition.MinImprovement)
            { state.Best = value; improved |= !initial; }
        }
        var current = BoundedSituation(observation.ComparableSituation);
        if (sampleOnly) pendingImprovement |= improved;
        else { improved |= pendingImprovement; pendingImprovement = false; }
        if (improved)
        {
            sinceProgress = 0; history.Clear(); Phase = ExplorePhase.Exploring; Signal = StallSignal.None;
        }
        if (sampleOnly)
        {
            if (!known) sinceProgress = 0;
            if (current is null) { history.Clear(); situation = null; }
            return new(defined, known, improved, violation, Signal, Phase);
        }
        if (!improved && action is not null)
        {
            if (known) { if (sinceProgress < long.MaxValue) sinceProgress++; }
            else sinceProgress = 0;
            if (situation is not null && current is not null)
            {
                var boundedAction = BoundedSituation(action);
                if (boundedAction is not null)
                {
                    history.Add(new(situation, boundedAction, current));
                    if (history.Count > maxHistory) history.RemoveAt(0);
                    if (Repeats()) Suspect(StallSignal.ComparableRepetition);
                }
                else history.Clear();
            }
            else history.Clear();
            if (known && sinceProgress >= actionLimit) Suspect(StallSignal.ProgressNotUpdated);
        }
        // Missing captures cannot bridge a comparable operation sequence, even during observe/wait.
        if (current is null) history.Clear();
        situation = current;
        return new(defined, known, improved, violation, Signal, Phase);
    }

    private JsonObject? BoundedSituation(JsonObject? value)
    {
        if (value is null) return null;
        var stack = new Stack<(JsonNode Node, int Depth)>(); stack.Push((value, 0)); int count = 0;
        while (stack.TryPop(out var entry))
        {
            if (++count > maxObservationNodes || entry.Depth > 32) return null;
            if (entry.Node is JsonObject map) foreach (var item in map) { if (item.Value is not null) stack.Push((item.Value, entry.Depth + 1)); }
            else if (entry.Node is JsonArray array) foreach (var item in array) { if (item is not null) stack.Push((item, entry.Depth + 1)); }
        }
        if (System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString()) > maxObservationBytes) return null;
        return (JsonObject)value.DeepClone();
    }

    private bool Repeats()
    {
        // Compare complete semantic situations and complete operation sequences, including oscillating routes.
        // At least two occurrences are needed even for a configured threshold of one.
        var repeats = Math.Max(2, repeatLimit);
        for (int period = 1; period <= history.Count / repeats; period++)
        {
            var start = history.Count - (int)(period * repeats); bool same = true;
            for (int i = start + period; i < history.Count && same; i++)
            {
                var a = history[i]; var b = history[start + (i - start) % period];
                same = JsonNode.DeepEquals(a.Before, b.Before) && JsonNode.DeepEquals(a.Decision, b.Decision) &&
                       JsonNode.DeepEquals(a.After, b.After);
            }
            if (same) return true;
        }
        return false;
    }
    private void Suspect(StallSignal signal)
    {
        if (Phase == ExplorePhase.Exploring) { Signal = signal; Phase = ExplorePhase.SuspectedStall; }
    }
    private static bool IsUnavailable(EvaluationCode code) => code is EvaluationCode.Unavailable or EvaluationCode.Missing
        or EvaluationCode.GetterError or EvaluationCode.Stale or EvaluationCode.Gap or EvaluationCode.Truncated or EvaluationCode.RegexTimeout;
}
