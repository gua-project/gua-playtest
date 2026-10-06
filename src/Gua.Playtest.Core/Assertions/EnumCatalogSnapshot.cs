using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Core.Assertions;

/// <summary>Immutable native-free snapshot of the pinned Gua enum-catalog-v1 wire contract.</summary>
public sealed class EnumCatalogSnapshot
{
    private readonly Dictionary<string, HashSet<string>> definitions;
    private EnumCatalogSnapshot(Dictionary<string, HashSet<string>> definitions) => this.definitions = definitions;

    public static EnumCatalogSnapshot Create(JsonObject catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            ContractSchemas.Validate("enum-catalog-v1.schema.json", catalog);
            var definitions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var definition in catalog["enums"]!.AsArray())
            {
                var name = definition!["enumType"]!.GetValue<string>();
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in definition["members"]!.AsArray())
                {
                    var text = member!.GetValue<string>();
                    ValueReader.CheckUnicode(text);
                    if (!members.Add(text)) throw new ContractException("EnumDuplicate");
                }
                if (definitions.TryGetValue(name, out var prior) && !prior.SetEquals(members)) throw new ContractException("EnumConflict");
                definitions.TryAdd(name, members);
            }
            return new(definitions);
        }
        catch (Exception ex) when (ValueReader.IsValueException(ex))
        {
            throw new AssertionConfigurationException(ex is ContractException ce ? ce.Code : "EnumCatalogInvalid");
        }
    }

    internal void RequireType(string name)
    {
        if (!definitions.ContainsKey(name)) throw new ContractException("EnumUnknown");
    }
    internal void RequireMember(string name, string member)
    {
        RequireType(name);
        if (!definitions[name].Contains(member)) throw new ContractException("EnumMemberInvalid");
    }
    internal bool HasSameDefinition(EnumCatalogSnapshot? other, string name)
        => other is not null && definitions.TryGetValue(name, out var members)
            && other.definitions.TryGetValue(name, out var otherMembers) && members.SetEquals(otherMembers);
}
