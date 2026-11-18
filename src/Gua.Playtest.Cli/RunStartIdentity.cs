using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Cli;

/// <summary>Trusted in-process start information, before artifact acquisition or game launch.
/// Not host attestation, a remote credential or a primary-result authority.</summary>
public sealed class RunStartIdentity
{
    internal RunStartIdentity(string runId, RunIdentity identity, string sourceEnvironmentSha256)
    { RunId = runId; Identity = identity; SourceEnvironmentSha256 = sourceEnvironmentSha256; }
    public string RunId { get; }
    public RunIdentity Identity { get; }
    public string SourceEnvironmentSha256 { get; }
}

internal static class RunAdmission
{
    // Synchronous trusted bookkeeping only: no blocking I/O, remote calls or game access.
    // This callback is not sandboxed. Check cancellation both sides so late approval
    // cannot start a game after the caller has cancelled.
    internal static void Require(Func<RunStartIdentity, CancellationToken, bool>? admit,
        string runId, RunIdentity identity, string sourceEnvironmentSha256, CancellationToken token)
    {
        if (admit is null) return;
        token.ThrowIfCancellationRequested();
        bool accepted;
        try { accepted = admit(new(runId, identity, sourceEnvironmentSha256), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidDataException("RunAdmissionFailed"); }
        token.ThrowIfCancellationRequested();
        if (!accepted) throw new InvalidDataException("RunAdmissionRejected");
    }
}
