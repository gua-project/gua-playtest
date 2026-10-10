using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Runner.Conditions;

namespace Gua.Playtest.Runner.Exploration;

public enum MetricDirection { Minimize, Maximize }

/// <summary>Stable configuration identity, never a resolved Runtime ID. Private host input, not Planner feedback.</summary>
public sealed record MilestoneDefinition(string Id, PreparedCondition Condition);

public sealed class MetricDefinition
{
    public string Id { get; }
    public MetricDirection Direction { get; }
    public double MinImprovement { get; }
    public string ReadJson { get; }
    internal PreparedAssertion Validator { get; }
    public MetricDefinition(string id, JsonObject read, MetricDirection direction, double minImprovement,
        AssertionOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !Enum.IsDefined(direction) ||
            !double.IsFinite(minImprovement) || minImprovement <= 0)
            throw new ArgumentException("ProgressDefinitionInvalid");
        var type = read["valueType"]?["type"]?.GetValue<string>();
        if (type is not ("integer" or "number")) throw new ArgumentException("MetricTypeInvalid");
        Id = id; Direction = direction; MinImprovement = minImprovement; ReadJson = read.ToJsonString();
        Validator = PreparedAssertion.Create(new JsonObject
        {
            ["kind"] = "assertion", ["quantifier"] = "one", ["operator"] = "equals",
            ["read"] = read.DeepClone(), ["expected"] = new JsonObject { ["type"] = type, ["value"] = 0 }
        }, options);
    }
}

public sealed class ProgressDefinitions
{
    public IReadOnlyList<MilestoneDefinition> Milestones { get; }
    public IReadOnlyList<MetricDefinition> Metrics { get; }
    public ProgressDefinitions(IEnumerable<MilestoneDefinition> milestones, IEnumerable<MetricDefinition> metrics)
    {
        var m = milestones.ToArray(); var n = metrics.ToArray();
        if (m.Length + n.Length > 1000 || m.Any(x => x is null || x.Condition is null ||
                string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128) || n.Any(x => x is null) ||
            m.Select(x => x.Id).Concat(n.Select(x => x.Id)).Distinct(StringComparer.Ordinal).Count() != m.Length + n.Length)
            throw new ArgumentException("ProgressDefinitionInvalid");
        Milestones = Array.AsReadOnly(m); Metrics = Array.AsReadOnly(n);
    }
}

/// <summary>Every private read belongs to this capture boundary. Missing entries remain Unknown.
/// A comparable situation is a host-certified semantic projection; exclude volatile revision/frame/animation
/// and replaceable runtime IDs. Null means no valid comparison, not an empty situation.</summary>
public sealed record ProgressObservation(TimeSpan CapturedAt,
    IReadOnlyDictionary<string, ConditionObservationUnit> Milestones,
    IReadOnlyDictionary<string, ConditionLeafObservation> Metrics,
    JsonObject? ComparableSituation = null);

public enum ExplorePhase { Exploring, SuspectedStall, Recovering }
public enum StallSignal { None, ProgressNotUpdated, ComparableRepetition, PlannerReported }
public sealed record ProgressSummary(bool Defined, bool Known, bool Improved, bool ObservationViolation,
    StallSignal Signal, ExplorePhase Phase);
