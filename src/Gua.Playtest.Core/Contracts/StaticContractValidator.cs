using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;

namespace Gua.Playtest.Core.Contracts;

public sealed class ValidatedFile
{
    private readonly byte[] bytes;
    public string Path { get; }
    public string Sha256 { get; }
    public int ByteLength => bytes.Length;
    internal JsonObject Json { get; }
    internal ValidatedFile(string path, byte[] contents, JsonObject json)
    {
        Path = path; bytes = contents; Json = json;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(contents));
    }
    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public JsonObject CopyJson() => (JsonObject)Json.DeepClone();
}

public sealed record StaticValidationReport(string Code, ContractDocument? Document,
    IReadOnlyDictionary<string, ValidatedFile> Files)
{
    public bool IsValid => Code == "Valid";
    public bool RuntimeVerified => false;
}

/// <summary>Validates forms and explicit fixed local references. Never launches code, resolves secrets or grades a Goal.</summary>
public sealed class StaticContractValidator
{
    private readonly AllowedPaths paths;
    public StaticContractValidator(IEnumerable<string> allowedRoots) => paths = new AllowedPaths(allowedRoots);

    public async ValueTask<StaticValidationReport> ValidateFileAsync(string file, CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, ValidatedFile>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await LoadDocumentAsync(paths.Resolve(file, Directory.GetCurrentDirectory()), files, cancellationToken);
            var model = Deserialize(entry.Json);
            return new("Valid", model, new ReadOnlyDictionary<string, ValidatedFile>(files));
        }
        catch (OperationCanceledException) { return Failed("Interrupted"); }
        catch (ContractException e) { return Failed(e.Code); }
        catch (YamlException) { return Failed("MalformedDocument"); }
        catch (JsonException) { return Failed("MalformedDocument"); }
        catch (UnauthorizedAccessException) { return Failed("ReferenceUnreadable"); }
        catch (IOException) { return Failed("ReferenceUnreadable"); }
        catch (ArgumentException) { return Failed("InvalidPath"); }
        catch (NotSupportedException) { return Failed("InvalidPath"); }
        catch (InvalidOperationException) { return Failed("SchemaInvalid"); }
        catch (OverflowException) { return Failed("NonFiniteNumber"); }
    }

    private static StaticValidationReport Failed(string code) => new(code, null, new ReadOnlyDictionary<string, ValidatedFile>(new Dictionary<string, ValidatedFile>()));

    private async Task<ValidatedFile> LoadDocumentAsync(string path, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        var entry = await ReadAsync(path, files, token);
        ContractSchemas.ValidateDocument(entry.Json);
        ContractSemantics.Validate(entry.Json);
        switch (ContractSemantics.Text(entry.Json, "kind"))
        {
            case "replayPlan": await ValidatePlanAsync(entry, files, token); break;
            case "scenarioRegistry": await ValidateRegistryAsync(entry, files, token); break;
            case "run": await ValidateRunAsync(entry, files, token); break;
            case "adoptionEvidence": await ValidateEvidenceAsync(entry, files, token); break;
        }
        return entry;
    }

    private static async Task<ValidatedFile> ReadAsync(string path, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        if (files.TryGetValue(path, out var old)) return old;
        if (files.Count >= 1000) throw new ContractException("DocumentTooComplex");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > ContractDecoder.MaxBytes) throw new ContractException("DocumentTooLarge");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1) throw new ContractException("DocumentChanged");
        if (files.Values.Sum(v => (long)v.ByteLength) + bytes.Length > 32L * 1024 * 1024) throw new ContractException("DocumentTooComplex");
        var json = ContractDecoder.Decode(bytes, System.IO.Path.GetExtension(path));
        var result = new ValidatedFile(path, bytes, json);
        files.Add(path, result);
        return result;
    }

    private async Task<ValidatedFile> ReferenceAsync(ValidatedFile owner, JsonNode reference, string kind,
        Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        var path = paths.Resolve(reference["path"]!.GetValue<string>(), System.IO.Path.GetDirectoryName(owner.Path)!);
        if (path.Equals(owner.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ContractException("ReferenceCycle");
        var value = await ReadAsync(path, files, token);
        if (value.Sha256 != reference["sha256"]!.GetValue<string>()) throw new ContractException("HashMismatch");
        if (kind == "recording") ContractSchemas.Validate("recording.schema.json", value.Json);
        else
        {
            if (ContractSemantics.Text(value.Json, "kind") != kind) throw new ContractException("ReferenceKindMismatch");
            ContractSchemas.ValidateDocument(value.Json); ContractSemantics.Validate(value.Json);
        }
        return value;
    }

    private async Task ValidatePlanAsync(ValidatedFile plan, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        await ReferenceAsync(plan, plan.Json["scenario"]!, "scenario", files, token);
        var recording = await ReferenceAsync(plan, plan.Json["recording"]!, "recording", files, token);
        int count = recording.Json["steps"]!.AsArray().Count;
        long last = -1;
        foreach (var checkpoint in plan.Json["checkpoints"]!.AsArray())
        {
            var step = checkpoint!["beforeStep"]!.GetValue<long>();
            if (step > count || step < last) throw new ContractException("RecordingRangeInvalid");
            last = step;
        }
        if (ContractSemantics.Text(plan.Json, "timing") == "recorded" && plan.Json["checkpoints"]!.AsArray().Count != 0)
            throw new ContractException("RecordingWaitConflict");
    }

    private async Task ValidateRegistryAsync(ValidatedFile registry, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registry.Json["scenarios"]!.AsArray())
        {
            token.ThrowIfCancellationRequested();
            if (!ids.Add(ContractSemantics.Text(registration!, "scenarioId"))) throw new ContractException("DuplicateScenarioId");
            var scenario = await ReferenceAsync(registry, registration!["file"]!, "scenario", files, token);
            if (ContractSemantics.Text(registration, "scenarioId") != ContractSemantics.Text(scenario.Json, "scenarioId")
                || ContractSemantics.Text(registration, "definitionVersion") != ContractSemantics.Text(scenario.Json, "definitionVersion"))
                throw new ContractException("IdentityMismatch");
        }
        foreach (var root in registry.Json["artifactRoots"]!.AsArray())
        {
            var resolved = paths.Resolve(root!.GetValue<string>(), System.IO.Path.GetDirectoryName(registry.Path)!);
            if (!Directory.Exists(resolved)) throw new ContractException("InvalidPath");
        }
    }

    private async Task ValidateRunAsync(ValidatedFile run, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        var scenario = await ReferenceAsync(run, run.Json["scenario"]!, "scenario", files, token);
        await ReferenceAsync(run, run.Json["environment"]!, "environment", files, token);
        CheckScenarioIdentity(run.Json["identity"]!, scenario);
        if (run.Json["plan"] is not null)
        {
            if (ContractSemantics.Text(run.Json, "mode") != "Replay") throw new ContractException("IdentityMismatch");
            var plan = await ReferenceAsync(run, run.Json["plan"]!, "replayPlan", files, token);
            await ValidatePlanAsync(plan, files, token);
            if (plan.Json["scenario"]!["sha256"]!.GetValue<string>() != scenario.Sha256) throw new ContractException("IdentityMismatch");
        }
        else if (ContractSemantics.Text(run.Json, "mode") == "Replay") throw new ContractException("ReferenceMissing");
        // Actual connected build and effective secret-free Environment identity are checked by the runtime, not source-file claims.
    }

    private async Task ValidateEvidenceAsync(ValidatedFile evidence, Dictionary<string, ValidatedFile> files, CancellationToken token)
    {
        var plan = await ReferenceAsync(evidence, evidence.Json["plan"]!, "replayPlan", files, token);
        var scenario = await ReferenceAsync(evidence, evidence.Json["scenario"]!, "scenario", files, token);
        var recording = await ReferenceAsync(evidence, evidence.Json["recording"]!, "recording", files, token);
        await ValidatePlanAsync(plan, files, token);
        if (plan.Json["scenario"]!["sha256"]!.GetValue<string>() != scenario.Sha256
            || plan.Json["recording"]!["sha256"]!.GetValue<string>() != recording.Sha256
            || ContractSemantics.Text(plan.Json, "completion") != "afterPlan") throw new ContractException("IdentityMismatch");
        CheckScenarioIdentity(evidence.Json["identity"]!, scenario);
    }

    private static void CheckScenarioIdentity(JsonNode identity, ValidatedFile scenario)
    {
        if (ContractSemantics.Text(identity, "scenarioId") != ContractSemantics.Text(scenario.Json, "scenarioId")
            || ContractSemantics.Text(identity, "definitionVersion") != ContractSemantics.Text(scenario.Json, "definitionVersion")
            || ContractSemantics.Text(identity, "scenarioSha256") != scenario.Sha256) throw new ContractException("IdentityMismatch");
    }

    private static ContractDocument Deserialize(JsonObject json) => ContractSemantics.Text(json, "kind") switch
    {
        "scenario" => json.Deserialize<ScenarioDocument>(ContractJson.Options)!,
        "environment" => json.Deserialize<EnvironmentDocument>(ContractJson.Options)!,
        "replayPlan" => json.Deserialize<ReplayPlanDocument>(ContractJson.Options)!,
        "plannerInput" => json.Deserialize<PlannerInputDocument>(ContractJson.Options)!,
        "plannerDecision" => json.Deserialize<PlannerDecisionDocument>(ContractJson.Options)!,
        "run" => json.Deserialize<RunDocument>(ContractJson.Options)!,
        "result" => json.Deserialize<ResultDocument>(ContractJson.Options)!,
        "scenarioRegistry" => json.Deserialize<ScenarioRegistryDocument>(ContractJson.Options)!,
        "adoptionEvidence" => json.Deserialize<AdoptionEvidenceDocument>(ContractJson.Options)!,
        _ => throw new ContractException("SchemaInvalid")
    };
}
