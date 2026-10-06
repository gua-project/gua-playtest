namespace Gua.Playtest.Core.Assertions;

public enum TruthValue { False, True, Unknown }
public enum EvaluationError { None, InvalidConfiguration, ObservationContractViolation }
public enum EvaluationCode { None, Unavailable, Missing, GetterError, Stale, Gap, Truncated, RegexTimeout, InvalidObservation, TypeMismatch, EnumCatalogChanged }

/// <summary>Truth never carries a game Value. Errors cannot establish success or failure.</summary>
public readonly record struct EvaluationResult
{
    public TruthValue Truth { get; }
    public EvaluationError Error { get; }
    public EvaluationCode Code { get; }

    private EvaluationResult(TruthValue truth, EvaluationError error, EvaluationCode code)
        => (Truth, Error, Code) = (truth, error, code);

    public static EvaluationResult Known(bool value) => new(value ? TruthValue.True : TruthValue.False, EvaluationError.None, EvaluationCode.None);
    public static EvaluationResult Unknown(EvaluationCode code = EvaluationCode.Unavailable)
    {
        if (code is not (EvaluationCode.Unavailable or EvaluationCode.Missing or EvaluationCode.GetterError or EvaluationCode.Stale or EvaluationCode.Gap or EvaluationCode.Truncated or EvaluationCode.RegexTimeout))
            throw new ArgumentOutOfRangeException(nameof(code));
        return new(TruthValue.Unknown, EvaluationError.None, code);
    }
    public static EvaluationResult InvalidConfiguration() => new(TruthValue.Unknown, EvaluationError.InvalidConfiguration, EvaluationCode.None);
    internal static EvaluationResult Violation(EvaluationCode code) => new(TruthValue.Unknown, EvaluationError.ObservationContractViolation, code);
}

/// <summary>Pure ordinary groups, with no temporal history or selector quantification.</summary>
public static class TruthLogic
{
    public static EvaluationResult All(IEnumerable<EvaluationResult> children) => Combine(children, all: true);
    public static EvaluationResult Any(IEnumerable<EvaluationResult> children) => Combine(children, all: false);

    private static EvaluationResult Combine(IEnumerable<EvaluationResult> children, bool all)
    {
        ArgumentNullException.ThrowIfNull(children);
        bool decisive = false, seen = false;
        EvaluationResult? unknown = null, error = null;
        foreach (var child in children)
        {
            seen = true;
            // Inspect every branch. Invalid configuration takes precedence over observation violations.
            if (child.Error != EvaluationError.None)
            {
                if (error is null || child.Error == EvaluationError.InvalidConfiguration) error = child;
            }
            else if (child.Truth == TruthValue.Unknown) unknown ??= child;
            else if (child.Truth == (all ? TruthValue.False : TruthValue.True)) decisive = true;
        }
        if (error is { } failure) return failure;
        if (!seen) return EvaluationResult.InvalidConfiguration(); // schema groups require at least one child
        if (decisive) return EvaluationResult.Known(!all);
        return unknown ?? EvaluationResult.Known(all);
    }
}

public sealed class AssertionConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Explicit effective Environment limits; no implicit defaults.</summary>
public sealed record AssertionOptions(int RegexTimeoutMilliseconds, int RegexMaxPatternLength);
