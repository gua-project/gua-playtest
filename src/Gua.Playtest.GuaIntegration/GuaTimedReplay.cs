using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Runtime;
using Gua.Testing.Recording;

namespace Gua.Playtest.GuaIntegration;

/// <summary>Explicit ceilings from the effective Environment, not latency defaults.
/// Lease and execution/cleanup safety remain real-time even with a controlled simulation clock.</summary>
public sealed record GuaReplayTimingPolicy(long MaxSegmentMilliseconds, long MaxLatenessMilliseconds,
    long CleanupMilliseconds, GuaSegmentClock Clock, bool RequireApplicationTimes, bool RequireSameTickApplication);

/// <summary>Imports v2 through Gua and delegates scheduling, offsets, ordering, holds and cleanup
/// to its public Timed Segment executor. No Playtest scheduler or invented host capability.
/// The caller registers owner-scoped fallback input cleanup during host preparation.</summary>
public sealed class GuaTimedReplay : IReplayPlayback
{
    private readonly IGuaTimedSegmentHost host;
    private readonly Func<ReplayBatch, bool, ReplayCheck> admit;
    private readonly GuaReplayTimingPolicy policy;
    private readonly Func<string, GuaGameInputValueType?>? valueType;
    private readonly Func<string, JsonElement?>? secretResolver;
    public GuaTimedReplay(IGuaTimedSegmentHost host, Func<ReplayBatch, bool, ReplayCheck> admit,
        GuaReplayTimingPolicy policy, Func<string, GuaGameInputValueType?>? valueType = null,
        Func<string, JsonElement?>? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(admit); ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxSegmentMilliseconds is < 0 or > 60000 || policy.MaxLatenessMilliseconds is < 0 or > 60000 ||
            policy.CleanupMilliseconds is <= 0 or > 60000 || !Enum.IsDefined(policy.Clock)) throw new ArgumentException("ReplayTimingPolicyInvalid");
        this.host = host; this.admit = admit; this.policy = policy; this.valueType = valueType; this.secretResolver = secretResolver;
    }
    public ReplayCheck Check(ReplayBatch batch, bool starting = true)
    {
        Exception? providerFault = null;
        GuaTimedSegment segment;
        try
        {
            segment = Import(batch, valueType is null ? null : name =>
            {
                try { return valueType(name); }
                catch (Exception exception) { providerFault = exception; throw; }
            });
        }
        catch (Exception exception) when (providerFault is null && exception is (JsonException or InvalidDataException or ArgumentException or OverflowException or InvalidOperationException))
        { return ReplayCheck.Unsupported; }
        if (!host.OrderedApplication || segment.Clock == GuaSegmentClock.Simulation && string.IsNullOrWhiteSpace(host.SimulationScope) ||
            (segment.RequireApplicationTimes || segment.RequireSameTickApplication) && !host.ApplicationTimes ||
            segment.RequireSameTickApplication && !host.SameTickApplication) return ReplayCheck.Unsupported;
        return admit(batch, starting);
    }
    public ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken token)
        => new(Task.Run(async () =>
        {
            var queued = new QueuedHost(host, calls, () => admit(batch, false), token);
            Exception? secretFault = null;
            GuaTimedSegmentResult result;
            try
            {
                result = await GuaTimedSegmentReplay.ReplayAsync(queued, Import(batch), secretResolver is null ? null : name =>
                {
                    try { return secretResolver(name); }
                    catch (Exception exception) { secretFault = exception; throw; }
                }, token).ConfigureAwait(false);
            }
            catch when (secretFault is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(secretFault).Throw();
                throw;
            }
            var count = result.Inputs.Count(i => i.ResultReceivedMilliseconds is not null && i.Succeeded is not null);
            var status = result.Outcome == GuaSegmentOutcome.Succeeded ? ReplayReceiptStatus.Succeeded
                : result.Inputs.Any(i => i.SentMilliseconds is not null && i.ResultReceivedMilliseconds is null)
                    ? ReplayReceiptStatus.Unconfirmed : ReplayReceiptStatus.Failed;
            return new ReplayReceipt(status, count, result.NeutralConfirmed && result.CleanupSucceeded, queued.OriginalException);
        }, token));
    private GuaTimedSegment Import(ReplayBatch batch, Func<string, GuaGameInputValueType?>? importValueType = null)
    {
        if (batch.Timing is not ("recorded" or "conditionSynchronized") || batch.OffsetBaseMilliseconds < 0)
            throw new InvalidDataException("ReplayTimingInvalid");
        var json = JsonNode.Parse(batch.RecordingJson)!.AsObject();
        var steps = json["steps"]!.AsArray();
        if (steps.Count != batch.Count || batch.Count is < 1 or > 1000) throw new InvalidDataException("ReplayRangeInvalid");
        foreach (var step in steps)
        {
            var offset = checked((long)step!["relativeMilliseconds"]!.GetValue<decimal>());
            step["relativeMilliseconds"] = checked(offset - batch.OffsetBaseMilliseconds);
        }
        var duration = steps[^1]!["relativeMilliseconds"]!.GetValue<long>();
        if (duration > policy.MaxSegmentMilliseconds) throw new InvalidDataException("ReplaySegmentTooLong");
        var execution = checked((long)batch.Timeout.TotalMilliseconds - policy.CleanupMilliseconds);
        var segment = GuaTimedSegmentImport.FromRecording(json.ToJsonString(), duration, policy.MaxLatenessMilliseconds,
            execution, policy.CleanupMilliseconds, importValueType ?? valueType) with
        {
            Clock = policy.Clock, RequireApplicationTimes = policy.RequireApplicationTimes,
            RequireSameTickApplication = policy.RequireSameTickApplication
        };
        // Public playback requests may not tunnel reset/general cleanup commands. The
        // executor's own owner-scoped safety cleanup stays separate from Plan play requests.
        if (segment.Inputs.Any(i => i.Kind == GuaGameInputKind.Cleanup || i.Operation == GuaGameInputOperation.Reset))
            throw new InvalidDataException("ReplayOperationForbidden");
        GuaTimedSegmentFile.Validate(segment); return segment;
    }
    private sealed class QueuedHost(IGuaTimedSegmentHost host, IReplayCalls calls, Func<ReplayCheck> admit, CancellationToken token) : IGuaTimedSegmentValueHost
    {
        private int next;
        private bool cleaning;
        private bool dispatchingOnOwner;
        public Exception? OriginalException { get; private set; }
        private T Retain<T>(Func<T> callback)
        {
            try { return callback(); }
            catch (Exception exception) { OriginalException ??= exception; throw; }
        }
        private T Read<T>(Func<T> callback) => Retain(() => calls.ReadAsync(() => Retain(callback), token).AsTask().GetAwaiter().GetResult());
        public bool OrderedApplication => Read(() => host.OrderedApplication);
        public bool ApplicationTimes => Read(() => host.ApplicationTimes);
        public bool SameTickApplication => Read(() => host.SameTickApplication);
        public string? SimulationScope => Read(() => host.SimulationScope);
        // Gua's send guard samples its simulation clock inside the already serialized owner
        // callback. Queueing that nested read would block the owner that must pump it.
        public double SimulationMilliseconds => dispatchingOnOwner ? Retain(() => host.SimulationMilliseconds) : Read(() => host.SimulationMilliseconds);
        public string? ExecutionFailureCode => cleaning ? Retain(() => host.ExecutionFailureCode) : Read(() => host.ExecutionFailureCode);
        public void Begin(GuaTimedSegment segment) => Read(() => { host.Begin(segment); return true; });
        public void Begin(GuaTimedSegment segment, IReadOnlyList<JsonElement?> values)
            => Read(() => { if (host is IGuaTimedSegmentValueHost valueHost) valueHost.Begin(segment, values); else host.Begin(segment); return true; });
        public ulong Send(GuaTimedInput input, JsonElement? secret, Action verifySendBoundary)
        {
            var request = calls.SendAsync(next, beforeSend =>
            {
                dispatchingOnOwner = true;
                try
                {
                    var id = Retain(() => host.Send(input, secret, () =>
                    {
                        verifySendBoundary();
                        if (admit() != ReplayCheck.Approved) throw new InvalidOperationException("ReplayAdmissionChanged");
                        beforeSend();
                    }));
                    return new ReplaySend<ulong>(id, id != 0);
                }
                finally { dispatchingOnOwner = false; }
            }, token).AsTask().GetAwaiter().GetResult();
            next++; return request;
        }
        public GuaTimedCompletion? Poll(ulong requestId) => cleaning ? Retain(() => host.Poll(requestId)) : Read(() => host.Poll(requestId));
        // Gua's bounded cleanup has fresh authority after caller cancellation/Run closure.
        // It never invokes Plan play requests or touches RunSession; this exact host owns its input.
        public ulong ReleaseAll() { cleaning = true; return Retain(host.ReleaseAll); }
        public bool IsNeutral => cleaning ? Retain(() => host.IsNeutral) : Read(() => host.IsNeutral);
        public void End() => Retain(() => { host.End(); return true; });
    }
}
