using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Testing;
using Gua.Core;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Exploration;
using Gua.Playtest.Runner.Planning;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>Trusted finite script composition. Public source and judgement have independent connections;
/// the script receives only PlannerInput, never a connection, Scenario or private judgement.</summary>
internal sealed class ScriptedUiExplore : IExploreObservationFeed, IPlannerAuthority, IPreparationPlannerCheck
{
    internal sealed record ProposalReference(string RunId, string DecisionRequestId, string BasedOnObservationId);
    private readonly Func<BridgeObservations> createPublicSource;
    private BridgeObservations? activeRead;
    private readonly Func<GuaObservationConnection> judgement;
    private readonly IClock clock;
    private readonly string target;
    private readonly int maxBytes;
    private readonly Action verify;
    private readonly Action<GuardedUiReceipt> record;
    private readonly Func<GuardedUiClick, GuardedClickRecording> createRecording;
    private readonly Action<string, UiSnapshotReads> recordPublic;
    private readonly Action<ProposalReference> recordApproval;
    private readonly JsonObject[] reads;
    private readonly JsonObject clickDecision;
    private GuardedUiClick? click;
    private ProjectedPlannerState? latest;
    private long sequence;
    private int released;
    private readonly object resources = new();
    private readonly object releaseGate = new();
    private Task<bool>? releaseTask;
    internal ProposalReference? DecisionReference { get; private set; }
    internal long Requests { get; private set; }
    internal long PollsStarted { get; private set; }
    internal long PollsCompleted { get; private set; }

    internal ScriptedUiExplore(Uri endpoint, Func<GuaObservationConnection> judgement, IClock clock,
        string target, int maxNodes, int maxBytes, TimeSpan timeout, Action verify,
        Action<GuardedUiReceipt> record, Func<GuardedUiClick, GuardedClickRecording> createRecording,
        Action<string, UiSnapshotReads> recordPublic, Action<ProposalReference> recordApproval)
    {
        this.judgement = judgement; this.clock = clock; this.target = target; this.maxBytes = maxBytes;
        this.verify = verify; this.record = record; this.createRecording = createRecording;
        this.recordPublic = recordPublic; this.recordApproval = recordApproval;
        createPublicSource = () => new(endpoint.AbsoluteUri, GuaObservationProfile.Player, timeout, maxNodes, maxBytes);
        reads = PublicReads(target);
        clickDecision = new JsonObject
        {
            ["kind"] = "execute", ["mode"] = "single", ["action"] = new JsonObject
            { ["kind"] = "ui", ["selector"] = new JsonObject { ["id"] = new JsonObject { ["value"] = target } }, ["operation"] = "click" }
        };
    }
    internal static JsonObject[] PublicReads(string target)
    {
        JsonObject Read(string field) => new()
        {
            ["target"] = new JsonObject { ["source"] = "ui", ["selector"] = new JsonObject { ["id"] = new JsonObject { ["value"] = target } } },
            ["region"] = "standard", ["field"] = field, ["valueType"] = new JsonObject { ["type"] = "bool" }
        };
        return [Read("visible"), Read("enabled")];
    }
    internal static bool PublicUiReadAllowed(JsonNode? read)
    {
        if (read?["target"]?["source"]?.GetValue<string>() != "ui" || read["region"]?.GetValue<string>() != "standard" ||
            read["valueType"]?["type"]?.GetValue<string>() is not ("bool" or "integer" or "number" or "string") ||
            read["target"]?["selector"] is not JsonObject selector || selector.Count != 1 || selector["id"] is not JsonObject id ||
            id["match"]?.GetValue<string>() is { } match && match != "exact" || id["value"]?.GetValue<string>() is not { Length: > 0 and <= 128 } text)
            return false;
        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    }

    public ValueTask<bool> CheckAsync(CancellationToken token) => new(Task.Factory.StartNew(() =>
    {
        using var interrupt = token.Register(() => Volatile.Read(ref activeRead)?.Interrupt());
        lock (resources)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref released) != 0) throw new InvalidOperationException("ExplorePublicSourceClosed");
            latest = CapturePublic(); token.ThrowIfCancellationRequested(); return true;
        }
    }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    private ProjectedPlannerState CapturePublic()
    {
        lock (resources)
        {
            if (Volatile.Read(ref released) != 0) throw new InvalidOperationException("ExplorePublicSourceClosed");
            using var source = createPublicSource(); Volatile.Write(ref activeRead, source);
            try { return CapturePublic(source); }
            finally { Volatile.Write(ref activeRead, null); }
        }
    }
    private ProjectedPlannerState CapturePublic(BridgeObservations publicSource)
    {
        verify();
        // Values belong to one immutable native tree bracketed by actual
        // source/profile/epoch checks, rather than separate per-field RPCs.
        var snapshot = publicSource.ReadUiSnapshot(reads); var captured = snapshot.Collections;
        using var metadata = publicSource.CreateGuardedUiClick();
        var definitionsJson = metadata.GetGameInputActionsJson();
        if (Encoding.UTF8.GetByteCount(definitionsJson) > maxBytes ||
            !(GuaDistribution.ValidateJson("game-input-actions-v2.schema.json", definitionsJson) ||
                GuaDistribution.ValidateJson("game-input-actions.schema.json", definitionsJson)))
            throw new InvalidOperationException("ExplorePublicDefinitionsUnavailable");
        var identity = snapshot.Identity;
        if (identity.SourceId != metadata.SourceId || identity.Profile != "Player" || judgement().LastIdentity is not { } privateIdentity ||
            privateIdentity.SourceId != identity.SourceId || privateIdentity.Epoch != identity.SessionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("ExplorePublicIdentityMismatch");
        var definitions = JsonNode.Parse(definitionsJson)!.AsObject();
        if (definitions["sessionEpoch"]!.GetValue<ulong>() != identity.SessionEpoch)
            throw new InvalidOperationException("ExplorePublicDefinitionsChanged");
        var id = "public-" + Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var publicReads = new JsonArray();
        for (int index = 0; index < reads.Length; index++)
        {
            var collection = captured[index]; var value = collection.Reads.Single();
            var availability = value.Availability;
            if (value.Identity is { } readIdentity && (readIdentity.SourceId != identity.SourceId || readIdentity.SessionEpoch != identity.SessionEpoch ||
                readIdentity.Profile != "player" || readIdentity.Revision != identity.Revision)) availability = ReadAvailability.Stale;
            var item = new JsonObject { ["read"] = reads[index].DeepClone(), ["status"] = availability.ToString().ToLowerInvariant() };
            if (availability == ReadAvailability.Available && value.Value is { } wire) item["value"] = JsonNode.Parse(wire.GetRawText());
            publicReads.Add(item);
        }
        var observation = new JsonObject
        {
            ["observationId"] = id, ["observedAt"] = captured[0].ObservedAt.ToString("O"), ["profile"] = "Player",
            ["sources"] = new JsonArray(new JsonObject { ["source"] = "ui", ["sourceId"] = identity.SourceId,
                ["sessionEpoch"] = identity.SessionEpoch, ["revision"] = identity.Revision, ["frameSequence"] = identity.FrameSequence }),
            ["complete"] = publicReads.All(x => x!["status"]!.GetValue<string>() is "available" or "unavailable"), ["reads"] = publicReads
        };
        verify();
        recordPublic(id, snapshot);
        return new(id, observation, definitions, reads.Select(x => (JsonObject)x.DeepClone()).ToArray(), []);
    }

    public async ValueTask<ExploreObservation> CaptureAsync(CancellationToken token)
    {
        var projection = await Task.Factory.StartNew(() =>
        {
            // An obsolete capture token is not permission to destroy this Run's
            // public connection. SDK calls are finite; the owner joins before reuse.
            token.ThrowIfCancellationRequested();
            var value = click?.Receipt is { DispatchBegan: true, State: GuaRemoteDispatchState.Enqueued }
                ? StaleWhileSettling(latest!) : CapturePublic();
            token.ThrowIfCancellationRequested(); return value;
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        var observation = await judgement().CaptureAsync(token).ConfigureAwait(false);
        latest = projection;
        return Frame(observation, projection);
    }
    internal static ProjectedPlannerState StaleWhileSettling(ProjectedPlannerState original)
    {
        var copy = (JsonObject)original.Observation.DeepClone(); copy["complete"] = false;
        foreach (var read in copy["reads"]!.AsArray().OfType<JsonObject>()) { read["status"] = "stale"; read.Remove("value"); }
        return original with { Observation = copy }; // Retain real ID/time/source; never invent a fresh sample.
    }

    private GuardedUiReceipt Start(Action before, TimeSpan timeout, CancellationToken token)
    {
        lock (resources)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref released) != 0 || click is not null) throw new InvalidOperationException("ExploreDispatchClosed");
            using var source = createPublicSource(); Volatile.Write(ref activeRead, source);
            try
            {
                click = source.CreateGuardedUiClick();
                token.ThrowIfCancellationRequested();
                return createRecording(click).Start(() =>
                {
                    before(); recordApproval(DecisionReference ?? throw new InvalidOperationException("ExploreProposalMissing"));
                }, timeout, token);
            }
            finally { Volatile.Write(ref activeRead, null); }
        }
    }
    public ValueTask WaitForChangeAsync(CancellationToken token) => clock.DelayAsync(TimeSpan.FromMilliseconds(25), token);
    private static ExploreObservation Frame(RunObservation observation, ProjectedPlannerState projection)
        => new(observation, projection, new(observation.CapturedAt,
            new Dictionary<string, Runner.Conditions.ConditionObservationUnit>(), new Dictionary<string, Runner.Conditions.ConditionLeafObservation>()));

    internal ValueTask<bool> ExecuteAsync(RunSession run, RunStartBoundary boundary, ResourceLimits limits,
        string runId, string publicObjective, OwnedCleanup cleanup, CancellationToken caller)
    {
        var planner = new Script(clickDecision, input =>
        {
            Requests++;
            DecisionReference = new(input.RunId, input.DecisionRequestId, input.BasedOnObservationId);
        });
        var gate = new PlannerGate(run, clock, this, runId, publicObjective, limits);
        return ExploreDriver.ExecuteRunningAsync(run, gate, planner, this, new Work(this, run.Limits.WaitTimeout),
            new ProgressDefinitions([], []), limits, Frame(boundary.InitialObservation, latest!),
            _ => ValueTask.FromResult(true), cleanup, caller);
    }

    private sealed class Script(JsonObject decision, Action<PlannerInputDocument> observed) : IPlanner<PlannerInputDocument, PlannerReply>
    {
        private int proposed;
        public ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); observed(input);
            // One proposal only. A rejection or unknown effect never restarts the script.
            var next = Interlocked.Exchange(ref proposed, 1) == 0 ? (JsonObject)decision.DeepClone() :
                new JsonObject { ["kind"] = "finish", ["report"] = "cannotProceed" };
            return ValueTask.FromResult(new PlannerReply(PlannerReplyStatus.Completed, Encoding.UTF8.GetBytes(
                ContractJson.Serialize(new PlannerDecisionDocument(input.RunId, input.DecisionRequestId, input.BasedOnObservationId, next)))));
        }
    }

    private sealed class Work(ScriptedUiExplore owner, TimeSpan timeout) : IExploreWork
    {
        public ValueTask<ExploreReceipt> ExecuteAsync(JsonObject decision, IExploreCalls calls, CancellationToken token) => new(Task.Run(async () =>
        {
            if (!JsonNode.DeepEquals(decision, owner.clickDecision)) throw new InvalidOperationException("ExploreScriptWorkUnsupported");
            var receipt = await calls.SendAsync(0, before =>
            {
                owner.verify();
                var result = owner.Start(before, timeout, token); owner.record(result);
                return new ExploreSend<GuardedUiReceipt>(result, result.State is GuaRemoteDispatchState.Enqueued or GuaRemoteDispatchState.Completed);
            }, token).ConfigureAwait(false);
            while (receipt.State == GuaRemoteDispatchState.Enqueued)
            {
                await owner.clock.DelayAsync(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
                receipt = await calls.ReadAsync(() =>
                {
                    owner.verify(); owner.PollsStarted++; var value = owner.click!.Poll(); owner.PollsCompleted++;
                    if (value.State != GuaRemoteDispatchState.Enqueued) { owner.record(value); owner.click.Dispose(); }
                    return value;
                }, token).ConfigureAwait(false);
            }
            return new ExploreReceipt(receipt.State switch
            {
                GuaRemoteDispatchState.Completed when receipt.Succeeded == true => ExploreWorkStatus.Completed,
                GuaRemoteDispatchState.Uncertain => ExploreWorkStatus.Unconfirmed,
                _ => receipt.DispatchBegan ? ExploreWorkStatus.Failed : ExploreWorkStatus.NotSent
            }, true);
        }, token));
    }

    public bool InputsNeutral => true; // Only a UI click is admitted; no held input is supported/acquired.
    public bool RequiresResynchronization => Volatile.Read(ref released) != 0 || latest is null;
    private bool Finish(JsonObject proposal) => proposal["kind"]?.GetValue<string>() == "finish";
    public bool CheckPermissions(JsonObject proposal) => Finish(proposal) || JsonNode.DeepEquals(proposal, clickDecision);
    public bool CheckDefinitions(JsonObject proposal) => CheckPermissions(proposal);
    public bool CheckClock(JsonObject proposal) => CheckPermissions(proposal); // Immediate single UI only, no timing claim.
    public bool CheckInputBoundary(JsonObject proposal) => CheckPermissions(proposal);
    public bool CheckContext(JsonObject proposal, ProjectedPlannerState basis)
        => ContextCurrent(latest, basis, Finish(proposal) ? null : click?.Receipt);
    internal static bool ContextCurrent(ProjectedPlannerState? latest, ProjectedPlannerState basis, GuardedUiReceipt? guard)
    {
        var actual = latest?.Observation["sources"]?[0]; var source = basis.Observation["sources"]?[0];
        if (actual is null || source is null || latest!.Observation["profile"]?.GetValue<string>() != "Player" ||
            basis.Observation["profile"]?.GetValue<string>() != "Player" ||
            !JsonNode.DeepEquals(actual["sourceId"], source["sourceId"]) || !JsonNode.DeepEquals(actual["sessionEpoch"], source["sessionEpoch"])) return false;
        // The guarded click publishes its fresh pre-send identity before invoking the
        // approval callback. Refuse a replacement since the proposal's public basis.
        return guard is null ||
            JsonNode.DeepEquals(actual["revision"], source["revision"]) &&
            guard.SourceId == source["sourceId"]!.GetValue<string>() && guard.Profile == "Player" &&
            guard.SessionEpoch == source["sessionEpoch"]!.GetValue<ulong>() && guard.Revision == source["revision"]!.GetValue<ulong>();
    }
    public bool CheckTargets(JsonObject proposal, ProjectedPlannerState basis) => Finish(proposal) ||
        latest?.Observation["reads"] is JsonArray items && items.Count == 2 && items.All(item =>
            item?["status"]?.GetValue<string>() == "available" && item["value"]?["type"]?.GetValue<string>() == "bool" && item["value"]?["value"]?.GetValue<bool>() == true);

    public ValueTask<bool> ReleaseAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref released, 1); Volatile.Read(ref activeRead)?.Interrupt();
        lock (releaseGate)
            return new(releaseTask ??= Task.Factory.StartNew(() =>
            {
                lock (resources) { click?.Dispose(); return true; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }
}
