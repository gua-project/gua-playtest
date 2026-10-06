using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;

namespace Gua.Playtest.Runner;

public sealed record PreparedAssertionLeaf(string ConditionPath, PreparedAssertion Comparison);

/// <summary>Trusted Runner preparation: compile every comparison before any condition branch can succeed.</summary>
public static class AssertionPreparation
{
    public static IReadOnlyList<PreparedAssertionLeaf> Prepare(JsonObject condition, AssertionOptions options, EnumCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var leaves = new List<PreparedAssertionLeaf>();
        Visit(condition, "$", 0);
        return leaves.AsReadOnly();

        void Visit(JsonObject node, string path, int depth)
        {
            if (depth > 64) throw new AssertionConfigurationException("ConditionTooDeep");
            switch (node["kind"]?.GetValue<string>())
            {
                case "assertion": leaves.Add(new(path, PreparedAssertion.Create(node, options, catalog))); break;
                case "all":
                case "any":
                    if (node["conditions"] is not JsonArray children || children.Count is < 1 or > 100) throw new AssertionConfigurationException("ConditionShapeInvalid");
                    for (int i = 0; i < children.Count; i++)
                    {
                        if (children[i] is not JsonObject child) throw new AssertionConfigurationException("ConditionShapeInvalid");
                        Visit(child, path + "/conditions/" + i, depth + 1);
                    }
                    break;
                case "time":
                    if (node["condition"] is not JsonObject inner) throw new AssertionConfigurationException("ConditionShapeInvalid");
                    Visit(inner, path + "/condition", depth + 1); break;
                case "targets": break; // selector/target count semantics remain #5's responsibility
                default: throw new AssertionConfigurationException("ConditionShapeInvalid");
            }
        }
    }
}
