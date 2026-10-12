using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gua.Playtest.Core.Contracts;

// Vector representation, space identity, capsule endpoints and query policy use Gua spatial-r1.
// These are configuration and pure calculations, never a movement or spatial-query authority.
public sealed record NavigationVector(double X, double Y, double Z)
{
    public double Dot(NavigationVector b) => X * b.X + Y * b.Y + Z * b.Z;
    public NavigationVector Add(NavigationVector b) => new(X + b.X, Y + b.Y, Z + b.Z);
    public NavigationVector Scale(double k) => new(X * k, Y * k, Z * k);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}
public sealed record NavigationPlane(NavigationVector Up, NavigationVector AxisX, NavigationVector AxisY);
public sealed record NavigationCapsule(string Representation, double Radius, double Height,
    NavigationVector FootOffset, NavigationVector CenterOffset);
public sealed record NavigationAxis(double Minimum, double Maximum, int Polarity);
public sealed record NavigationVectorReads(JsonObject X, JsonObject Y, JsonObject Z);
public sealed record NavigationBasisReads(NavigationVectorReads AxisX, NavigationVectorReads AxisY);
public sealed record NavigationMovement(string Model, string ActionId, string RelativeTo,
    NavigationAxis AxisX, NavigationAxis AxisY, string Release, NavigationBasisReads? Basis = null);
public sealed record NavigationMotion(double MaxSpeed, double MaxStoppingDistance, long MaxStoppingMilliseconds,
    NavigationVectorReads Velocity, JsonObject Grounded);
public sealed record NavigationArrival(double Radius, double SpeedTolerance, double HeightTolerance, double MaxSlopeDegrees);
public sealed record NavigationSurvey(NavigationVector Min, NavigationVector Max, double MinHeight, double MaxHeight,
    double CellSize, double SupportSpacing, string Topology);
public sealed record NavigationBudgets(long MaxObservationAgeMilliseconds, long MaxInputMilliseconds,
    long MaxQueries, long MaxNodes, long MaxEdges, long MaxReplans, long MaxLlmCalls,
    long QueryDeadlineMilliseconds, long MaxApproachCandidates);
public sealed record NavigationTarget(JsonObject Selector, string DomainId, NavigationVector[] ApproachPoints,
    double ApproachRadius, JsonObject Uncollected);
public sealed record NavigationPreparation(string FixtureId, string Clock);
public sealed record NavigationProvenance(string Author, string ParameterSource);
public sealed record NavigationProfile(int SchemaVersion, JsonObject ActorSelector, string ActorDomainId,
    string PositionField, string SpaceId, long SpaceEpoch, NavigationPlane Plane, NavigationCapsule Capsule,
    string QueryPolicyId, string Consistency, NavigationMovement Movement, NavigationMotion Motion,
    NavigationArrival Arrival, NavigationSurvey Survey, NavigationBudgets Budgets, NavigationTarget[] Targets,
    NavigationPreparation Preparation, NavigationProvenance Provenance);

public static class NavigationProfileValidation
{
    public static bool ValidScalar(JsonObject value)
    {
        try
        {
            ContractDecoder.Decode(System.Text.Encoding.UTF8.GetBytes(value.ToJsonString()), ".json");
            ContractSchemas.Validate("value-v1.schema.json", value);
            return true;
        }
        catch (Exception e) when (e is ContractException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return false; }
    }
    public static NavigationProfile Copy(NavigationProfile p) => JsonSerializer.Deserialize<NavigationProfile>(
        JsonSerializer.Serialize(p, ContractJson.Options), ContractJson.Options)!;
    public static string ValidateHostDefinitions(JsonObject actions, JsonObject provider)
    {
        try
        {
            // Bounded decoding checks nonfinite values before the reused schemas.
            ContractDecoder.Decode(System.Text.Encoding.UTF8.GetBytes(actions.ToJsonString()), ".json");
            ContractDecoder.Decode(System.Text.Encoding.UTF8.GetBytes(provider.ToJsonString()), ".json");
            ContractSchemas.Validate("game-input-actions.schema.json", actions);
            ContractSchemas.Validate("spatial-r1.schema.json", provider);
            if (provider["documentType"]?.GetValue<string>() != "provider") return "NavigationCapabilityUnavailable";
            return "Valid";
        }
        catch (Exception e) when (e is ContractException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return "NavigationCapabilityUnavailable"; }
    }
    /// <summary>Offline structure and internal consistency only. Does not inspect a host or calibrate movement.</summary>
    public static string Validate(NavigationProfile profile)
    {
        try
        {
            var json = JsonSerializer.SerializeToNode(profile, ContractJson.Options)!;
            ContractSchemas.Validate("navigation-profile.schema.json", json);
            ValidateSemantics(profile);
            return "Valid";
        }
        catch (ContractException e) { return e.Code; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        { return "NavigationProfileInvalid"; }
    }

    internal static void ValidateSemantics(NavigationProfile p)
    {
        if (!ValidPlane(p.Plane)) Reject("NavigationBasisInvalid");
        if (p.Capsule.Height < 2 * p.Capsule.Radius) Reject("NavigationShapeInvalid");
        var expectedCenter = p.Capsule.FootOffset.Add(p.Plane.Up.Scale(p.Capsule.Height / 2));
        if (!Near(expectedCenter, p.Capsule.CenterOffset)) Reject("NavigationShapeInvalid");
        if (p.Movement.RelativeTo == "world" ? p.Movement.Basis is not null : p.Movement.Basis is null)
            Reject("NavigationBasisRequired");
        if (p.Movement.AxisX.Minimum >= 0 || p.Movement.AxisX.Maximum <= 0 || p.Movement.AxisY.Minimum >= 0 || p.Movement.AxisY.Maximum <= 0)
            Reject("NavigationInputRangeInvalid");
        if (p.Arrival.SpeedTolerance > p.Motion.MaxSpeed || !double.IsFinite(p.Motion.MaxSpeed * p.Motion.MaxSpeed) ||
            p.Budgets.MaxInputMilliseconds > 60000)
            Reject("NavigationLimitInvalid");
        var s = p.Survey;
        if (s.Min.X >= s.Max.X || s.Min.Y >= s.Max.Y || s.Min.Z >= s.Max.Z || s.MinHeight >= s.MaxHeight)
            Reject("NavigationRegionInvalid");
        // Bound arithmetic as well as declared budgets. Overflow/degenerate scales cannot create an unbounded grid.
        double cells = Math.Ceiling((s.Max.X - s.Min.X) / s.CellSize) * Math.Ceiling((s.Max.Y - s.Min.Y) / s.CellSize)
            * Math.Ceiling((s.Max.Z - s.Min.Z) / s.CellSize);
        if (!double.IsFinite(cells) || !double.IsFinite((s.Max.X - s.Min.X + s.Max.Y - s.Min.Y + s.Max.Z - s.Min.Z) / s.SupportSpacing))
            Reject("NavigationLimitInvalid");
        NumberVector(p.Motion.Velocity);
        Scalar(p.Motion.Grounded, "bool");
        if (p.Movement.Basis is { } basis) { NumberVector(basis.AxisX); NumberVector(basis.AxisY); }
        var domains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in p.Targets)
        {
            if (!domains.Add(t.DomainId)) Reject("NavigationIdentityInvalid");
            Scalar(t.Uncollected, "bool");
            if (!SameActor(t.Uncollected, t.Selector)) Reject("NavigationIdentityInvalid");
            if (t.ApproachPoints.LongLength > p.Budgets.MaxApproachCandidates) Reject("NavigationLimitInvalid");
            foreach (var point in t.ApproachPoints)
                if (!Contains(s, point) || point.Dot(p.Plane.Up) < s.MinHeight || point.Dot(p.Plane.Up) > s.MaxHeight)
                    Reject("NavigationApproachInvalid");
        }
        foreach (var read in Reads(p))
            if (read["target"]?["source"]?.GetValue<string>() == "ui") Reject("NavigationObservationInvalid");
        if (!SameActor(p.Motion.Grounded, p.ActorSelector)) Reject("NavigationIdentityInvalid");
    }
    public static IEnumerable<JsonObject> Reads(NavigationProfile p)
    {
        foreach (var r in Components(p.Motion.Velocity)) yield return r;
        yield return p.Motion.Grounded;
        if (p.Movement.Basis is { } b)
            foreach (var r in Components(b.AxisX).Concat(Components(b.AxisY))) yield return r;
        foreach (var t in p.Targets) yield return t.Uncollected;
    }
    public static IEnumerable<JsonObject> Components(NavigationVectorReads v) { yield return v.X; yield return v.Y; yield return v.Z; }
    private static void NumberVector(NavigationVectorReads v)
    {
        foreach (var r in Components(v)) Scalar(r, "number");
        var refs = Components(v).ToArray();
        if (JsonNode.DeepEquals(refs[0], refs[1]) || JsonNode.DeepEquals(refs[0], refs[2]) || JsonNode.DeepEquals(refs[1], refs[2]))
            Reject("NavigationObservationInvalid");
    }
    private static void Scalar(JsonObject r, string type)
    { ContractSemantics.CheckRead(r); if (r["valueType"]?["type"]?.GetValue<string>() != type) Reject("NavigationObservationInvalid"); }
    private static bool SameActor(JsonObject r, JsonObject selector) => r["target"]?["source"]?.GetValue<string>() == "object"
        && JsonNode.DeepEquals(r["target"]?["selector"], selector);
    public static bool Contains(NavigationSurvey s, NavigationVector v) => v.IsFinite && v.X >= s.Min.X && v.X <= s.Max.X
        && v.Y >= s.Min.Y && v.Y <= s.Max.Y && v.Z >= s.Min.Z && v.Z <= s.Max.Z;
    public static bool ValidPlane(NavigationPlane p) => Unit(p.Up) && Unit(p.AxisX) && Unit(p.AxisY)
        && Math.Abs(p.Up.Dot(p.AxisX)) <= 1e-6 && Math.Abs(p.Up.Dot(p.AxisY)) <= 1e-6 && Math.Abs(p.AxisX.Dot(p.AxisY)) <= 1e-6;
    private static bool Unit(NavigationVector v) => v.IsFinite && Math.Abs(v.Dot(v) - 1) <= 1e-6;
    public static bool Near(NavigationVector a, NavigationVector b) => a.IsFinite && b.IsFinite
        && Math.Abs(a.X - b.X) <= 1e-6 && Math.Abs(a.Y - b.Y) <= 1e-6 && Math.Abs(a.Z - b.Z) <= 1e-6;
    private static void Reject(string code) => throw new ContractException(code);
}
