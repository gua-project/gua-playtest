using System.Text;
using System.Text.Json;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner;

namespace Gua.Playtest.Cli;

public static class CliApplication
{
    public const int MaxDocumentBytes = 1024 * 1024;
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (args is ["--help"] or [])
        {
            await output.WriteLineAsync("gua-playtest validate --gua-schema <packaged-schema-name> <json-file>\ngua-playtest doctor --native\nScenario validation, run, replay and report commands are pending #2/#15.");
            return 0;
        }
        if (args is ["--version"])
        {
            await output.WriteLineAsync("0.1.0-dev (Gua packages 1.1.1)");
            return 0;
        }
        ValidationResult result;
        if (args is ["doctor", "--native"])
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageCompatibility.CheckNative();
                result = new(ValidationStatus.Valid, "native-package-compatible");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { result = new(ValidationStatus.Interrupted, "doctor-interrupted"); }
            catch (Exception) { result = new(ValidationStatus.Unavailable, "native-package-unavailable-or-incompatible"); }
        }
        else if (args is ["validate", "--gua-schema", var schema, var file])
        {
            try
            {
                // Read at most the limit + 1, including files that grow while being read.
                await using var stream = File.OpenRead(file);
                var bytes = new byte[MaxDocumentBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                result = count > MaxDocumentBytes
                    ? new(ValidationStatus.Invalid, "document-too-large")
                    : await new ValidationRunner(new PackagedSchemaValidator(schema)).ValidateAsync(
                        new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF'), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { result = new(ValidationStatus.Interrupted, "validation-interrupted"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { result = new(ValidationStatus.Invalid, "document-unreadable"); }
        }
        else result = new(ValidationStatus.Unavailable, args.FirstOrDefault() == "validate" ? "scenario-validator-not-implemented" : "unsupported-command");
        await output.WriteLineAsync(JsonSerializer.Serialize(new { status = result.Status.ToString(), code = result.Code }));
        return result.ExitCode;
    }
}
