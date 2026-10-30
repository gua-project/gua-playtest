using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>Single-leaf observation lane. Never acquires input, performs Setup, or grants a Planner capability.
/// Snapshot reads claim no interval continuity. Unsupported multi-capture conditions are refused up front.</summary>
internal sealed class GuaObservationConnection : IPreparationConnection, IRunObservationFeed
{
    private readonly BridgeObservations observations;
    private readonly IClock clock;
    private readonly PreparedCondition? success, failure;
    private readonly string build, profile;
    private readonly Action verifyOwner;
    private readonly Action<BridgeReadCollection, HostIdentity> recordCapture;
    private readonly object gate = new();
    private bool closed;
    internal HostIdentity? LastIdentity { get; private set; }

    internal GuaObservationConnection(Uri endpoint, string build, string profile, IClock clock,
        PreparedCondition? success, PreparedCondition? failure, int maxNodes, int maxBytes, TimeSpan timeout, Action verifyOwner,
        Action<BridgeReadCollection, HostIdentity> recordCapture)
    {
        this.build = build; this.profile = profile; this.clock = clock; this.success = success; this.failure = failure;
        this.verifyOwner = verifyOwner;
        this.recordCapture = recordCapture;
        observations = new(endpoint.AbsoluteUri, Enum.Parse<GuaObservationProfile>(profile), timeout, maxNodes, maxBytes);
    }
    public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token) => new(Task.Run(() =>
    {
        lock (gate) { token.ThrowIfCancellationRequested(); return Identify(); }
    }, token));
    private HostIdentity Identify()
    {
        ObjectDisposedException.ThrowIf(closed, this); verifyOwner();
        var identity = observations.Identify();
        LastIdentity = new(build, "2", profile, "real", new HashSet<string> { "observe_v1" },
            identity.SourceId, identity.SessionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.HasOutstandingRequests);
        verifyOwner(); return LastIdentity;
    }
    public ValueTask<InitialBoundary> SynchronizeAsync(string requestId, CancellationToken token) => new(Task.Run(() =>
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); observations.Resubscribe();
            var observation = Capture();
            return new InitialBoundary(LastIdentity!, true, true, requestId, observation.CapturedAt,
                "gua-subscription-and-fresh-read", observation, this, false);
        }
    }, token));
    public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => new(Task.Run(() =>
    { lock (gate) { token.ThrowIfCancellationRequested(); return Capture(); } }, token));
    private RunObservation Capture()
    {
        var identity = Identify();
        ConditionObservationUnit Unit(PreparedCondition? condition)
        {
            if (condition is null) return new([]);
            var leaves = new List<KeyValuePair<string, ConditionLeafObservation>>();
            foreach (var leaf in condition.Leaves)
            {
                var read = JsonNode.Parse(leaf.DefinitionJson)!["read"]!.AsObject();
                var collection = observations.Read(read);
                if (collection.Reads.Any(r => r.Identity is { } i && (i.SourceId != identity.SourceId || i.SessionEpoch.ToString() != identity.Epoch)))
                    throw new InvalidOperationException("ObservationIdentityChanged");
                recordCapture(collection, identity);
                var targets = collection.Reads.Select(r => new ConditionTargetObservation(
                    r.Identity is { } i ? JsonSerializer.Serialize(new { i.SourceId, i.SessionEpoch, i.Profile, i.Source, i.RuntimeId,
                        i.OwnerId, i.RegistrationId, i.ValueTypeIdentity }) : "unavailable",
                    r.Availability == ReadAvailability.Available ? r.Value?.GetRawText() : null)).ToArray();
                leaves.Add(new(leaf.ConditionPath, new(identity.SourceId + "/" + identity.Epoch + "/" + profile + "/" + leaf.DefinitionJson,
                    collection.Availability == ReadAvailability.Available, targets, false,
                    collection.Availability == ReadAvailability.Available || targets.Length != 0 ? null : EvaluationCode.Unavailable)));
            }
            return new(leaves);
        }
        var s = Unit(success); var f = Unit(failure);
        var after = Identify();
        if (after.SourceId != identity.SourceId || after.Epoch != identity.Epoch) throw new InvalidOperationException("ObservationIdentityChanged");
        return new(clock.Elapsed, s, f);
    }
    public ValueTask WaitForChangeAsync(CancellationToken token) => clock.DelayAsync(TimeSpan.FromMilliseconds(25), token);
    public ValueTask<bool> ReleaseAsync(CancellationToken token) => new(Task.Run(() =>
    {
        lock (gate)
        {
            if (closed) return true;
            closed = true;
            observations.Dispose();
            return true;
        }
    }, CancellationToken.None));
}
