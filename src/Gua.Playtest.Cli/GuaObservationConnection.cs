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

/// <summary>Snapshot observation feed with an optional explicit guarded UI owner.
/// Initial conditions are read-only; no Setup effects or Planner capability. No interval continuity claim.</summary>
internal sealed class GuaObservationConnection : IPreparationConnection, IRunObservationFeed
{
    private readonly BridgeObservations observations;
    private readonly IClock clock;
    private readonly PreparedCondition? success, failure;
    private readonly PreparedCondition? precondition;
    private bool initialReady = true;
    private readonly string build, profile;
    private readonly Action verifyOwner;
    private readonly Action<BridgeReadCollection, HostIdentity> recordCapture;
    private readonly object gate = new();
    private bool closed;
    private readonly bool enableClick;
    internal GuardedUiClick? Click { get; private set; }
    internal HostIdentity? LastIdentity { get; private set; }

    internal GuaObservationConnection(Uri endpoint, string build, string profile, IClock clock,
        PreparedCondition? success, PreparedCondition? failure, int maxNodes, int maxBytes, TimeSpan timeout, Action verifyOwner,
        Action<BridgeReadCollection, HostIdentity> recordCapture, bool enableClick = false, PreparedCondition? precondition = null)
    {
        this.build = build; this.profile = profile; this.clock = clock; this.success = success; this.failure = failure;
        this.verifyOwner = verifyOwner;
        this.recordCapture = recordCapture;
        this.enableClick = enableClick;
        this.precondition = precondition;
        observations = new(endpoint.AbsoluteUri, Enum.Parse<GuaObservationProfile>(profile), timeout, maxNodes, maxBytes);
    }
    public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token) => new(Task.Run(() =>
    {
        using var interrupt = token.Register(observations.Interrupt);
        return Cancellable(token, Identify);
    }, token));
    private HostIdentity Identify()
    {
        ObjectDisposedException.ThrowIf(closed, this); verifyOwner();
        var identity = observations.Identify();
        if (enableClick && Click is null) Click = observations.CreateGuardedUiClick();
        if (Click is not null && Click.SourceId != identity.SourceId) throw new InvalidOperationException("GuardedSourceChanged");
        LastIdentity = new(build, "2", profile, "real", enableClick ? new HashSet<string> { "observe_v1", "guarded_dispatch_v1" } : new HashSet<string> { "observe_v1" },
            identity.SourceId, identity.SessionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.HasOutstandingRequests);
        verifyOwner(); return LastIdentity;
    }
    public ValueTask<InitialBoundary> SynchronizeAsync(string requestId, CancellationToken token) => new(Task.Run(() =>
    {
        using var interrupt = token.Register(observations.Interrupt);
        return Cancellable(token, () =>
        {
            token.ThrowIfCancellationRequested(); observations.Resubscribe();
            var observation = Capture(includePrecondition: true);
            return new InitialBoundary(LastIdentity!, true, initialReady, requestId, observation.CapturedAt,
                "gua-subscription-and-fresh-read", observation, this, false);
        });
    }, token));
    public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => new(Task.Run(() =>
    {
        // Monitor supersession also cancels this token when work completes. Do not
        // destroy the shared source: the serial feed joins this bounded request before
        // its final fresh capture. Terminal cleanup separately interrupts the socket.
        return Cancellable(token, () => Capture());
    }, token));
    private T Cancellable<T>(CancellationToken token, Func<T> operation)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            try { var result = operation(); token.ThrowIfCancellationRequested(); return result; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }
    private RunObservation Capture(bool includePrecondition = false)
    {
        var identity = Identify();
        var setup = includePrecondition ? precondition : null;
        var requests = new[] { success, failure, setup }.Where(x => x is not null).SelectMany(x => x!.Leaves)
            .Select(x => JsonNode.Parse(x.DefinitionJson)!["read"]!.AsObject()).ToArray();
        var collections = observations.ReadBatch(requests);
        int index = 0;
        ConditionObservationUnit Unit(PreparedCondition? condition)
        {
            if (condition is null) return new([]);
            var leaves = new List<KeyValuePair<string, ConditionLeafObservation>>();
            foreach (var leaf in condition.Leaves)
            {
                var collection = collections[index++];
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
        var s = Unit(success); var f = Unit(failure); var initial = Unit(setup);
        var after = Identify();
        if (after.SourceId != identity.SourceId || after.Epoch != identity.Epoch) throw new InvalidOperationException("ObservationIdentityChanged");
        var capturedAt = clock.Elapsed;
        if (setup is not null)
        {
            var evaluated = setup.Start(clock, capturedAt).EvaluateAt(initial, capturedAt);
            // Immediate ordinary conditions report Open, not the temporal Satisfied latch.
            initialReady = evaluated.Evaluation.Truth == TruthValue.True && evaluated.Evaluation.Error == EvaluationError.None;
        }
        return new(capturedAt, s, f);
    }
    public ValueTask WaitForChangeAsync(CancellationToken token) => clock.DelayAsync(TimeSpan.FromMilliseconds(25), token);
    public ValueTask<bool> ReleaseAsync(CancellationToken token)
    {
        observations.Interrupt();
        return new(Task.Run(() =>
        {
            lock (gate)
            {
                if (closed) return true;
                Click?.Dispose();
                observations.Dispose();
                closed = true; return true;
            }
        }, CancellationToken.None));
    }
}
