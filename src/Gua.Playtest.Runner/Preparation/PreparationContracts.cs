using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Preparation;

public enum HostMode { Launch, Attach }
public enum PlayMode { Explore, Replay }
public enum PreparationStage { Started, Ownership, Launch, Connect, Identity, Setup, Planner, Synchronize, Preconditions, Ready, RetryDelay }
public enum PreparationCode { Started, Completed, Busy, LaunchFailed, ConnectionFailed, IdentityMismatch, CapabilityUnavailable,
    OutstandingRequests, SetupForbidden, SetupFailed, SetupUnconfirmed, PlannerUnavailable, IdentityUnavailable, SynchronizationFailed, StaleObservation, PreconditionsUnsatisfied,
    Cancelled, Timeout, ProcessExited }
public sealed record PreparationEvent(PreparationStage Stage, PreparationCode Code);
/// <summary>Adapter to the common Trace lifecycle sink, entered before launching. Never Planner feedback.</summary>
public interface IPreparationTrace { void Record(PreparationEvent evidence); }

/// <summary>Explicit trusted host policy, separate from Scenario and from Planner input.</summary>
public sealed record LaunchCommand(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments);
public sealed record PreparationPolicy(HostMode HostMode, PlayMode PlayMode, Uri Endpoint, string ExpectedBuildId,
    string RequiredProtocol, string Profile, string Clock, IReadOnlyList<string> Capabilities, bool RequireBuildAttestation,
    bool StrictStart, LaunchCommand? Launch, TimeSpan OperationTimeout, TimeSpan RetryDelay, int ConnectAttempts,
    TimeSpan ShutdownTimeout);
public sealed record HostIdentity(string? AttestedGameBuildId, string Protocol, string Profile, string Clock,
    IReadOnlySet<string> Capabilities, string SourceId, string Epoch, bool HasOutstandingRequests);
public sealed record InitialBoundary(HostIdentity CapturedIdentity, bool Continuous, bool PreconditionsSatisfied,
    string CaptureRequestId, TimeSpan RealCapturedAt, string SynchronizationEvidence,
    RunObservation Observation, IRunObservationFeed Feed, bool CurrentRestorable);
public sealed record PreparedHost(RunStartBoundary Boundary, IRunObservationFeed Feed, bool CurrentRestorable);
public enum SetupReceipt { Confirmed, Failed, Unconfirmed }
/// <summary>Trusted Environment fixture mapping. Setup is not a play action, reset, or Planner capability.
/// The fixture internally enforces its explicit time/operation/permission ceilings before every dispatch.</summary>
public interface IApprovedSetup
{
    // Metadata reads are pure and safe from a worker thread. They may not dispatch,
    // touch Run/cleanup authority or require the engine thread; operation execution
    // remains the explicit async port below. Runner bounds and discards late reads.
    bool IsAuthorized(HostMode mode);
    IReadOnlyList<string> OperationIds { get; }
    IReadOnlySet<string> AllowedOperationIds { get; }
    int MaximumOperations { get; }
    TimeSpan Timeout { get; }
    ValueTask<SetupReceipt> ExecuteOperationAsync(int index, IPreparationConnection connection, CancellationToken cancellationToken);
}
public interface IPreparationConnection
{
    ValueTask<HostIdentity> IdentifyAsync(CancellationToken cancellationToken);
    // Must subscribe then capture, returning current identity and the same evaluation unit used for start preconditions.
    ValueTask<InitialBoundary> SynchronizeAsync(string captureRequestId, CancellationToken cancellationToken);
    // Closes only this connection and its owner-scoped input/subscriptions; never host reset/kill.
    ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken);
}
public interface IPreparationConnector
{
    // No Setup or input dispatch. Providers must release an acquisition completed after cancellation.
    ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
}
/// <summary>Only explicitly safe failure before connection/side-effect dispatch is retryable.</summary>
public sealed class ConnectionNotReadyException : Exception;
public interface IOwnedProcess
{
    bool HasExited { get; }
    ValueTask WaitForExitAsync(CancellationToken cancellationToken);
    ValueTask<bool> ShutdownAsync(CancellationToken cancellationToken);
}
public interface IProcessLauncher
{
    // Owns the newly created handle only. Never discovers/attaches by PID/name/port.
    ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken cancellationToken);
}
public interface IPreparationPlannerCheck
{
    // Capability/availability only: no decision requests and no setup route.
    ValueTask<bool> CheckAsync(CancellationToken cancellationToken);
}
public sealed class PreparationException(PreparationStage stage, PreparationCode code,
    Exception? innerException = null, RunPhase phase = RunPhase.Preparation)
    : RunFailureException(new(RunReason.ExecutionError, phase, RunOrigin.Host), innerException)
{
    public PreparationStage Stage { get; } = stage;
    public PreparationCode Code { get; } = code;
}
