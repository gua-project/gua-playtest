using System.Text.Json;
using Gua.Playtest.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Structural validation only, using the exact embedded public Gua schema registry.</summary>
public sealed class PackagedSchemaValidator(string schemaName) : IStaticValidator
{
    public ValueTask<ValidationResult> ValidateAsync(string document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GuaDistribution.SchemaNames.Contains(schemaName, StringComparer.Ordinal))
            return ValueTask.FromResult(new ValidationResult(ValidationStatus.Invalid, "unknown-gua-schema"));
        try
        {
            var valid = GuaDistribution.ValidateJson(schemaName, document);
            return ValueTask.FromResult(new ValidationResult(valid ? ValidationStatus.Valid : ValidationStatus.Invalid,
                valid ? "gua-schema-valid" : "gua-schema-invalid"));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new ValidationResult(ValidationStatus.Invalid, "invalid-json"));
        }
    }
}
