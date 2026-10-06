using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;

namespace Gua.Playtest.Runner.Conditions;

/// <summary>Single-owner state machine; the Runner serializes notification and timer evaluations.</summary>
public sealed class ConditionSession
{
    private sealed class TimeState
    {
        public TimeSpan? Started;
        public string? Witness;
        public bool Satisfied;
        public bool Expired;
    }
    private sealed record Sample(EvaluationResult Value, ConditionCompletion Completion, string? Witness,
        bool Continuous, TimeSpan? Next);
    private readonly PreparedCondition prepared;
    private readonly IClock clock;
    private readonly TimeSpan origin;
    private TimeSpan last;
    private readonly Dictionary<string, TimeState> states = new(StringComparer.Ordinal);
    public TimeSpan Origin => origin;

    internal ConditionSession(PreparedCondition prepared, IClock clock, TimeSpan? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.prepared = prepared;
        this.clock = clock;
        var current = clock.Elapsed;
        origin = last = boundary ?? current;
        if (origin < TimeSpan.Zero || origin > TimeSpan.MaxValue - TimeSpan.FromDays(2))
            throw new ArgumentOutOfRangeException(nameof(clock));
        if (current < origin) throw new ArgumentOutOfRangeException(nameof(boundary));
    }

    public ConditionEvaluation Evaluate(ConditionObservationUnit unit)
    {
        var now = clock.Elapsed; // one timestamp for the entire group
        return EvaluateCore(unit, now);
    }

    /// <summary>Evaluate at the trusted unit's monotonic capture/boundary timestamp, not its delivery time.
    /// The owner must supply all relevant intervening units; this cannot backdate a hold before its first observation.</summary>
    public ConditionEvaluation EvaluateAt(ConditionObservationUnit unit, TimeSpan evaluatedAt)
    {
        if (evaluatedAt > clock.Elapsed) throw new InvalidOperationException("ConditionObservationInFuture");
        return EvaluateCore(unit, evaluatedAt);
    }

    private ConditionEvaluation EvaluateCore(ConditionObservationUnit unit, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (now < last) throw new InvalidOperationException("ConditionClockRegressed");
        if (now > TimeSpan.MaxValue - TimeSpan.FromDays(2)) throw new InvalidOperationException("ConditionClockOutOfRange");
        last = now;
        var sample = Visit(prepared.Root);
        return new(sample.Value, sample.Completion, now, sample.Next);

        Sample Visit(PreparedCondition.Node node)
        {
            if (node.Kind is "assertion" or "targets") return Leaf(node, unit.Leaves.GetValueOrDefault(node.Path));
            var children = node.Children.Select(Visit).ToArray(); // never short-circuit errors, even after a temporal latch
            if (node.Kind == "time") return Time(node, children[0], now);
            var all = node.Kind == "all";
            var value = all ? TruthLogic.All(children.Select(Effective)) : TruthLogic.Any(children.Select(Effective));
            var completion = all
                ? children.Any(c => c.Completion == ConditionCompletion.Expired) ? ConditionCompletion.Expired
                    : children.All(c => c.Completion == ConditionCompletion.Satisfied) ? ConditionCompletion.Satisfied : ConditionCompletion.Open
                : children.Any(c => c.Completion == ConditionCompletion.Satisfied) ? ConditionCompletion.Satisfied
                    : children.All(c => c.Completion == ConditionCompletion.Expired) ? ConditionCompletion.Expired : ConditionCompletion.Open;
            if (value.Error != EvaluationError.None) completion = ConditionCompletion.Open;
            else if (completion == ConditionCompletion.Open && children.Any(c => c.Completion == ConditionCompletion.Pending)
                && children.All(c => c.Completion != ConditionCompletion.Open)) completion = ConditionCompletion.Pending;
            var witnesses = all ? children : children.Where(c => Effective(c).Truth == TruthValue.True).ToArray();
            var witness = value.Truth == TruthValue.True ? Join(witnesses.Select(c => c.Witness ?? "")) : null;
            bool continuous = witnesses.Length != 0 && witnesses.All(c => c.Continuous && c.Witness is not null);
            return new(value, completion, witness, continuous,
                completion is ConditionCompletion.Satisfied or ConditionCompletion.Expired ? null : Earliest(children.Select(c => c.Next)));
        }
    }

    private static EvaluationResult Effective(Sample sample) => sample.Value.Error == EvaluationError.None && sample.Completion == ConditionCompletion.Expired
        ? EvaluationResult.Known(false) : sample.Value;

    private Sample Time(PreparedCondition.Node node, Sample child, TimeSpan now)
    {
        if (!states.TryGetValue(node.Path, out var state)) states[node.Path] = state = new();
        // Broken required monitoring is not hidden by a previously established fact.
        if (child.Value.Error != EvaluationError.None)
        {
            state.Started = null;
            state.Witness = null;
            return new(child.Value, ConditionCompletion.Pending, null, false, child.Next);
        }
        if (state.Satisfied) return new(EvaluationResult.Known(true), ConditionCompletion.Satisfied, node.Path, true, null);
        if (state.Expired) return new(child.Value.Truth == TruthValue.Unknown ? child.Value : EvaluationResult.Known(false), ConditionCompletion.Expired, null, false, null);
        TimeSpan? deadline = node.Within is { } within ? origin + within : null;
        var actual = Effective(child);
        bool trueNow = actual.Truth == TruthValue.True;
        bool keep = trueNow && child.Continuous && child.Witness is not null && child.Witness == state.Witness;
        if (!keep) { state.Started = null; state.Witness = null; }
        if (trueNow && state.Started is null && (deadline is null || now <= deadline))
        {
            state.Started = now;
            state.Witness = child.Witness;
        }
        if (state.Started is { } started && now - started >= (node.For ?? TimeSpan.Zero))
        {
            state.Satisfied = true;
            return new(EvaluationResult.Known(true), ConditionCompletion.Satisfied, node.Path, true, null);
        }
        // The inclusive deadline allows a true start at exactly the boundary. Expiry is the first later tick.
        if (child.Completion == ConditionCompletion.Expired || (deadline is not null && now > deadline && state.Started is null))
        {
            state.Expired = true;
            return new(actual.Truth == TruthValue.Unknown ? actual : EvaluationResult.Known(false), ConditionCompletion.Expired, null, false, null);
        }
        var wake = state.Started is { } hold ? hold + (node.For ?? TimeSpan.Zero)
            : deadline is { } end ? end + TimeSpan.FromTicks(1) : (TimeSpan?)null;
        return new(actual.Truth == TruthValue.Unknown ? actual : EvaluationResult.Known(false), ConditionCompletion.Pending,
            null, false, Earliest(new[] { wake, child.Next }));
    }

    private static Sample Leaf(PreparedCondition.Node node, ConditionLeafObservation? observation)
    {
        if (observation is null) return Plain(EvaluationResult.Unknown(EvaluationCode.Missing));
        if (string.IsNullOrEmpty(observation.ScopeIdentity) || observation.Targets.Any(t => t is null || string.IsNullOrEmpty(t.Identity))
            || observation.Targets.Select(t => t.Identity).Distinct(StringComparer.Ordinal).Count() != observation.Targets.Count)
            return Plain(EvaluationResult.Violation(EvaluationCode.InvalidObservation));
        if (observation.Unavailable is { } unavailable)
        {
            if (!IsUnavailable(unavailable) || observation.Targets.Count != 0) return Plain(EvaluationResult.Violation(EvaluationCode.InvalidObservation));
            return Plain(EvaluationResult.Unknown(unavailable));
        }
        EvaluationResult result;
        string[] provingTargets = [];
        var count = observation.Targets.Count;
        if (node.Kind == "targets")
        {
            result = node.Operation switch
            {
                "exists" when count > 0 => EvaluationResult.Known(true),
                "notExists" when count > 0 => EvaluationResult.Known(false),
                _ when !observation.SearchComplete => EvaluationResult.Unknown(EvaluationCode.Truncated),
                "exists" => EvaluationResult.Known(false),
                "notExists" => EvaluationResult.Known(true),
                "countEquals" => EvaluationResult.Known(count == node.Count),
                "countNotEquals" => EvaluationResult.Known(count != node.Count),
                "countGreaterThan" => EvaluationResult.Known(count > node.Count),
                "countGreaterThanOrEqual" => EvaluationResult.Known(count >= node.Count),
                "countLessThan" => EvaluationResult.Known(count < node.Count),
                "countLessThanOrEqual" => EvaluationResult.Known(count <= node.Count),
                _ => EvaluationResult.InvalidConfiguration()
            };
        }
        else
        {
            // Even a decisive any or incomplete search cannot hide a malformed available Value.
            var values = observation.Targets.Select(t => t.ValueJson is { } wire ? node.Comparison!.EvaluateJson(wire, t.Catalog)
                : IsUnavailable(t.Unavailable) ? node.Comparison!.EvaluateUnavailable(t.Unavailable)
                : EvaluationResult.Violation(EvaluationCode.InvalidObservation)).ToArray();
            if (node.Quantifier == "any") provingTargets = observation.Targets.Where((_, i) => values[i].Truth == TruthValue.True)
                .Select(t => t.Identity).Order(StringComparer.Ordinal).ToArray();
            var errors = TruthLogic.Any(values.Length == 0 ? new[] { EvaluationResult.Known(false) } : values);
            if (errors.Error != EvaluationError.None) result = errors;
            else if (node.Quantifier == "one" && count > 1) result = EvaluationResult.Violation(EvaluationCode.TargetAmbiguous);
            else if (!observation.SearchComplete && node.Quantifier is "one" or "all" or "none") result = EvaluationResult.Unknown(EvaluationCode.Truncated);
            else if (count == 0) result = observation.SearchComplete ? EvaluationResult.Known(node.Quantifier == "none") : EvaluationResult.Unknown(EvaluationCode.Truncated);
            else result = node.Quantifier switch
            {
                "one" => values[0],
                "all" => TruthLogic.All(values),
                "any" => AddSearchUnknown(TruthLogic.Any(values), observation.SearchComplete),
                "none" => Negate(TruthLogic.Any(values)),
                _ => EvaluationResult.InvalidConfiguration()
            };
        }
        var witness = result.Truth == TruthValue.True ? Join(new[] { node.Path, observation.ScopeIdentity,
            Join(observation.Targets.Select(t => t.Identity).Order(StringComparer.Ordinal)), Join(provingTargets) }) : null;
        return new(result, ConditionCompletion.Open, witness, observation.ContinuousFromPrevious, null);
    }

    private static bool IsUnavailable(EvaluationCode code) => code is EvaluationCode.Unavailable or EvaluationCode.Missing or EvaluationCode.GetterError
        or EvaluationCode.Stale or EvaluationCode.Gap or EvaluationCode.Truncated or EvaluationCode.RegexTimeout;
    private static EvaluationResult AddSearchUnknown(EvaluationResult value, bool complete) => !complete && value.Truth == TruthValue.False
        ? EvaluationResult.Unknown(EvaluationCode.Truncated) : value;
    private static EvaluationResult Negate(EvaluationResult value) => value.Error != EvaluationError.None || value.Truth == TruthValue.Unknown
        ? value : EvaluationResult.Known(value.Truth == TruthValue.False);
    private static Sample Plain(EvaluationResult result) => new(result, ConditionCompletion.Open, null, false, null);
    private static string Join(IEnumerable<string> parts) => string.Concat(parts.Select(s => s.Length + ":" + s));
    private static TimeSpan? Earliest(IEnumerable<TimeSpan?> values) => values.Where(v => v.HasValue).Min();
}
