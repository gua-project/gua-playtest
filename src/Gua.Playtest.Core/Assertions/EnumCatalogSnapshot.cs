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
            var structural = catalog.DeepClone().AsObject();
            if (structural["enums"] is not JsonArray entries) throw new ContractException("EnumCatalogInvalid");
            var definitions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry is not JsonObject definition || definition["enumType"] is not JsonValue nameValue
                    || definition["members"] is not JsonArray memberArray || memberArray.Count == 0)
                    throw new ContractException("EnumCatalogInvalid");
                var name = nameValue.GetValue<string>();
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in memberArray)
                {
                    if (member is not JsonValue memberValue) throw new ContractException("EnumCatalogInvalid");
                    var text = memberValue.GetValue<string>();
                    if (text.Length == 0) throw new ContractException("EnumCatalogInvalid");
                    ValueReader.CheckUnicode(text);
                    if (!members.Add(text)) throw new ContractException("EnumDuplicate");
                }
                if (definitions.TryGetValue(name, out var prior) && !prior.SetEquals(members)) throw new ContractException("EnumConflict");
                definitions.TryAdd(name, members);
                // Every member and uniqueness were validated linearly above. Keep minItems and all
                // object/name constraints in the schema without its quadratic uniqueItems scan.
                definition["members"] = new JsonArray("validated-member");
            }
            ContractSchemas.Validate("enum-catalog-v1.schema.json", structural);
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
