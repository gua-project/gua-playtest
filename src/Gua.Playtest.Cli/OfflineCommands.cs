using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Testing;

namespace Gua.Playtest.Cli;

/// <summary>Explicit offline composition; never creates a game, Planner, Run or adoption proof.</summary>
internal static class OfflineCommands
{
    internal delegate ValueTask<StaticValidationReport> InputValidator(string input, IEnumerable<string> roots, CancellationToken token);
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics,
        bool json, CancellationToken token, InputValidator? validateInput = null)
    {
        var resolvedSecrets = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            validateInput ??= static (input, roots, token) => new StaticContractValidator(roots).ValidateFileAsync(input, token);
            if (args[0] == "plan") return await PlanAsync(args, output, diagnostics, json, token, validateInput, resolvedSecrets);
            var options = Options.Parse(args[1..], args[0] == "report"
                ? ["--allow-root", "--trace", "--output"]
                : ["--allow-root", "--scenario", "--plan", "--environment", "--max-actions", "--max-duration-ms", "--deny-action"]);
            var roots = options.All("--allow-root");
            var paths = new AllowedPaths(roots);
            if (args[0] == "report")
            {
                var source = paths.Resolve(options.Required("--trace"), Directory.GetCurrentDirectory());
                if (!Directory.Exists(source)) throw new InvalidDataException("ReportTraceDirectoryRequired");
                var read = GuaTraceReader.Read(source);
                if (read.Issues.Count != 0 || !read.Manifest.Finalized) throw new InvalidDataException("SavedTraceInvalidOrIncomplete");
                token.ThrowIfCancellationRequested();
                if (options.Optional("--output") is { } destination)
                {
                    var full = Path.GetFullPath(destination);
                    var parent = paths.Resolve(Path.GetDirectoryName(full)!, Directory.GetCurrentDirectory());
                    full = Path.Combine(parent, Path.GetFileName(full));
                    if (ArtifactPathAuthority.ContainsDirectory(source, parent))
                        throw new InvalidDataException("ReportOutputInsideSource");
                    if (File.Exists(full) || Directory.Exists(full)) throw new InvalidDataException("ReportOutputExists");
                    if (!GuaTraceReport.WriteHtml(source, full).Succeeded) throw new InvalidDataException("ReportWriteRejected");
                }
                return await CommandOutput.WriteAsync(output, diagnostics, json, "CommandSucceeded", "SavedTraceRead", 0,
                    new { finalized = read.Manifest.Finalized, eventCount = read.Events.Count });
            }
            var replay = args[0] == "replay";
            if (options.Has(replay ? "--scenario" : "--plan")) throw new InvalidDataException("ExecutionArgumentsRequired");
            var input = await new StaticContractValidator(roots).ValidateFileAsync(options.Required(replay ? "--plan" : "--scenario"), token);
            var environment = await new StaticContractValidator(roots).ValidateFileAsync(options.Required("--environment"), token);
            token.ThrowIfCancellationRequested();
            if (!input.IsValid || !environment.IsValid) throw new InvalidDataException("ExecutionInputInvalid");
            if (environment.Document is not EnvironmentDocument env) throw new InvalidDataException("EnvironmentRequired");
            ScenarioDocument scenario;
            if (replay)
            {
                if (input.Document is not ReplayPlanDocument plan) throw new InvalidDataException("ReplayPlanRequired");
                scenario = JsonSerializer.Deserialize<ScenarioDocument>(input.Files.Values.Single(f => f.Sha256 == plan.Scenario.Sha256).CopyJson(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            }
            else scenario = input.Document as ScenarioDocument ?? throw new InvalidDataException("ScenarioRequired");
            var effective = EffectiveExecution.Resolve(scenario, env,
                options.Positive("--max-actions"), options.Positive("--max-duration-ms"), options.All("--deny-action"));
            // No guessed launch/attach, clock, authentication, fixture or local guard surrogate.
            // These hashes describe retained source bytes, not a host-certified effective Run identity.
            return await CommandOutput.WriteAsync(output, diagnostics, json, "Unavailable", "ProductionRuntimeUnavailable", 2,
                new { effective, inputHashes = input.Files.Values.Select(f => f.Sha256).ToArray(),
                    environmentSourceSha256 = environment.Files.Values.Single().Sha256,
                    runtimeVerified = false, blockers = new[] { "TrustedRuntimeInputRequired", "PublishedExternalGuardUnavailable", "HostIdentityAndCleanupUnverified" } });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return await CommandOutput.WriteKnownFailureAsync(output, diagnostics, json, "Interrupted", "CommandInterrupted", 4, resolvedSecrets); }
        catch (Exception error)
        {
            // Only our finite diagnostic vocabulary may cross the process boundary.
            var code = error is InvalidDataException && Codes.Contains(error.Message) ? error.Message : "CommandInputRejected";
            return await CommandOutput.WriteKnownFailureAsync(output, diagnostics, json, "Invalid", code, 2, resolvedSecrets);
        }
    }

    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "CommandArgumentsRequired", "DuplicateOption", "ExecutionArgumentsRequired", "ExecutionInputInvalid",
        "EnvironmentRequired", "ScenarioRequired", "ReplayPlanRequired", "PositiveLimitRequired",
        "ReportTraceDirectoryRequired", "SavedTraceInvalidOrIncomplete", "ReportOutputInsideSource", "ReportOutputExists",
        "ReportWriteRejected", "SecretBindingInvalid", "SecretBindingNotReferenced", "SecretUnavailable", "PlanSecretCollision"
    };

    private static async Task<int> PlanAsync(string[] args, TextWriter output, TextWriter diagnostics, bool json, CancellationToken token, InputValidator validateInput, List<string> secrets)
    {
        if (args.Length < 2 || args[1] is not ("create" or "accept")) throw new InvalidDataException("CommandArgumentsRequired");
        var create = args[1] == "create";
        var options = Options.Parse(args[2..], create
            ? ["--managed-root", "--allow-root", "--scenario", "--recording", "--timing", "--secret-env"]
            : ["--managed-root", "--plan", "--trial", "--approve", "--approve-expectations", "--secret-env"]);
        var managed = options.Required("--managed-root");
        string[] command;
        PlanCommand.InputHashes inputHashes;
        IEnumerable<JsonNode> documents;
        if (create)
        {
            // PlanManager retains these bytes again and enforces its create-only publication policy.
            var roots = options.All("--allow-root");
            if (roots.Length != 1) throw new InvalidDataException("CommandArgumentsRequired");
            var scenario = options.Required("--scenario"); var recording = options.Required("--recording");
            var source = await validateInput(scenario, roots, token);
            token.ThrowIfCancellationRequested();
            if (!source.IsValid || source.Document is not ScenarioDocument) throw new InvalidDataException("ExecutionInputInvalid");
            var recorded = await new StaticContractValidator(roots).ValidateRecordingFileAsync(recording, token);
            token.ThrowIfCancellationRequested();
            if (!recorded.IsValid) throw new InvalidDataException("ExecutionInputInvalid");
            documents = source.Files.Values.Concat(recorded.Files.Values).Select(f => (JsonNode)f.CopyJson());
            inputHashes = new("", source.Files.Values.Single().Sha256, recorded.Files.Values.Single().Sha256);
            command = ["plan", "create", "--managed-root", managed, "--allow-root", roots[0], "--scenario", scenario,
                "--recording", recording, "--timing", options.Required("--timing")];
        }
        else
        {
            if (!options.Has("--approve") || !options.Has("--approve-expectations")) throw new InvalidDataException("CommandArgumentsRequired");
            var plan = options.Required("--plan");
            var source = await validateInput(plan, [managed], token);
            token.ThrowIfCancellationRequested();
            if (!source.IsValid || source.Document is not ReplayPlanDocument) throw new InvalidDataException("ExecutionInputInvalid");
            documents = source.Files.Values.Select(f => (JsonNode)f.CopyJson());
            inputHashes = new(source.Files.Values.Single(f => f.CopyJson()["kind"]?.GetValue<string>() == "replayPlan").Sha256, "", "");
            command = ["plan", "accept", "--managed-root", managed, "--plan", plan, "--trial", options.Required("--trial"), "--approve", "--approve-expectations"];
        }
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents) CollectSecretKeys(document, references);
        if (references.Count > 1024) throw new InvalidDataException("CommandInputRejected");
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in options.All("--secret-env"))
        {
            var parts = binding.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]) || !bindings.TryAdd(parts[0], parts[1])) throw new InvalidDataException("SecretBindingInvalid");
            if (!references.Contains(parts[0])) throw new InvalidDataException("SecretBindingNotReferenced");
        }
        long secretCharacters = 0;
        foreach (var key in references)
        {
            if (!bindings.TryGetValue(key, out var variable))
                throw new InvalidDataException("SecretUnavailable");
            var supply = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(supply)) throw new InvalidDataException("SecretUnavailable");
            secrets.Add(supply);
            secretCharacters += supply.Length;
            if (secretCharacters > PersistenceRedactor.MaxSecretCharacters)
                throw new InvalidDataException("CommandInputRejected");
        }
        var redactor = new PersistenceRedactor(secrets, ["password", "token", "authorization", "secretValue"]);
        if (command.Append(Path.GetFullPath(managed)).Any(x => redactor.Redact(x) != x))
            return await SecretPolicyFailure(output, diagnostics, json, redactor);
        bool ReplyAllowed(string reply)
        {
            if (redactor.Redact(reply) != reply) return false;
            using var parsed = JsonDocument.Parse(reply);
            var status = parsed.RootElement.GetProperty("status").GetString()!;
            var code = parsed.RootElement.TryGetProperty("code", out var value) ? value.GetString()! : create ? "PlanCandidateCreated" : "PlanAccepted";
            var document = CommandOutput.Serialize(status, code, parsed.RootElement);
            var text = (create && status == "Candidate" ? parsed.RootElement.GetProperty("plan").GetString() + output.NewLine : "") + $"{status}: {code}";
            var rendered = (json ? document : text) + output.NewLine;
            return redactor.Redact(rendered) == rendered && redactor.Redact(code + diagnostics.NewLine) == code + diagnostics.NewLine &&
                (!parsed.RootElement.TryGetProperty("plan", out var path) || redactor.Redact(path.GetString()!) == path.GetString());
        }
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await PlanCommand.ExecuteAsync(command, buffer, redactor, token, inputHashes, ReplyAllowed);
        if (string.IsNullOrWhiteSpace(buffer.ToString())) return await SecretPolicyFailure(output, diagnostics, json, redactor);
        using var result = JsonDocument.Parse(buffer.ToString());
        if (result.RootElement.ValueKind != JsonValueKind.Object || !result.RootElement.TryGetProperty("status", out _))
            return await SecretPolicyFailure(output, diagnostics, json, redactor);
        if (result.RootElement.TryGetProperty("code", out var rejected) && rejected.GetString() == "PlanSecretCollision")
            return await SecretPolicyFailure(output, diagnostics, json, redactor);
        if (!ReplyAllowed(buffer.ToString())) return await SecretPolicyFailure(output, diagnostics, json, redactor);
        var status = result.RootElement.GetProperty("status").GetString()!;
        var code = result.RootElement.TryGetProperty("code", out var c) ? c.GetString()! : create ? "PlanCandidateCreated" : "PlanAccepted";
        if (!json && exit == 0 && create) await output.WriteLineAsync(result.RootElement.GetProperty("plan").GetString());
        return await CommandOutput.WriteAsync(output, diagnostics, json, status, code, exit, result.RootElement.Clone());
    }

    private static async Task<int> SecretPolicyFailure(TextWriter output, TextWriter diagnostics, bool json, PersistenceRedactor redactor)
    {
        var code = redactor.Redact("PlanSecretCollision");
        var document = CommandOutput.PolicyFailureDocument(redactor);
        await output.WriteAsync(json ? document : code);
        if (code.Length != 0) await diagnostics.WriteAsync(code);
        return 2;
    }

    private static void CollectSecretKeys(JsonNode? node, HashSet<string> keys)
    {
        if (node is JsonObject obj)
            foreach (var property in obj)
            {
                if (property.Key == "secretKey" && property.Value is JsonValue value && value.TryGetValue<string>(out var key)) keys.Add(key);
                else CollectSecretKeys(property.Value, keys);
            }
        else if (node is JsonArray array) foreach (var item in array) CollectSecretKeys(item, keys);
    }

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
        internal bool Has(string key) => values.ContainsKey(key);
        internal string[] All(string key) => values.TryGetValue(key, out var list) ? list.ToArray() : [];
        internal string? Optional(string key) => values.TryGetValue(key, out var list) ? list.Single() : null;
        internal string Required(string key) => Optional(key) ?? throw new InvalidDataException("CommandArgumentsRequired");
        internal long? Positive(string key)
        {
            if (Optional(key) is not { } value) return null;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                throw new InvalidDataException("PositiveLimitRequired");
            return number;
        }
        internal static Options Parse(string[] args, string[] allowed)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!allowed.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException("CommandArgumentsRequired");
                var flag = key is "--approve" or "--approve-expectations";
                var value = flag ? "true" : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)
                    ? args[i] : throw new InvalidDataException("CommandArgumentsRequired");
                if (!options.values.TryGetValue(key, out var list)) options.values[key] = list = [];
                if (list.Count != 0 && key is not ("--allow-root" or "--deny-action" or "--secret-env")) throw new InvalidDataException("DuplicateOption");
                list.Add(value);
            }
            return options;
        }
    }
}
