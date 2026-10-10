namespace Gua.Playtest.Core;

/// <summary>Retained Gua Recording payload for one contiguous, complete part of a Plan.
/// OffsetBase is the preceding Recording offset, never an observed response timestamp.</summary>
public sealed record ReplayBatch(int BeforeStep, int Count, string RecordingJson, string Timing,
    long OffsetBaseMilliseconds, TimeSpan Timeout);
public enum ReplayCheck { Approved, Unsupported, PermissionDenied, DefinitionChanged, SecretUnavailable, InputsNotNeutral }
public enum ReplayReceiptStatus { Succeeded, Failed, Unconfirmed }
public sealed record ReplayReceipt(ReplayReceiptStatus Status, int CompletedSteps, bool NeutralConfirmed, Exception? OriginalException = null);

/// <summary>Trusted adapter only. The scheduler is Gua's. All synchronous context operations,
/// including each actual play enqueue, must pass through calls. Callbacks must be short and bounded.
/// Owner-scoped safety release uses separate bounded cleanup authority. No alternate route,
/// retry, reset or resource acquisition is allowed here.</summary>
public interface IReplayPlayback
{
    ReplayCheck Check(ReplayBatch batch, bool starting = true);
    ValueTask<ReplayReceipt> PlayAsync(ReplayBatch batch, IReplayCalls calls, CancellationToken cancellationToken);
}

/// <summary>Marshals Gua callbacks onto the serialized execution owner. Reads consume no action;
/// each play request uses its local batch index once, in order, at actual enqueue.</summary>
public interface IReplayCalls
{
    ValueTask<T> ReadAsync<T>(Func<T> callback, CancellationToken cancellationToken);
    ValueTask<T> SendAsync<T>(int actionIndex, Func<Action, ReplaySend<T>> callback, CancellationToken cancellationToken);
}
public sealed record ReplaySend<T>(T Value, bool Enqueued);
