using Gua.Playtest.Core;

namespace Gua.Playtest.Runner;

/// <summary>Shared library/CLI validation boundary. Never starts a host or planner.</summary>
public sealed class ValidationRunner(IStaticValidator validator)
{
    private readonly IStaticValidator _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    public async ValueTask<ValidationResult> ValidateAsync(string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _validator.ValidateAsync(document, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ValidationStatus.Interrupted, "validation-interrupted");
        }
        catch (Exception)
        {
            // Provider exception text can contain document values, credentials and local paths.
            return new(ValidationStatus.Unavailable, "validation-provider-failed");
        }
    }
}
