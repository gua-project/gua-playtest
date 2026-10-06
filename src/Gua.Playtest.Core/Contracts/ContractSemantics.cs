using System.Text.Json.Nodes;

namespace Gua.Playtest.Core.Contracts;

internal static class ContractSemantics
{
    public static void Validate(JsonObject document)
    {
        foreach (var field in new[] { "setup", "initial" }) if (document[field] is { } condition) Walk(condition);
        if (document["goal"] is JsonObject goal)
            foreach (var field in new[] { "success", "failure" }) if (goal[field] is { } condition) Walk(condition);
        if (document["checkpoints"] is JsonArray checkpoints)
            foreach (var checkpoint in checkpoints) Walk(checkpoint!["condition"]!);
        if (document["permissions"]?["reads"] is JsonArray reads) foreach (var read in reads) CheckRead(read!.AsObject());
        switch (Text(document, "kind"))
        {
            case "environment":
                if (!Uri.TryCreate(Text(document["connection"]!, "endpoint"), UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(endpoint.Host)
                    || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                    throw new ContractException("EndpointInvalid");
                break;
            case "plannerDecision":
                var decision = document["decision"]!.AsObject();
                if (decision["condition"] is { } condition) Walk(condition);
                if (decision["reads"] is JsonArray requested) foreach (var read in requested) CheckRead(read!.AsObject());
                CheckDecision(decision); break;
            case "plannerInput":
                if (Text(document, "basedOnObservationId") != Text(document["observation"]!, "observationId")) throw new ContractException("IdentityMismatch");
                var sourceNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var source in document["observation"]!["sources"]!.AsArray())
                    if (!sourceNames.Add(Text(source!, "source"))) throw new ContractException("ObservationShapeInvalid");
                foreach (var read in document["observation"]!["reads"]!.AsArray())
                {
                    CheckRead(read!["read"]!.AsObject());
                    if (!sourceNames.Contains(Text(read["read"]!["target"]!, "source"))) throw new ContractException("ObservationShapeInvalid");
                    if ((Text(read!, "status") == "available") != (read!["value"] is not null)) throw new ContractException("ObservationShapeInvalid");
                    if (document["observation"]!["complete"]!.GetValue<bool>() && Text(read!, "status") is "omitted" or "truncated" or "stale" or "gap") throw new ContractException("ObservationShapeInvalid");
                    if (read["value"] is { } value)
                        foreach (var field in new[] { "type", "elementType", "enumType" })
                            if (Text(read["read"]!["valueType"]!, field) != Text(value, field)) throw new ContractException("TypeMismatch");
                }
                break;
        }
    }

    private static void Walk(JsonNode node)
    {
        if (node is JsonObject map)
        {
            if (Text(map, "kind") == "assertion") CheckAssertion(map);
            if (Text(map, "kind") == "targets")
            {
                bool count = Text(map, "operator").StartsWith("count", StringComparison.Ordinal);
                if (count != map.ContainsKey("count")) throw new ContractException("ConditionShapeInvalid");
            }
            if (Text(map, "kind") == "time" && !map.ContainsKey("withinMilliseconds") && !map.ContainsKey("forMilliseconds"))
                throw new ContractException("ConditionShapeInvalid");
            if (map.ContainsKey("region")) CheckRead(map);
            foreach (var pair in map) if (pair.Value is not null) Walk(pair.Value);
        }
        else if (node is JsonArray list) foreach (var child in list) if (child is not null) Walk(child);
    }

    internal static void CheckRead(JsonObject read)
    {
        if (Text(read, "region") != "standard") return;
        var source = Text(read["target"]!, "source"); var field = Text(read, "field");
        var declared = Text(read["valueType"]!, "type");
        string? actual = source == "ui" ? field switch
        {
            "visible" or "enabled" or "state.focused" or "state.hovered" or "state.pressed" or "state.checked" or "state.selected" => "bool",
            "state.caretPosition" or "state.selectionStart" or "state.selectionEnd" or "state.selectedIndex" => "integer",
            "bounds.x" or "bounds.y" or "bounds.w" or "bounds.h" or "state.scrollX" or "state.scrollY" or "state.scrollMaxX" or "state.scrollMaxY" or "state.rangeValue" or "state.rangeMin" or "state.rangeMax" => "number",
            "id" or "parentId" or "role" or "label" or "text" => "string",
            "value" or "state.value" => null,
            _ => throw new ContractException("UnknownStandardField")
        } : field switch
        {
            "visibleToPlayer" or "active" => "bool",
            "position.x" or "position.y" or "position.z" => "number",
            "id" or "parentId" or "kind" or "label" or "description" or "space" or "domainId" or "relatedUiNodeId" or "agentExposure" => "string",
            "tags" => "list",
            _ when field.StartsWith("state.", StringComparison.Ordinal) && field.Length > 6 => null,
            _ => throw new ContractException("UnknownStandardField")
        };
        if (actual is not null && declared != actual) throw new ContractException("TypeMismatch");
        if (field == "tags" && Text(read["valueType"]!, "elementType") != "string") throw new ContractException("TypeMismatch");
        if (actual is null && declared is not ("bool" or "integer" or "number" or "string")) throw new ContractException("TypeMismatch");
    }

    internal static void CheckAssertion(JsonObject assertion)
    {
        var type = assertion["read"]!["valueType"]!;
        var expected = assertion["expected"];
        var op = Text(assertion, "operator"); var kind = Text(type, "type");
        bool noExpected = op is "isEmpty" or "isNotEmpty";
        if (noExpected == assertion.ContainsKey("expected")) throw new ContractException("ConditionShapeInvalid");
        if ((op == "approximatelyEquals") != assertion.ContainsKey("tolerance")) throw new ContractException("ConditionShapeInvalid");
        if (op is "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual" or "approximatelyEquals")
        {
            if (kind is not ("integer" or "number") || (op == "approximatelyEquals" && kind != "number")) throw new ContractException("TypeMismatch");
        }
        if (op is "startsWith" or "endsWith" or "matches" && kind != "string") throw new ContractException("TypeMismatch");
        if (op.Contains("Sequence", StringComparison.Ordinal) || op == "sequenceEquals")
            if (kind != "list") throw new ContractException("TypeMismatch");
        if (noExpected || op.StartsWith("count", StringComparison.Ordinal) || op is "notContains" or "containsAll" or "containsAny")
            if (kind is not ("list" or "set")) throw new ContractException("TypeMismatch");
        if (op == "contains" && kind is not ("string" or "list" or "set")) throw new ContractException("TypeMismatch");
        if (noExpected) return;
        if (expected is null) throw new ContractException("ConditionShapeInvalid");
        string requiredKind = op.StartsWith("count", StringComparison.Ordinal) ? "integer" : op is "contains" or "notContains" && kind is "list" or "set" ? Text(type, "elementType") : kind;
        if (op is "containsAll" or "containsAny")
        {
            if (Text(expected, "type") is not ("list" or "set")) throw new ContractException("TypeMismatch");
        }
        else if (Text(expected, "type") != requiredKind) throw new ContractException("TypeMismatch");
        if (op.StartsWith("count", StringComparison.Ordinal))
        {
            if (Assertions.ValueReader.ExactInteger(expected["value"]!.ToJsonString()) < 0) throw new ContractException("TypeMismatch");
        }
        else
        {
            if (expected["elementType"] is not null && Text(expected, "elementType") != Text(type, "elementType")) throw new ContractException("TypeMismatch");
            if (Text(expected, "enumType") != Text(type, "enumType")) throw new ContractException("TypeMismatch");
        }
    }

    private static void CheckDecision(JsonObject decision)
    {
        if (Text(decision, "kind") != "execute") return;
        if (Text(decision, "mode") == "timed")
        {
            var segment = decision["segment"]!;
            var duration = segment["durationMilliseconds"]!.GetValue<long>();
            long last = -1;
            foreach (var input in segment["inputs"]!.AsArray())
            {
                var offset = input!["offsetMilliseconds"]!.GetValue<long>();
                if (offset < last || offset > duration) throw new ContractException("SegmentTimingInvalid");
                if (input["kind"]!.GetValue<int>() == 6 || (input["kind"]!.GetValue<int>() == 4 && input["operation"]!.GetValue<int>() == 9)) throw new ContractException("ActionForbidden");
                last = offset;
            }
        }
        else
        {
            var action = decision["action"]!; var kind = Text(action, "kind");
            if (kind == "raw")
            {
                var input = action["input"]!;
                if (input["offsetMilliseconds"]!.GetValue<long>() != 0 || input["kind"]!.GetValue<int>() is 1 or 5 or 6
                    || input["operation"]!.GetValue<int>() == 9) throw new ContractException("ActionForbidden");
            }
            else
            {
                var op = Text(action, "operation");
                bool takesValue = op is "set" or "set_value" or "set_checked" or "select" or "scroll" or "press_key";
                bool hasValue = action["value"] is not null;
                bool hasSecret = action["secret"] is not null;
                if ((takesValue && hasValue == hasSecret) || (!takesValue && (hasValue || hasSecret))) throw new ContractException("ActionShapeInvalid");
                if (hasSecret && op is not ("set" or "set_value")) throw new ContractException("ActionShapeInvalid");
                if (op == "set_checked" && action["value"]?.GetValueKind() != System.Text.Json.JsonValueKind.True && action["value"]?.GetValueKind() != System.Text.Json.JsonValueKind.False)
                    throw new ContractException("TypeMismatch");
                if (kind == "ui" && hasValue)
                {
                    var value = action["value"]!;
                    if (op is "set_value" or "select" or "press_key"
                        && (value.GetValueKind() != System.Text.Json.JsonValueKind.String
                            || (op is "select" or "press_key" && value.GetValue<string>().Length == 0)))
                        throw new ContractException("TypeMismatch");
                    if (op == "scroll" && value is not JsonObject)
                        throw new ContractException("TypeMismatch");
                }
            }
        }
    }

    internal static string Text(JsonNode node, string property) => node[property]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? node[property]!.GetValue<string>() : "";
}
