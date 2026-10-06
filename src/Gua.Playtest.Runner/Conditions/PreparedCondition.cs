using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Conditions;

/// <summary>Immutable prepared tree. Sessions own history; every leaf is validated before a session can start.</summary>
public sealed class PreparedCondition
{
    internal sealed record Node(string Kind, string Path, Node[] Children, PreparedAssertion? Comparison,
        string Operation, string Quantifier, long Count, TimeSpan? Within, TimeSpan? For);
    internal Node Root { get; }
    public IReadOnlyList<ConditionLeafRequest> Leaves { get; }
    private PreparedCondition(Node root, List<ConditionLeafRequest> leaves) => (Root, Leaves) = (root, leaves.AsReadOnly());

    public static PreparedCondition Create(JsonObject condition, AssertionOptions options, EnumCatalogSnapshot? catalog = null)
    {
        ConditionStructure.Validate(condition);
        var comparisons = AssertionPreparation.Prepare(condition, options, catalog).ToDictionary(x => x.ConditionPath, x => x.Comparison, StringComparer.Ordinal);
        var leaves = new List<ConditionLeafRequest>();
        return new PreparedCondition(Build(condition, "$"), leaves);

        Node Build(JsonObject node, string path)
        {
            var kind = node["kind"]!.GetValue<string>();
            var children = kind is "all" or "any" ? node["conditions"]!.AsArray().Select((c, i) => Build(c!.AsObject(), path + "/conditions/" + i)).ToArray()
                : kind == "time" ? new[] { Build(node["condition"]!.AsObject(), path + "/condition") } : [];
            if (kind is "assertion" or "targets") leaves.Add(new(path, node.ToJsonString()));
            long count = 0;
            if (node["count"] is { } countNode)
            {
                // schema accepts mathematical integer lexemes (1.0/1e0); never round via binary64.
                count = ConditionStructure.ReadNonnegativeInteger(countNode, 9007199254740991);
            }
            return new(kind, path, children, comparisons.GetValueOrDefault(path), node["operator"]?.GetValue<string>() ?? "",
                node["quantifier"]?.GetValue<string>() ?? "", count, Duration("withinMilliseconds"), Duration("forMilliseconds"));

            TimeSpan? Duration(string name)
            {
                if (node[name] is not { } value) return null;
                return TimeSpan.FromTicks(ConditionStructure.ReadNonnegativeInteger(value, 86400000) * TimeSpan.TicksPerMillisecond);
            }
        }
    }

    /// <summary>Call at Running or wait-point arrival. There is no credit for unmonitored earlier time.</summary>
    public ConditionSession Start(IClock clock) => new(this, clock);
}
