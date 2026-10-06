using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Core.Assertions;

/// <summary>A fully validated, immutable comparison leaf. Selector and time state belong to the caller.</summary>
public sealed class PreparedAssertion
{
    private readonly ValueTypeIdentity type;
    private readonly string operation;
    private readonly AssertionValue? expected;
    private readonly double tolerance;
    private readonly Regex? regex;
    private readonly EnumCatalogSnapshot? catalog;

    private PreparedAssertion(ValueTypeIdentity type, string operation, AssertionValue? expected, double tolerance, Regex? regex, EnumCatalogSnapshot? catalog)
        => (this.type, this.operation, this.expected, this.tolerance, this.regex, this.catalog) = (type, operation, expected, tolerance, regex, catalog);

    public static PreparedAssertion CreateJson(string assertionJson, AssertionOptions options, EnumCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(assertionJson);
        try { return Create(ContractDecoder.Decode(new UTF8Encoding(false, true).GetBytes(assertionJson), ".json"), options, catalog); }
        catch (Exception ex) when (ValueReader.IsValueException(ex))
        { throw new AssertionConfigurationException(ex is ContractException ce ? ce.Code : "AssertionInvalid"); }
    }

    public static PreparedAssertion Create(JsonObject assertion, AssertionOptions options, EnumCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            if (options.RegexTimeoutMilliseconds is <= 0 or int.MaxValue || options.RegexMaxPatternLength <= 0) throw new ContractException("RegexLimitsInvalid");
            var expected = assertion["expected"] is JsonObject value ? ValueReader.Read(value, catalog) : null;
            var schemaAssertion = assertion.DeepClone().AsObject();
            if (expected is not null) schemaAssertion["expected"] = ValueReader.SchemaValue(assertion["expected"]!.AsObject(), expected);
            double tolerance = 0;
            if (assertion["tolerance"] is { } t)
            {
                if (t.GetValueKind() != System.Text.Json.JsonValueKind.Number) throw new ContractException("ToleranceInvalid");
                tolerance = double.Parse(t.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(tolerance) || tolerance < 0) throw new ContractException("ToleranceInvalid");
                schemaAssertion["tolerance"] = 0;
            }
            ContractSchemas.ValidateCondition(schemaAssertion);
            if (ContractSemantics.Text(assertion, "kind") != "assertion") throw new ContractException("ConditionShapeInvalid");
            ContractSemantics.CheckAssertion(assertion);
            ContractSemantics.CheckRead(assertion["read"]!.AsObject());
            var type = ValueTypeIdentity.Read(assertion["read"]!["valueType"]!);
            if (type.EnumType.Length != 0)
            {
                if (catalog is null) throw new ContractException("EnumUnknown");
                catalog.RequireType(type.EnumType);
            }
            var op = assertion["operator"]!.GetValue<string>();
            Regex? regex = null;
            if (op == "matches")
            {
                var pattern = (string)expected!.Scalar;
                if (pattern.Length > options.RegexMaxPatternLength) throw new ContractException("RegexPatternTooLong");
                // .NET dialect, case sensitive and culture independent, no inline option restriction.
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(options.RegexTimeoutMilliseconds));
            }
            return new(type, op, expected, tolerance, regex, catalog);
        }
        catch (Exception ex) when (ValueReader.IsValueException(ex))
        {
            throw new AssertionConfigurationException(ex is ContractException ce ? ce.Code : ex is RegexParseException ? "RegexInvalid" : "AssertionInvalid");
        }
    }

    /// <summary>Unavailable reads carry no fabricated Value. All comparisons, including notEquals, remain Unknown.</summary>
    public EvaluationResult EvaluateUnavailable(EvaluationCode reason = EvaluationCode.Unavailable) => EvaluationResult.Unknown(reason);

    public EvaluationResult EvaluateJson(string observedValueJson, EnumCatalogSnapshot? observedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(observedValueJson);
        try { return Evaluate(ContractDecoder.Decode(new UTF8Encoding(false, true).GetBytes(observedValueJson), ".json"), observedCatalog); }
        catch (Exception ex) when (ValueReader.IsValueException(ex)) { return EvaluationResult.Violation(EvaluationCode.InvalidObservation); }
    }

    /// <summary>Pass the original parsed wire Value and current approved host catalog; never round wire integers first.</summary>
    public EvaluationResult Evaluate(JsonObject observedValue, EnumCatalogSnapshot? observedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(observedValue);
        try
        {
            var actual = ValueReader.Read(observedValue, observedCatalog);
            if (actual.Type != type) return EvaluationResult.Violation(EvaluationCode.TypeMismatch);
            if (type.EnumType.Length != 0 && !catalog!.HasSameDefinition(observedCatalog, type.EnumType)) return EvaluationResult.Violation(EvaluationCode.EnumCatalogChanged);
            return EvaluationResult.Known(Compare(actual));
        }
        catch (RegexMatchTimeoutException) { return EvaluationResult.Unknown(EvaluationCode.RegexTimeout); }
        catch (Exception ex) when (ValueReader.IsValueException(ex)) { return EvaluationResult.Violation(EvaluationCode.InvalidObservation); }
    }

    private bool Compare(AssertionValue actual)
    {
        var items = actual.Items;
        var expectedItems = expected?.Items ?? [];
        switch (operation)
        {
            case "equals": return Equal(actual, expected!);
            case "notEquals": return !Equal(actual, expected!);
            case "greaterThan": return Order(actual.Scalar, expected!.Scalar) > 0;
            case "greaterThanOrEqual": return Order(actual.Scalar, expected!.Scalar) >= 0;
            case "lessThan": return Order(actual.Scalar, expected!.Scalar) < 0;
            case "lessThanOrEqual": return Order(actual.Scalar, expected!.Scalar) <= 0;
            case "approximatelyEquals": return Math.Abs((double)actual.Scalar - (double)expected!.Scalar) <= tolerance;
            case "contains": return type.Kind == "string"
                ? ((string)actual.Scalar).Contains((string)expected!.Scalar, StringComparison.Ordinal) : items.Contains(expected!.Scalar);
            case "notContains": return !items.Contains(expected!.Scalar);
            case "startsWith": return ((string)actual.Scalar).StartsWith((string)expected!.Scalar, StringComparison.Ordinal);
            case "endsWith": return ((string)actual.Scalar).EndsWith((string)expected!.Scalar, StringComparison.Ordinal);
            case "matches": return regex!.IsMatch((string)actual.Scalar);
            case "containsAll": return expectedItems.All(items.Contains);
            case "containsAny": return expectedItems.Any(items.Contains);
            case "isEmpty": return items.Length == 0;
            case "isNotEmpty": return items.Length != 0;
            case "countEquals": return items.LongLength == (long)expected!.Scalar;
            case "countNotEquals": return items.LongLength != (long)expected!.Scalar;
            case "countGreaterThan": return items.LongLength > (long)expected!.Scalar;
            case "countGreaterThanOrEqual": return items.LongLength >= (long)expected!.Scalar;
            case "countLessThan": return items.LongLength < (long)expected!.Scalar;
            case "countLessThanOrEqual": return items.LongLength <= (long)expected!.Scalar;
            case "sequenceEquals": return items.SequenceEqual(expectedItems);
            case "startsWithSequence": return SequenceAt(items, expectedItems, 0);
            case "endsWithSequence": return SequenceAt(items, expectedItems, items.Length - expectedItems.Length);
            case "containsSequence":
                for (int start = 0; start <= items.Length - expectedItems.Length; start++)
                    if (SequenceAt(items, expectedItems, start)) return true;
                return false;
            default: throw new InvalidOperationException(); // exhaustive schema/operator validation in Create
        }
    }

    private static bool SequenceAt(object[] source, object[] sequence, int start)
        => start >= 0 && start + sequence.Length <= source.Length && source.AsSpan(start, sequence.Length).SequenceEqual(sequence);
    private static bool Equal(AssertionValue left, AssertionValue right)
        => left.Type.Kind == "set" ? left.Items.Length == right.Items.Length && left.Items.All(right.Items.Contains) : left.Items.SequenceEqual(right.Items);
    private static int Order(object left, object right) => left is long integer ? integer.CompareTo((long)right) : ((double)left).CompareTo((double)right);
}
