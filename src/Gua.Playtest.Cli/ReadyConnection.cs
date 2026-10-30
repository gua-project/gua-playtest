using System.Net.WebSockets;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>A TCP listener/object is not a ready WebSocket connection. Read-only handshake
/// failures belong to the existing bounded Connect retry, before ownership is handed off.</summary>
internal static class ReadyConnection
{
    internal static async ValueTask<T> AcquireAsync<T>(Func<T> create, CancellationToken token) where T : IPreparationConnection
    {
        var connection = create();
        try
        {
            await connection.IdentifyAsync(token);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch (Exception error)
        {
            // Never retry while the unregistered previous connection remains unconfirmed.
            if (!await connection.ReleaseAsync(CancellationToken.None)) throw new InvalidOperationException("UnregisteredReleaseUnconfirmed", error);
            token.ThrowIfCancellationRequested();
            if (error is WebSocketException or OperationCanceledException) throw new ConnectionNotReadyException();
            throw;
        }
    }
}

/// <summary>Connector acquisitions remain owned until HostPreparation accepts handoff.
/// A failed self-release remains a final cleanup obligation, not a forgotten retry.</summary>
internal sealed class PendingConnections
{
    private readonly object gate = new();
    private readonly HashSet<IPreparationConnection> connections = new(ReferenceEqualityComparer.Instance);
    private bool closed;
    internal T Track<T>(Func<T> create) where T : IPreparationConnection
    {
        lock (gate)
        {
            if (closed || connections.Count >= 100) throw new InvalidOperationException("ConnectionAcquisitionClosed");
            var connection = create(); connections.Add(connection); return connection;
        }
    }
    internal bool Handoff(IPreparationConnection connection)
    { lock (gate) { if (closed) return false; return connections.Remove(connection); } }
    internal async ValueTask<bool> ReleaseAsync(CancellationToken token)
    {
        IPreparationConnection[] snapshot; lock (gate) { closed = true; snapshot = connections.ToArray(); }
        bool complete = true;
        var errors = new List<Exception>();
        foreach (var connection in snapshot)
        {
            try
            {
                if (await connection.ReleaseAsync(token)) { lock (gate) connections.Remove(connection); }
                else complete = false;
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
        return complete;
    }
}
