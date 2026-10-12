using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Preparation;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class NavigationPreparationTests
{
    internal static async Task<NavigationPreparationPolicy> Policy()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "navigation-environment.json");
        var report = await new StaticContractValidator([Path.GetDirectoryName(file)!]).ValidateFileAsync(file);
        Assert.True(report.IsValid, report.Code);
        var e = Assert.IsType<EnvironmentDocument>(report.Document);
        return new(e.Navigation!, "walk-room", new HashSet<string> { "move" }, e.Permissions.Reads, 50);
    }
    private static readonly HostIdentity Identity = new("game", "2", "Player", "real", new HashSet<string>(), "source", "1", false);
    private static readonly NavigationSample Sample = new("source", "1", "capture", "run-real", "physics-1",
        TimeSpan.FromMilliseconds(10), "room", 1);
    private static NavigationStartEvidence Evidence(NavigationProfile p)
    {
        var actions = JsonNode.Parse("""
            {"schemaVersion":1,"sessionEpoch":1,"revision":0,"context":"game","actions":[
             {"id":"move","description":"translation declared by author","valueType":"vector2","holdable":true,
              "active":true,"bindings":[],"risk":"safe","requiresConfirmation":false,"range":{"minimum":-1,"maximum":1}}]}
            """)!.AsObject();
        var provider = JsonNode.Parse("""
            {"schemaVersion":"spatial-r1","documentType":"provider","providerId":"fixture","spaceId":"room","spaceEpoch":1,
             "worldSpace":"world3d","basis":{"x":{"x":1,"y":0,"z":0},"y":{"x":0,"y":1,"z":0},"z":{"x":0,"y":0,"z":1}},
             "up":{"x":0,"y":1,"z":0},"unit":{"label":"fixture units"},"precision":{"representation":"binary64","reason":"synthetic"},
             "operations":["raycast","overlap","sweep"],"shapes":["capsule"],"policies":["walking"],"consistencies":["samePhysicsSample"],
             "engine":{"name":"Fake","version":"1","backend":"Fake","backendVersion":"1"},
             "limits":{"maxQueriesPerBatch":64,"maxHitsPerQuery":32,"maxDeadlineMs":20}}
            """)!.AsObject();
        var scalars = NavigationProfileValidation.Reads(p).Select(r => new NavigationScalarEvidence(r,
            new JsonObject { ["type"] = r["valueType"]!["type"]!.DeepClone(),
                ["value"] = r["valueType"]!["type"]!.GetValue<string>() == "bool" ? JsonValue.Create(true) : JsonValue.Create(0.0) }, "available", Sample)).ToArray();
        return new(Sample, actions, provider, p.Movement, p.Survey.Topology, "real", true, true, true, p.Survey,
            new(p.ActorSelector, p.ActorDomainId, "runtime-player-1", new(0,0,0), "world3d", Sample, true, 1),
            p.Targets.Select(t => new NavigationObjectEvidence(t.Selector, t.DomainId, "runtime-coin-1", new(1,0,1), "world3d", Sample, false, 1)).ToArray(), scalars);
    }
    private static string Check(NavigationPreparationPolicy p, NavigationStartEvidence? e, TimeSpan? now = null) =>
        NavigationPreparationCheck.Validate(p, e, Identity, "capture", Sample.CapturedAt, now ?? Sample.CapturedAt, "run-real");
    [Fact]
    public async Task ValidSyntheticReadinessDoesNotDependOnRuntimeIds()
    {
        var p = await Policy(); var e = Evidence(p.Profile);
        Assert.Equal("Valid", Check(p,e));
        Assert.Equal("Valid", Check(p,e with { Targets = [e.Targets[0] with { RuntimeId = "new-runtime-id" }] }));
        Assert.Equal("NavigationTargetUnavailable", Check(p,e with { Targets = [e.Targets[0] with { DomainId = "other-domain" }] }));
    }
    [Theory]
    [InlineData("missing", "NavigationEvidenceMissing")]
    [InlineData("stale", "NavigationStaleObservation")]
    [InlineData("clockId", "NavigationStaleObservation")]
    [InlineData("epoch", "NavigationStaleObservation")]
    [InlineData("space", "NavigationStaleObservation")]
    [InlineData("future", "NavigationStaleObservation")]
    [InlineData("request", "NavigationStaleObservation")]
    [InlineData("motion", "NavigationUnsupported")]
    [InlineData("topology", "NavigationUnsupported")]
    [InlineData("loaded", "NavigationRegionUnconfirmed")]
    [InlineData("clock", "NavigationClockUnavailable")]
    [InlineData("release", "NavigationClockUnavailable")]
    [InlineData("shape", "NavigationCapabilityUnavailable")]
    [InlineData("policy", "NavigationCapabilityUnavailable")]
    [InlineData("operation", "NavigationCapabilityUnavailable")]
    [InlineData("action", "NavigationActionUnavailable")]
    [InlineData("range", "NavigationInputRangeUnavailable")]
    [InlineData("actor", "NavigationActorUnavailable")]
    [InlineData("ambiguous", "NavigationActorUnavailable")]
    [InlineData("grounded", "NavigationMotionUnavailable")]
    [InlineData("missingScalar", "NavigationObservationUnavailable")]
    [InlineData("mixedSample", "NavigationObservationUnavailable")]
    [InlineData("collected", "NavigationTargetUnavailable")]
    public async Task RejectsIncompleteOrIncompatibleHostEvidence(string mutation, string code)
    {
        var p = await Policy(); var e = Evidence(p.Profile);
        switch (mutation)
        {
            case "missing": Assert.Equal(code, Check(p,null)); return;
            case "stale": Assert.Equal(code, Check(p,e,TimeSpan.FromSeconds(1))); return;
            case "clockId": e = e with { Sample = Sample with { ClockId = "game-clock" } }; break;
            case "epoch": e = e with { Sample = Sample with { Epoch = "2" } }; break;
            case "space": e = e with { Sample = Sample with { SpaceEpoch = 2 } }; break;
            case "future": e = e with { Sample = Sample with { CapturedAt = TimeSpan.FromSeconds(1) } }; break;
            case "request": e = e with { Sample = Sample with { CaptureRequestId = "old" } }; break;
            case "motion": e = e with { Movement = e.Movement with { RelativeTo = "body" } }; break;
            case "topology": e = e with { Topology = "stackedLayers" }; break;
            case "loaded": e = e with { LoadedRegionConfirmed = false }; break;
            case "clock": e = e with { Clock = "game" }; break;
            case "release": e = e with { NeutralAtSegmentEndSupported = false }; break;
            case "shape": e.SpatialProvider["shapes"] = new JsonArray("sphere"); break;
            case "policy": e.SpatialProvider["policies"] = new JsonArray("other"); break;
            case "operation": e.SpatialProvider["operations"] = new JsonArray("raycast"); break;
            case "action": e.ActionDefinitions["actions"]![0]!["valueType"] = "axis1d"; break;
            case "range": e.ActionDefinitions["actions"]![0]!.AsObject().Remove("range"); break;
            case "actor": e = e with { Actor = e.Actor with { Controllable = false } }; break;
            case "ambiguous": e = e with { Actor = e.Actor with { MatchCount = 2 } }; break;
            case "grounded": e.Scalars.Single(s => JsonNode.DeepEquals(s.Read, p.Profile.Motion.Grounded)).Value["value"] = false; break;
            case "missingScalar": e = e with { Scalars = e.Scalars[1..] }; break;
            case "mixedSample": e.Scalars[0] = e.Scalars[0] with { Sample = Sample with { SampleId = "old" } }; break;
            case "collected": e.Scalars[^1].Value["value"] = false; break;
        }
        Assert.Equal(code, Check(p,e));
    }
    [Theory]
    [InlineData("camera")]
    [InlineData("body")]
    public async Task RelativeBasisRequiresFreshGroupedPublicScalars(string relative)
    {
        var p = await Policy();
        NavigationVectorReads Reads(string prefix) => new(Read(prefix+"X"), Read(prefix+"Y"), Read(prefix+"Z"));
        JsonObject Read(string name) => new() { ["target"] = new JsonObject { ["source"]="world" }, ["region"]="property", ["name"]=name, ["valueType"]=new JsonObject { ["type"]="number" } };
        var profile = p.Profile with { Movement = p.Profile.Movement with { RelativeTo = relative, Basis = new(Reads("basisX"), Reads("basisY")) } };
        p = p with { Profile = profile, AllowedReads = NavigationProfileValidation.Reads(profile).ToArray() };
        var e = Evidence(profile);
        foreach (var r in NavigationProfileValidation.Components(profile.Movement.Basis!.AxisX))
            e.Scalars.Single(s => JsonNode.DeepEquals(s.Read,r)).Value["value"] = r["name"]!.GetValue<string>().EndsWith("X",StringComparison.Ordinal) ? 1.0 : 0.0;
        foreach (var r in NavigationProfileValidation.Components(profile.Movement.Basis.AxisY))
            e.Scalars.Single(s => JsonNode.DeepEquals(s.Read,r)).Value["value"] = r["name"]!.GetValue<string>().EndsWith("Z",StringComparison.Ordinal) ? 1.0 : 0.0;
        Assert.Equal("Valid", Check(p,e));
        var scalar = Array.FindIndex(e.Scalars,s => JsonNode.DeepEquals(s.Read,profile.Movement.Basis.AxisX.X));
        e.Scalars[scalar] = e.Scalars[scalar] with { Sample = Sample with { CapturedAt = TimeSpan.Zero } };
        Assert.Equal("NavigationObservationUnavailable", Check(p,e));
    }
    [Fact]
    public async Task PivotOutsideSurveyDoesNotReplaceFootOrApprovedApproachBounds()
    {
        var p = await Policy();
        p = p with { Profile = p.Profile with { Capsule = p.Profile.Capsule with {
            FootOffset = new(0,-4,0), CenterOffset = new(0,-3.1,0) } } };
        var e = Evidence(p.Profile);
        e = e with { Actor = e.Actor with { Position = new(0,4,0) },
            Targets = [e.Targets[0] with { Position = new(1,10,1) }] };
        Assert.Equal("Valid", Check(p,e));
        Assert.Equal("NavigationActorUnavailable", Check(p,e with { Actor = e.Actor with { Position = new(0,6,0) } }));
        Assert.Equal("NavigationTargetUnavailable", Check(p,e with { Targets = [e.Targets[0] with { Position = new(double.NaN,0,0) }] }));
    }
    [Fact]
    public async Task EffectivePermissionsAndSegmentLimitsAreRequired()
    {
        var p = await Policy();
        Assert.Equal("NavigationPermissionDenied", Check(p with { AllowedActionIds = new HashSet<string>() }, Evidence(p.Profile)));
        Assert.Equal("NavigationPermissionDenied", Check(p with { AllowedReads = [] }, Evidence(p.Profile)));
        Assert.Equal("NavigationPermissionDenied", Check(p with { MaxSegmentMilliseconds = 49 }, Evidence(p.Profile)));
    }
}
