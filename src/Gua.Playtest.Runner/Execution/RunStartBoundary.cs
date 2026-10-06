using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Execution;

/// <summary>A session-owned readiness capability. The owner arms it only after Setup/compatibility are ready.
/// The adapter must synchronously correlate its new subscription/capture to RequestId; it cannot reuse preparation evidence.</summary>
public sealed class RunStartCapture
{
    private readonly RunSession owner;
    private readonly TimeSpan readyReal, readyCondition;
    private bool certified;
    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    internal RunStartCapture(RunSession owner, TimeSpan readyReal, TimeSpan readyCondition)
    { this.owner = owner; this.readyReal = readyReal; this.readyCondition = readyCondition; }
    public RunStartBoundary Certify(string captureRequestId, TimeSpan capturedRealAt,
        RunObservation initialObservation, string synchronizationEvidence, bool preconditionsSatisfied)
    {
        if (certified || owner.State != ExecutionState.Preparing || captureRequestId != RequestId ||
            !preconditionsSatisfied || string.IsNullOrWhiteSpace(synchronizationEvidence) || synchronizationEvidence.Length > 256)
            throw new InvalidOperationException("RunningBoundaryUncertified");
        if (initialObservation is null)
            owner.RejectStartObservation("RunningBoundaryObservationMissing", new ArgumentNullException(nameof(initialObservation)));
        if (initialObservation.Success is null)
            owner.RejectStartObservation("RunningBoundarySuccessMissing", new ArgumentNullException(nameof(initialObservation.Success)));
        if (initialObservation.Failure is null)
            owner.RejectStartObservation("RunningBoundaryFailureMissing", new ArgumentNullException(nameof(initialObservation.Failure)));
        if (capturedRealAt < readyReal || initialObservation.CapturedAt < readyCondition)
            owner.RejectStartObservation("RunningBoundaryUncertified");
        owner.ValidateStartTimes(capturedRealAt, initialObservation.CapturedAt);
        certified = true;
        return new(owner, capturedRealAt, initialObservation, synchronizationEvidence);
    }
}

/// <summary>Opaque certificate for one synchronized Running subscription/capture boundary.
/// It retains the exact initial evidence unit, paired clock times and adapter synchronization evidence.</summary>
public sealed class RunStartBoundary
{
    internal RunSession Owner { get; }
    internal bool Used { get; set; }
    public TimeSpan RealCapturedAt { get; }
    public RunObservation InitialObservation { get; }
    public string SynchronizationEvidence { get; }
    internal RunStartBoundary(RunSession owner, TimeSpan realCapturedAt, RunObservation observation, string evidence)
    { Owner = owner; RealCapturedAt = realCapturedAt; InitialObservation = observation; SynchronizationEvidence = evidence; }
}
