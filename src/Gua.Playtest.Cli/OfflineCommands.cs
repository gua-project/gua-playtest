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
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics,
        bool json, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args[0] == "plan") return await PlanAsync(args, output, diagnostics, json, token);
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
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (full.Equals(source, comparison) || full.StartsWith(source + Path.DirectorySeparatorChar, comparison))
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
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Interrupted", "CommandInterrupted", 4); }
        catch (Exception error)
        {
            // Only our finite diagnostic vocabulary may cross the process boundary.
            var code = error is InvalidDataException && Codes.Contains(error.Message) ? error.Message : "CommandInputRejected";
            return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", code, 2);
        }
    }

    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "CommandArgumentsRequired", "DuplicateOption", "ExecutionArgumentsRequired", "ExecutionInputInvalid",
        "EnvironmentRequired", "ScenarioRequired", "ReplayPlanRequired", "PositiveLimitRequired",
        "ReportTraceDirectoryRequired", "SavedTraceInvalidOrIncomplete", "ReportOutputInsideSource", "ReportOutputExists",
        "ReportWriteRejected", "SecretBindingInvalid", "SecretBindingNotReferenced", "SecretUnavailable", "PlanSecretCollision"
    };

    private static async Task<int> PlanAsync(string[] args, TextWriter output, TextWriter diagnostics, bool json, CancellationToken token)
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
            var source = await new StaticContractValidator(roots).ValidateFileAsync(scenario, token);
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
            var source = await new StaticContractValidator([managed]).ValidateFileAsync(plan, token);
            if (!source.IsValid || source.Document is not ReplayPlanDocument) throw new InvalidDataException("ExecutionInputInvalid");
            documents = source.Files.Values.Select(f => (JsonNode)f.CopyJson());
            inputHashes = new(source.Files.Values.Single(f => f.CopyJson()["kind"]?.GetValue<string>() == "replayPlan").Sha256, "", "");
            command = ["plan", "accept", "--managed-root", managed, "--plan", plan, "--trial", options.Required("--trial"), "--approve", "--approve-expectations"];
        }
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents) CollectSecretKeys(document, references);
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in options.All("--secret-env"))
        {
            var parts = binding.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]) || !bindings.TryAdd(parts[0], parts[1])) throw new InvalidDataException("SecretBindingInvalid");
            if (!references.Contains(parts[0])) throw new InvalidDataException("SecretBindingNotReferenced");
        }
        var secrets = new List<string>();
        foreach (var key in references)
        {
            if (!bindings.TryGetValue(key, out var variable))
                throw new InvalidDataException("SecretUnavailable");
            var supply = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(supply)) throw new InvalidDataException("SecretUnavailable");
            secrets.Add(supply);
        }
        var redactor = new PersistenceRedactor(secrets, ["password", "token", "authorization", "secretValue"]);
        if (command.Any(x => redactor.Redact(x) != x)) throw new InvalidDataException("PlanSecretCollision");
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await PlanCommand.ExecuteAsync(command, buffer, redactor, token, inputHashes);
        using var result = JsonDocument.Parse(buffer.ToString());
        var status = result.RootElement.GetProperty("status").GetString()!;
        var code = result.RootElement.TryGetProperty("code", out var c) ? c.GetString()! : create ? "PlanCandidateCreated" : "PlanAccepted";
        if (redactor.Redact(buffer.ToString()) != buffer.ToString()) throw new InvalidDataException("PlanSecretCollision");
        if (!json && exit == 0 && create) await output.WriteLineAsync(result.RootElement.GetProperty("plan").GetString());
        return await CommandOutput.WriteAsync(output, diagnostics, json, status, code, exit, result.RootElement.Clone());
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
