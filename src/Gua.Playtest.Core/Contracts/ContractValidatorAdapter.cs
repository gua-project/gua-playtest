namespace Gua.Playtest.Core.Contracts;

/// <summary>The foundation validation port accepts an explicit path for Playtest contracts.</summary>
public sealed class ContractValidatorAdapter(IEnumerable<string> allowedRoots) : IStaticValidator
{
    private readonly StaticContractValidator validator = new(allowedRoots);
    public async ValueTask<ValidationResult> ValidateAsync(string document, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateFileAsync(document, cancellationToken);
        return new(result.IsValid ? ValidationStatus.Valid : result.Code == "Interrupted" ? ValidationStatus.Interrupted : ValidationStatus.Invalid, result.Code);
    }
}
