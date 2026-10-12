using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>Trusted effective Environment selection; never supplied by a Planner.
/// Composition must pass the reduced action/read/segment budgets, not the original requested permissions.</summary>
public sealed record NavigationPreparationPolicy(NavigationProfile Profile, string FixtureId,
    IReadOnlySet<string> AllowedActionIds, IReadOnlyList<JsonObject> AllowedReads, long MaxSegmentMilliseconds);
public sealed record NavigationSample(string SourceId, string Epoch, string CaptureRequestId, string ClockId,
    string SampleId, TimeSpan CapturedAt, string SpaceId, long SpaceEpoch);
public sealed record NavigationScalarEvidence(JsonObject Read, JsonObject Value, string Status, NavigationSample Sample);
public sealed record NavigationObjectEvidence(JsonObject Selector, string DomainId, string RuntimeId,
    NavigationVector Position, string Space, NavigationSample Sample, bool Controllable, long MatchCount);
/// <summary>Read-only public host evidence at the same synchronized capture boundary. No dispatch or Setup handle.</summary>
public sealed record NavigationStartEvidence(NavigationSample Sample, JsonObject ActionDefinitions,
    JsonObject SpatialProvider, NavigationMovement Movement, string Topology, string Clock,
    bool TimedSegmentsSupported, bool NeutralAtSegmentEndSupported, bool LoadedRegionConfirmed,
    NavigationSurvey LoadedSurvey, NavigationObjectEvidence Actor, NavigationObjectEvidence[] Targets,
    NavigationScalarEvidence[] Scalars);

public static class NavigationPreparationCheck
{
    public static string ValidatePolicy(NavigationPreparationPolicy p)
    {
        if (p is null || p.Profile is null || p.AllowedActionIds is null || p.AllowedReads is null ||
            p.AllowedReads.Count > 1000 || p.AllowedActionIds.Count > 1000 || p.MaxSegmentMilliseconds <= 0)
            return "NavigationPolicyInvalid";
        var code = NavigationProfileValidation.Validate(p.Profile);
        if (code != "Valid") return code;
        if (p.FixtureId != p.Profile.Preparation.FixtureId) return "NavigationFixtureMismatch";
        if (!p.AllowedActionIds.Contains(p.Profile.Movement.ActionId) ||
            p.Profile.Budgets.MaxInputMilliseconds > p.MaxSegmentMilliseconds ||
            NavigationProfileValidation.Reads(p.Profile).Any(r => !p.AllowedReads.Any(a => JsonNode.DeepEquals(a, r))))
            return "NavigationPermissionDenied";
        return "Valid";
    }

    /// <summary>No capability guesses, clock conversion, calibration or input. Valid is readiness only, never Goal success.</summary>
    public static string Validate(NavigationPreparationPolicy policy, NavigationStartEvidence? e,
        HostIdentity identity, string requestId, TimeSpan captureTime, TimeSpan now, string clockId)
    {
        try { return ValidateCore(policy, e, identity, requestId, captureTime, now, clockId); }
        catch (Exception x) when (x is InvalidOperationException or ArgumentException or NullReferenceException or OverflowException)
        { return "NavigationEvidenceInvalid"; }
    }
    private static string ValidateCore(NavigationPreparationPolicy policy, NavigationStartEvidence? e,
        HostIdentity identity, string requestId, TimeSpan captureTime, TimeSpan now, string clockId)
    {
        var code = ValidatePolicy(policy); if (code != "Valid") return code;
        var p = policy.Profile;
        if (e is null) return "NavigationEvidenceMissing";
        bool Fresh(NavigationSample s) => s is not null && s.SourceId == identity.SourceId && s.Epoch == identity.Epoch
            && s.CaptureRequestId == requestId && s.ClockId == clockId && !string.IsNullOrWhiteSpace(s.SampleId)
            && s.SpaceId == p.SpaceId && s.SpaceEpoch == p.SpaceEpoch && s.CapturedAt >= TimeSpan.Zero
            && s.CapturedAt <= captureTime && captureTime <= now
            && now - s.CapturedAt <= TimeSpan.FromMilliseconds(p.Budgets.MaxObservationAgeMilliseconds);
        if (!Fresh(e.Sample) || e.Sample.CapturedAt != captureTime) return "NavigationStaleObservation";
        if (e.Clock != p.Preparation.Clock || e.Clock != identity.Clock || !e.TimedSegmentsSupported || !e.NeutralAtSegmentEndSupported)
            return "NavigationClockUnavailable";
        if (!JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(e.Movement),
                System.Text.Json.JsonSerializer.SerializeToNode(p.Movement)) || e.Topology != p.Survey.Topology) return "NavigationUnsupported";
        if (!e.LoadedRegionConfirmed || e.LoadedSurvey != p.Survey) return "NavigationRegionUnconfirmed";
        if (NavigationProfileValidation.ValidateHostDefinitions(e.ActionDefinitions, e.SpatialProvider) != "Valid")
            return "NavigationCapabilityUnavailable";
        // Gua action-map epoch is numeric; never reinterpret an unrelated clock/epoch.
        if (e.ActionDefinitions["sessionEpoch"]!.ToJsonString() != identity.Epoch) return "NavigationIdentityMismatch";
        var actions = e.ActionDefinitions["actions"]!.AsArray().Where(a => a!["id"]!.GetValue<string>() == p.Movement.ActionId).ToArray();
        if (actions.Length != 1) return "NavigationActionUnavailable";
        var action = actions[0]!;
        if (action["valueType"]!.GetValue<string>() != "vector2" || !action["active"]!.GetValue<bool>() ||
            !action["holdable"]!.GetValue<bool>() || action["requiresConfirmation"]!.GetValue<bool>() ||
            action["agentExposure"]?.GetValue<string>() == "private") return "NavigationActionUnavailable";
        if (action["range"] is not { } range) return "NavigationInputRangeUnavailable";
        var min = range["minimum"]!.GetValue<double>(); var max = range["maximum"]!.GetValue<double>();
        if (min > p.Movement.AxisX.Minimum || min > p.Movement.AxisY.Minimum ||
            max < p.Movement.AxisX.Maximum || max < p.Movement.AxisY.Maximum) return "NavigationInputRangeUnavailable";
        var provider = e.SpatialProvider;
        bool Has(string field, string value) => provider[field]!.AsArray().Any(n => n!.GetValue<string>() == value);
        if (provider["spaceId"]!.GetValue<string>() != p.SpaceId || provider["spaceEpoch"]!.GetValue<long>() != p.SpaceEpoch ||
            !Has("operations", "raycast") || !Has("operations", "overlap") || !Has("operations", "sweep") ||
            !Has("shapes", "capsule") || !Has("policies", p.QueryPolicyId) || !Has("consistencies", p.Consistency) ||
            provider["limits"]!["maxDeadlineMs"]!.GetValue<double>() < p.Budgets.QueryDeadlineMilliseconds)
            return "NavigationCapabilityUnavailable";
        NavigationVector Vector(JsonNode n) => new(n["x"]!.GetValue<double>(), n["y"]!.GetValue<double>(), n["z"]!.GetValue<double>());
        var basis = provider["basis"]!;
        var bx = Vector(basis["x"]!); var by = Vector(basis["y"]!); var bz = Vector(basis["z"]!);
        if (!NavigationProfileValidation.Near(Vector(provider["up"]!), p.Plane.Up) ||
            !NavigationProfileValidation.ValidPlane(new(bz, bx, by))) return "NavigationBasisInvalid";
        bool ObjectMatches(NavigationObjectEvidence o, JsonObject selector, string domain) => o is not null &&
            JsonNode.DeepEquals(o.Selector, selector) && o.DomainId == domain && !string.IsNullOrWhiteSpace(o.RuntimeId) &&
            o.MatchCount == 1 && o.Space == "world3d" && Fresh(o.Sample) && o.Sample.SampleId == e.Sample.SampleId &&
            o.Sample.CapturedAt == e.Sample.CapturedAt && o.Position.IsFinite;
        if (!ObjectMatches(e.Actor, p.ActorSelector, p.ActorDomainId) || !e.Actor.Controllable) return "NavigationActorUnavailable";
        var foot = e.Actor.Position.Add(p.Capsule.FootOffset);
        if (!NavigationProfileValidation.Contains(p.Survey, foot) || foot.Dot(p.Plane.Up) < p.Survey.MinHeight ||
            foot.Dot(p.Plane.Up) > p.Survey.MaxHeight) return "NavigationActorUnavailable";
        if (e.Scalars.Length > 1000 || e.Targets.Length > 1000) return "NavigationEvidenceInvalid";
        NavigationScalarEvidence? Scalar(JsonObject read)
        {
            var found = e.Scalars.Where(s => JsonNode.DeepEquals(s.Read, read)).ToArray();
            if (found.Length != 1) return null;
            var s = found[0];
            if (s.Status != "available" || !NavigationProfileValidation.ValidScalar(s.Value) || !Fresh(s.Sample) || s.Sample.SampleId != e.Sample.SampleId ||
                s.Sample.CapturedAt != e.Sample.CapturedAt || !JsonNode.DeepEquals(s.Read["valueType"],
                    new JsonObject { ["type"] = s.Value["type"]?.DeepClone() })) return null;
            return s;
        }
        if (NavigationProfileValidation.Reads(p).Any(r => Scalar(r) is null)) return "NavigationObservationUnavailable";
        NavigationVector ReadVector(NavigationVectorReads r) => new(Scalar(r.X)!.Value["value"]!.GetValue<double>(),
            Scalar(r.Y)!.Value["value"]!.GetValue<double>(), Scalar(r.Z)!.Value["value"]!.GetValue<double>());
        var velocity = ReadVector(p.Motion.Velocity);
        if (!velocity.IsFinite || !double.IsFinite(velocity.Dot(velocity)) || velocity.Dot(velocity) > p.Motion.MaxSpeed * p.Motion.MaxSpeed ||
            !Scalar(p.Motion.Grounded)!.Value["value"]!.GetValue<bool>()) return "NavigationMotionUnavailable";
        if (p.Movement.Basis is { } relative && !NavigationProfileValidation.ValidPlane(new(p.Plane.Up,
                ReadVector(relative.AxisX), ReadVector(relative.AxisY)))) return "NavigationBasisInvalid";
        foreach (var t in p.Targets)
        {
            var targets = e.Targets.Where(o => JsonNode.DeepEquals(o.Selector, t.Selector)).ToArray();
            if (targets.Length != 1 || !ObjectMatches(targets[0], t.Selector, t.DomainId) ||
                !Scalar(t.Uncollected)!.Value["value"]!.GetValue<bool>()) return "NavigationTargetUnavailable";
        }
        return "Valid";
    }
}
