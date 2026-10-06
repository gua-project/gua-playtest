using System.Collections.ObjectModel;
using Gua.Playtest.Core.Assertions;

namespace Gua.Playtest.Runner.Conditions;

/// <summary>Identity includes the source lifetime/epoch and registration identity. ValueJson is original wire text.</summary>
public sealed record ConditionTargetObservation(string Identity, string? ValueJson = null,
    EvaluationCode Unavailable = EvaluationCode.Unavailable, EnumCatalogSnapshot? Catalog = null);

/// <summary>Trusted observation evidence for one leaf in one evaluation unit, never planner-provided.
/// ContinuousFromPrevious means coverage of the entire interval through this unit, not merely equal endpoint Values.</summary>
public sealed class ConditionLeafObservation
{
    public string ScopeIdentity { get; }
    public bool SearchComplete { get; }
    public bool ContinuousFromPrevious { get; }
    public IReadOnlyList<ConditionTargetObservation> Targets { get; }
    public EvaluationCode? Unavailable { get; }

    public ConditionLeafObservation(string scopeIdentity, bool searchComplete,
        IEnumerable<ConditionTargetObservation> targets, bool continuousFromPrevious = false, EvaluationCode? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ScopeIdentity = scopeIdentity;
        SearchComplete = searchComplete;
        ContinuousFromPrevious = continuousFromPrevious;
        Targets = Array.AsReadOnly(targets.ToArray());
        Unavailable = unavailable;
    }
}

/// <summary>All leaves are from the same trusted capture/boundary. Omitted leaves are Unknown, never cached.</summary>
public sealed class ConditionObservationUnit
{
    public IReadOnlyDictionary<string, ConditionLeafObservation> Leaves { get; }
    public ConditionObservationUnit(IEnumerable<KeyValuePair<string, ConditionLeafObservation>> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        Leaves = new ReadOnlyDictionary<string, ConditionLeafObservation>(leaves.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
}

public enum ConditionCompletion { Open, Pending, Satisfied, Expired }

/// <summary>No Values, selectors, target identities or exception text. Expired is local impossibility, not a Run outcome.</summary>
public sealed record ConditionEvaluation(EvaluationResult Evaluation, ConditionCompletion Completion,
    TimeSpan EvaluatedAt, TimeSpan? NextEvaluationAt);

/// <summary>Trusted adapter requests. DefinitionJson contains configured read/target only; do not project it to a planner.</summary>
public sealed record ConditionLeafRequest(string ConditionPath, string DefinitionJson);
