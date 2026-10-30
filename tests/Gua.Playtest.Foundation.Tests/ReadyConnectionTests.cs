using System.Net.WebSockets;
using Gua.Playtest.Cli;
using Gua.Playtest.Runner.Preparation;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class ReadyConnectionTests
{
    private sealed class Connection(Func<CancellationToken, ValueTask<HostIdentity>> identify, bool release = true) : IPreparationConnection
    {
        internal int Releases;
        public ValueTask<HostIdentity> IdentifyAsync(CancellationToken token) => identify(token);
        public ValueTask<InitialBoundary> SynchronizeAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> ReleaseAsync(CancellationToken token) { Releases++; Assert.False(token.IsCancellationRequested); return ValueTask.FromResult(release); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Websocket_readiness_failure_is_retryable_only_after_confirmed_unregistered_release(bool timeout)
    {
        var connection = new Connection(_ => { if (timeout) throw new TaskCanceledException("Handshake"); throw new WebSocketException(); });
        await Assert.ThrowsAsync<ConnectionNotReadyException>(() => ReadyConnection.AcquireAsync(() => connection, CancellationToken.None).AsTask());
        Assert.Equal(1, connection.Releases);
    }
    [Fact]
    public async Task Caller_cancellation_and_capability_failure_are_not_readiness_retries()
    {
        using var caller = new CancellationTokenSource();
        var connection = new Connection(token => { caller.Cancel(); throw new OperationCanceledException(token); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadyConnection.AcquireAsync(() => connection, caller.Token).AsTask());
        Assert.Equal(caller.Token,error.CancellationToken); Assert.Equal(1,connection.Releases);
        var unsupported = new Connection(_ => throw new NotSupportedException("capability"));
        await Assert.ThrowsAsync<NotSupportedException>(() => ReadyConnection.AcquireAsync(() => unsupported,CancellationToken.None).AsTask());
        Assert.Equal(1,unsupported.Releases);
    }
    [Fact]
    public async Task Unconfirmed_unregistered_release_cannot_admit_another_attempt()
    {
        var connection = new Connection(_ => throw new WebSocketException(),false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadyConnection.AcquireAsync(() => connection,CancellationToken.None).AsTask());
        Assert.Equal("UnregisteredReleaseUnconfirmed",error.Message); Assert.Equal(1,connection.Releases);
    }
    [Fact]
    public async Task Failed_readiness_self_release_remains_a_final_owned_cleanup_obligation()
    {
        var pending = new PendingConnections();
        var connection = new Connection(_ => throw new WebSocketException(),false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadyConnection.AcquireAsync(() => pending.Track(() => connection),CancellationToken.None).AsTask());
        var handedOff = pending.Track(() => new Connection(_ => throw new NotSupportedException())); Assert.True(pending.Handoff(handedOff));
        Assert.False(await pending.ReleaseAsync(CancellationToken.None)); Assert.Equal(2,connection.Releases);
        Assert.False(await pending.ReleaseAsync(CancellationToken.None)); Assert.Equal(0,handedOff.Releases);
    }
    [Fact]
    public async Task Final_cleanup_closes_connection_acquisition_before_any_new_factory_call()
    {
        var pending = new PendingConnections(); Assert.True(await pending.ReleaseAsync(CancellationToken.None));
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => pending.Track(() => { calls++; return new Connection(_ => throw new NotSupportedException()); }));
        Assert.Equal(0,calls);
    }
}
