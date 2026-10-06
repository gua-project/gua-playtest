using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gua.Playtest.Core.Contracts;

public sealed record PlannerExchangeResult(string Code, ContractDocument? Document)
{
    public bool IsValid => Code == "Valid";
}

/// <summary>Bounded completed JSON exchange. No file, network, secret or execution authority.</summary>
public static class PlannerExchange
{
    public static PlannerExchangeResult Validate(byte[] completedJson, string expectedKind)
    {
        if (expectedKind is not ("plannerInput" or "plannerDecision")) throw new ArgumentException("PlannerKindRequired");
        try
        {
            var json = ContractDecoder.Decode(completedJson, ".json");
            if (ContractSemantics.Text(json, "kind") != expectedKind) return new("SchemaInvalid", null);
            ContractSchemas.ValidateDocument(json);
            ContractSemantics.Validate(json);
            ContractDocument document = expectedKind == "plannerInput"
                ? json.Deserialize<PlannerInputDocument>(ContractJson.Options)!
                : json.Deserialize<PlannerDecisionDocument>(ContractJson.Options)!;
            return new("Valid", document);
        }
        catch (ContractException e) { return new(e.Code, null); }
        catch (JsonException) { return new("MalformedDocument", null); }
        catch (InvalidOperationException) { return new("SchemaInvalid", null); }
        catch (OverflowException) { return new("NonFiniteNumber", null); }
    }
}
