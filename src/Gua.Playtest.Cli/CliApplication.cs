using System.Text;
using System.Text.Json;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Cli;

public static class CliApplication
{
    public const int MaxDocumentBytes = 1024 * 1024;
    /// <summary>Result boundary for a trusted execution composition. Does not execute or certify a Run.</summary>
    public static Task<int> WriteRunResultAsync(RunOutcome outcome, PersistenceResult persistence,
        TextWriter output, TextWriter diagnostics, bool json)
        => CommandOutput.WriteAsync(output, diagnostics, json, outcome.Primary.Status.ToString(), outcome.Primary.Cause.Reason.ToString(),
            persistence.ExitCode(outcome), new
            {
                phase = outcome.Primary.Cause.Phase.ToString(), origin = outcome.Primary.Cause.Origin.ToString(),
                postProcessingComplete = outcome.PostProcessingComplete && persistence.Saved && !persistence.PostProcessingIncomplete,
                postProcessing = outcome.PostProcessing.Select(x => x.Reason.ToString()).ToArray(),
                persistence = persistence.Failure.ToString()
            });
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
        => await ExecuteAsync(args, output, TextWriter.Null, cancellationToken);

    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics, CancellationToken cancellationToken = default)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        if (args.Count(x => x == "--json") > 1) return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", "DuplicateJsonOption", 2);
        args = args.Where(x => x != "--json").ToArray();
        if (args is ["--help"] or [])
        {
            var help = "gua-playtest validate --allow-root <existing-directory> <json-or-yaml-file>\ngua-playtest validate --gua-schema <packaged-schema-name> <json-file>\ngua-playtest doctor --native\ngua-playtest plan create|accept (see docs/cli.md)\ngua-playtest report --allow-root <root> --trace <saved-trace-directory> [--output <new-html>]\ngua-playtest explore --allow-root <root> --scenario <file> --environment <file>\ngua-playtest replay --allow-root <root> --plan <file> --environment <file>\ngua-playtest dashboard --registry <file> --artifact-root <root> --allow-root <root> --port <1024..65535>\nUse --json for one final JSON document. Execution requires an approved runtime composition.";
            help += "\ngua-playtest observe-run --scenario <file> --environment <file> --executable <game.exe> --executable-sha256 <hash> --working-directory <dir> --output-root <private-dir> --allow-root <root> (Windows, observation-only; see docs/observation-runtime.md)";
            help += "\ngua-playtest click-run (same launch options as observe-run) --target-id <explicit-ui-id> (one guarded UI click; see docs/single-ui-runtime.md)";
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { status = "CommandSucceeded", code = "HelpShown", help }) : help);
            return 0;
        }
        if (args is ["--version"])
        {
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { status = "CommandSucceeded", code = "VersionShown", version = "0.1.0-dev", guaVersion = "1.1.2" }) : "0.1.0-dev (Gua packages 1.1.2)");
            return 0;
        }
        if (args.FirstOrDefault() == "dashboard")
            return await DashboardCommand.ExecuteAsync(args, output, diagnostics, json, cancellationToken);
        if (args.FirstOrDefault() == "observe-run")
            return await ObservationRunCommand.ExecuteAsync(args, output, diagnostics, json, cancellationToken);
        if (args.FirstOrDefault() == "click-run")
            return await ObservationRunCommand.ExecuteAsync(args, output, diagnostics, json, cancellationToken, singleClick: true);
        if (args.FirstOrDefault() is "plan" or "report" or "explore" or "replay")
            return await OfflineCommands.ExecuteAsync(args, output, diagnostics, json, cancellationToken);
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
        else if (args is ["validate", "--allow-root", var root, var contractFile])
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authorized = StaticPathAuthority.File(contractFile, [root], "InvalidAllowedRoots");
                result = await new ValidationRunner(new ContractValidatorAdapter(StaticPathAuthority.Validator([root]))).ValidateAsync(authorized, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { result = new(ValidationStatus.Interrupted, "validation-interrupted"); }
            catch (Exception)
            { result = new(ValidationStatus.Invalid, "InvalidAllowedRoots"); }
        }
        else if (args is ["validate", "--gua-schema", var schema, var file])
        {
            try
            {
                // Read at most the limit + 1, including files that grow while being read.
                await using var stream = File.OpenRead(StaticPathAuthority.ExplicitLocalFile(file));
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
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { result = new(ValidationStatus.Invalid, "document-unreadable"); }
        }
        else result = new(ValidationStatus.Unavailable, args.FirstOrDefault() == "validate" ? "explicit-validation-arguments-required" : "unsupported-command");
        return await CommandOutput.WriteAsync(output, diagnostics, json, result.Status.ToString(), result.Code,
            result.Status == ValidationStatus.Interrupted ? 4 : result.ExitCode);
    }
}
