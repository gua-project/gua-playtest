namespace Gua.Playtest.Core;

// These ports do not define Scenario, Value, Decision or result wire formats.
// Their typed payloads belong to the corresponding implementation issues.
public interface IClock
{
    TimeSpan Elapsed { get; }
    ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public interface IObservationSource<TObservation>
{
    ValueTask<TObservation> CaptureAsync(CancellationToken cancellationToken);
}

public interface IPlanner<TInput, TDecision>
{
    ValueTask<TDecision> DecideAsync(TInput input, CancellationToken cancellationToken);
}

/// <summary>A session cleans up only resources it owns; attach does not transfer process ownership.</summary>
public interface IHostSession : IAsyncDisposable
{
    ValueTask CleanupAsync(CancellationToken cancellationToken);
}

public enum ValidationStatus { Valid, Invalid, Unavailable, Interrupted }
public sealed record ValidationResult(ValidationStatus Status, string Code)
{
    public int ExitCode => Status switch
    {
        ValidationStatus.Valid => 0,
        ValidationStatus.Invalid => 2,
        ValidationStatus.Unavailable => 2,
        ValidationStatus.Interrupted => 130,
        _ => throw new ArgumentOutOfRangeException(nameof(Status))
    };
}

public interface IStaticValidator
{
    ValueTask<ValidationResult> ValidateAsync(string document, CancellationToken cancellationToken);
}
