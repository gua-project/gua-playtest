using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class GuardedUiClickTests
{
    [Fact]
    public void Original_nonzero_native_completion_error_is_preserved()
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations=Observe(Start(runtime)); using var click=observations.CreateGuardedUiClick();
        var sent=click.Click("start",()=>{},CancellationToken.None);
        Assert.Equal(GuaRemoteDispatchState.Enqueued,sent.State);
        Assert.True(runtime.TryConsumeAction(GuaActionType.Click,"start",out var request));
        runtime.EmitActionResult(request,false,GuaActionError.InvalidArgument);
        var completion=click.Poll(); Assert.False(completion.Succeeded);
        Assert.Equal((int)GuaActionError.InvalidArgument,completion.GuaErrorCode); Assert.Equal(sent.RequestId,completion.RequestId);
    }
    [Fact]
    public void Already_cancelled_token_never_reaches_a_dispatch_hook()
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations = Observe(Start(runtime)); using var click = observations.CreateGuardedUiClick();
        using var caller = new CancellationTokenSource(); caller.Cancel(); int calls=0;
        Assert.ThrowsAny<OperationCanceledException>(() => click.Click("start", () => calls++,caller.Token));
        Assert.Equal(0,calls); Assert.False(runtime.TryConsumeAction(GuaActionType.Click,"start",out _));
    }
    [Fact]
    public void Requested_profile_cannot_promote_the_actual_host_profile()
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations = new BridgeObservations(Start(runtime),GuaObservationProfile.Player,TimeSpan.FromSeconds(2),100,65536);
        var error=Assert.Throws<InvalidOperationException>(()=>observations.CreateGuardedUiClick());
        Assert.Equal("Bridge profile or schema mismatch.",error.Message);
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click,"start",out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_capability_or_foreign_owned_source_refuses_before_dispatch(bool foreign)
    {
        using var runtime = new GuaRuntime(); Frame(runtime); string endpoint=Start(runtime);
        using var initial=Observe(endpoint); string source=initial.Identify().SourceId;
        int modified=0, snapshots=0, sends=0;
        await using var proxy=new GuardedReplyProxy(endpoint,type=> { if(type.StartsWith("guarded_",StringComparison.Ordinal)) sends++; return false; },
            (type,reply)=>
            {
                string text=Encoding.UTF8.GetString(reply);
                if (!foreign && type=="get_version")
                { var changed=text.Replace("guarded_dispatch_v1","unsupported_guard_v1",StringComparison.Ordinal); if(changed!=text)modified++; text=changed; }
                if (foreign && type=="get_observe_snapshot" && ++snapshots>=3)
                { var changed=text.Replace(source,"foreign-source",StringComparison.Ordinal); if(changed!=text)modified++; text=changed; }
                return Encoding.UTF8.GetBytes(text);
            });
        using var observations=Observe(proxy.Endpoint);
        Assert.ThrowsAny<Exception>(()=> observations.CreateGuardedUiClick());
        Assert.True(modified>0); Assert.Equal(0,sends); Assert.False(runtime.TryConsumeAction(GuaActionType.Click,"start",out _));
    }
    private static void Frame(GuaRuntime runtime, string label = "Start")
    { runtime.BeginFrame("title"); runtime.RegisterNode(new("start", "button", label, new(0, 0, 10, 10))); runtime.EndFrame(); }
    private static string Start(GuaRuntime runtime)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.True(runtime.StartInspectorBridge(port)); return runtime.InspectorBridgeUrl;
    }
    private static BridgeObservations Observe(string endpoint) => new(endpoint, GuaObservationProfile.Debug, TimeSpan.FromSeconds(2), 100, 65536);

    [Fact]
    public void Published_guarded_UI_click_has_one_owned_correlated_completion_and_no_resend()
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations = Observe(Start(runtime)); using var click = observations.CreateGuardedUiClick();
        int callbacks = 0;
        var sent = click.Click("start", () => callbacks++, CancellationToken.None);
        Assert.Equal(GuaRemoteDispatchState.Enqueued, sent.State); Assert.True(sent.DispatchBegan);
        Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "start", out var request)); runtime.EmitActionResult(request, true);
        var complete = click.Poll(); Assert.Equal(GuaRemoteDispatchState.Completed, complete.State); Assert.True(complete.Succeeded);
        Assert.Equal(sent.RequestId, complete.RequestId); Assert.Equal(observations.Identify().SourceId, complete.SourceId);
        Assert.Equal(complete, click.Click("start", () => callbacks++, CancellationToken.None));
        Assert.Equal(1, callbacks); Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "start", out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cancellation_or_closed_owner_before_send_does_not_dispatch(bool cancellation)
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations = Observe(Start(runtime)); using var click = observations.CreateGuardedUiClick();
        var receipt = click.Click("start", () =>
        { if (cancellation) throw new OperationCanceledException(); throw new InvalidOperationException("Closed owner"); }, CancellationToken.None);
        Assert.Equal(GuaRemoteDispatchState.Rejected, receipt.State); Assert.False(receipt.DispatchBegan);
        Assert.Equal("pre-dispatch-rejected",receipt.DispatchError);
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "start", out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Atomic_host_rejects_revision_or_epoch_changed_after_client_preflight(bool reset)
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        using var observations = Observe(Start(runtime)); using var click = observations.CreateGuardedUiClick();
        var result = click.Click("start", () => { if (reset) { using var control = new GuaWebSocketContext(runtime.InspectorBridgeUrl); control.Reset(); } Frame(runtime, "Changed"); }, CancellationToken.None);
        Assert.Equal(GuaRemoteDispatchState.Rejected, result.State); Assert.True(result.DispatchBegan);
        Assert.False(runtime.TryConsumeAction(GuaActionType.Click, "start", out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lost_send_or_completion_reply_is_terminal_uncertainty_without_resend(bool completionReply)
    {
        using var runtime = new GuaRuntime(); Frame(runtime);
        int sends = 0;
        await using var proxy = new GuardedReplyProxy(Start(runtime), type =>
        {
            if (type == "guarded_click_node") Interlocked.Increment(ref sends);
            return type == (completionReply ? "guarded_poll_action" : "guarded_click_node");
        });
        using var observations = Observe(proxy.Endpoint); using var click = observations.CreateGuardedUiClick();
        var sent = click.Click("start", () => { }, CancellationToken.None);
        if (completionReply)
        {
            Assert.Equal(GuaRemoteDispatchState.Enqueued, sent.State);
            Assert.True(runtime.TryConsumeAction(GuaActionType.Click, "start", out var request)); runtime.EmitActionResult(request, true);
            sent = click.Poll();
        }
        Assert.Equal(GuaRemoteDispatchState.Uncertain, sent.State); Assert.True(sent.DispatchBegan);
        Assert.Equal(sent, click.Click("start", () => throw new Exception("Must not resend"), CancellationToken.None));
        Assert.Equal(sent, click.Poll()); Assert.Equal(1, sends);
    }
}

/// <summary>Two real connections, no synthetic successful response. Drops only after native receipt.</summary>
internal sealed class GuardedReplyProxy : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
    private readonly Task serving;
    public string Endpoint { get; }
    public GuardedReplyProxy(string upstream, Func<string, bool> drop, Func<string,byte[],byte[]>? rewrite = null)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Endpoint = $"ws://127.0.0.1:{number}/"; listener.Prefixes.Add($"http://127.0.0.1:{number}/"); listener.Start();
        serving = Task.Run(async () =>
        {
            var workers = new List<Task>();
            try
            {
                while (workers.Count < 4)
                {
                    var request = await listener.GetContextAsync().WaitAsync(stop.Token);
                    workers.Add(Forward(request));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            finally { await Task.WhenAll(workers); }
        });
        async Task Forward(HttpListenerContext request)
        {
            try
            {
                using var downstream = (await request.AcceptWebSocketAsync(null)).WebSocket;
                using var native = new ClientWebSocket(); await native.ConnectAsync(new Uri(upstream), stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    var command = await Read(downstream, stop.Token); using var input = JsonDocument.Parse(command);
                    await native.SendAsync(command, WebSocketMessageType.Text, true, stop.Token);
                    byte[] reply;
                    while (true)
                    {
                        reply = await Read(native, stop.Token); using var result = JsonDocument.Parse(reply);
                        if (result.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == input.RootElement.GetProperty("id").GetInt32()) break;
                    }
                    string type=input.RootElement.GetProperty("type").GetString()!;
                    if (drop(type)) { downstream.Abort(); return; }
                    if (rewrite is not null) reply=rewrite(type,reply);
                    await downstream.SendAsync(reply, WebSocketMessageType.Text, true, stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or HttpListenerException) { }
        }
    }
    private static async Task<byte[]> Read(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, token);
            if (part.MessageType != WebSocketMessageType.Text) throw new WebSocketException();
            message.Write(buffer, 0, part.Count); if (message.Length > 1048576) throw new InvalidDataException();
            if (part.EndOfMessage) return message.ToArray();
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await serving; }
        finally { try { listener.Close(); } catch(ObjectDisposedException) { } finally { stop.Dispose(); } }
    }
}
