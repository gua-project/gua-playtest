using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Replay;

/// <summary>Loads explicit pinned references once. Playback never reopens their paths.</summary>
public sealed class ResolvedReplay
{
    private readonly JsonObject recording;
    private readonly ScenarioDocument scenario;
    private readonly ReplayPlanDocument plan;
    public string PlanSha256 { get; }
    public string ScenarioSha256 { get; }
    public string RecordingSha256 { get; }
    public int StepCount => recording["steps"]!.AsArray().Count;
    public string Timing => plan.Timing;
    public CompletionPolicy Completion => plan.Completion == "afterPlan" ? CompletionPolicy.AfterPlan : CompletionPolicy.OnGoal;
    public PreparedCondition? Success { get; }
    public PreparedCondition? Failure { get; }
    public PreparedCondition? Initial { get; }
    internal IReadOnlyList<(int BeforeStep, PreparedCondition Condition, TimeSpan Timeout)> Checkpoints { get; }

    private ResolvedReplay(ValidatedFile planFile, ValidatedFile scenarioFile, ValidatedFile recordingFile,
        AssertionOptions options, EnumCatalogSnapshot? catalog)
    {
        plan = planFile.CopyJson().Deserialize<ReplayPlanDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        scenario = scenarioFile.CopyJson().Deserialize<ScenarioDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        recording = recordingFile.CopyJson();
        PlanSha256 = planFile.Sha256; ScenarioSha256 = scenarioFile.Sha256; RecordingSha256 = recordingFile.Sha256;
        Success = Prepare(scenario.Goal.Success); Failure = Prepare(scenario.Goal.Failure); Initial = Prepare(plan.Initial);
        Checkpoints = Array.AsReadOnly(plan.Checkpoints.Select(c => (checked((int)c.BeforeStep),
            Prepare(c.Condition)!, TimeSpan.FromMilliseconds(c.TimeoutMilliseconds))).ToArray());
        long previous = -1;
        foreach (var step in recording["steps"]!.AsArray())
        {
            var offset = ReadOffset(step!);
            if (offset < previous || offset > 86400000) throw new ArgumentException("ReplayOffsetsInvalid");
            previous = offset;
        }
        PreparedCondition? Prepare(JsonObject? condition) => condition is null ? null : PreparedCondition.Create(condition, options, catalog);
    }

    public static async ValueTask<ResolvedReplay> LoadAsync(string planPath, IEnumerable<string> allowedRoots,
        AssertionOptions options, EnumCatalogSnapshot? catalog = null, CancellationToken cancellationToken = default)
    {
        var report = await new StaticContractValidator(allowedRoots).ValidateFileAsync(planPath, cancellationToken).ConfigureAwait(false);
        if (!report.IsValid || report.Document is not ReplayPlanDocument)
            throw new ArgumentException(report.IsValid ? "ReplayPlanRequired" : report.Code);
        var entryPath = Path.GetFullPath(planPath);
        var planFile = report.Files.Values.SingleOrDefault(f => string.Equals(f.Path, entryPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?? report.Files.Values.Single(f => f.CopyJson()["kind"]?.GetValue<string>() == "replayPlan");
        var json = planFile.CopyJson();
        // The static validator already canonicalized symlinks/roots; select by the exact pinned
        // content identity from its retained set, never by rereading a mutable reference.
        var scenarioFile = report.Files.Values.First(f => f.Sha256 == json["scenario"]!["sha256"]!.GetValue<string>());
        var recordingFile = report.Files.Values.First(f => f.Sha256 == json["recording"]!["sha256"]!.GetValue<string>());
        return new(planFile, scenarioFile, recordingFile, options, catalog);
    }

    public ScenarioDocument CopyScenario() => scenario with
    {
        Goal = scenario.Goal with { Success = Clone(scenario.Goal.Success), Failure = Clone(scenario.Goal.Failure) },
        Constraints = scenario.Constraints with { AllowedActionIds = scenario.Constraints.AllowedActionIds?.ToArray() }, Setup = Clone(scenario.Setup)
    };
    public JsonObject CopyRecording() => (JsonObject)recording.DeepClone();
    internal Core.ReplayBatch Batch(int start, int end, TimeSpan timeout)
    {
        var steps = recording["steps"]!.AsArray();
        var json = new JsonObject { ["schemaVersion"] = recording["schemaVersion"]!.DeepClone(),
            ["steps"] = new JsonArray(steps.Skip(start).Take(end - start).Select(s => s!.DeepClone()).ToArray()) };
        return new(start, end - start, json.ToJsonString(), Timing, start == 0 ? 0 : ReadOffset(steps[start - 1]!), timeout);
    }
    private static long ReadOffset(JsonNode step) => checked((long)step["relativeMilliseconds"]!.GetValue<decimal>());
    private static JsonObject? Clone(JsonObject? node) => node is null ? null : (JsonObject)node.DeepClone();
}
