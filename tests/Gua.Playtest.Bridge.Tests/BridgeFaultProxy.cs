using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Gua.Playtest.Bridge.Tests;

/// <summary>Test-only fault seam. Forwards each request once to the actual packaged native
/// bridge. The callback runs after the real response and before writing it to the client.
/// A committed fixture transaction can therefore lose only its reply. No Planner sees it.</summary>
internal sealed class BridgeFaultProxy : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
    private readonly Task server;
    public string Endpoint { get; }
    public BridgeFaultProxy(string upstream, Func<JsonElement, JsonElement, bool> dropReply,
        Func<JsonElement, JsonElement, byte[]?>? rewriteReply = null)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Endpoint = $"ws://127.0.0.1:{number}/";
        listener.Prefixes.Add($"http://127.0.0.1:{number}/"); listener.Start();
        server = Task.Run(async () =>
        {
            try
            {
                var request = await listener.GetContextAsync().WaitAsync(shutdown.Token);
                using var downstream = (await request.AcceptWebSocketAsync(null)).WebSocket;
                using var native = new ClientWebSocket(); await native.ConnectAsync(new Uri(upstream), shutdown.Token);
                while (!shutdown.IsCancellationRequested)
                {
                    byte[] command = await Receive(downstream, shutdown.Token);
                    using var commandDoc = JsonDocument.Parse(command);
                    await native.SendAsync(command.AsMemory(), WebSocketMessageType.Text, true, shutdown.Token);
                    byte[] reply;
                    JsonDocument response;
                    do
                    {
                        reply = await Receive(native, shutdown.Token); response = JsonDocument.Parse(reply);
                        if (response.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == commandDoc.RootElement.GetProperty("id").GetInt32()) break;
                        response.Dispose();
                    } while (true);
                    using (response)
                    {
                        if (dropReply(commandDoc.RootElement, response.RootElement))
                        {
                            // Close after host commit. No fabricated Gua response or success.
                            downstream.Abort(); return;
                        }
                        // Interoperability faults modify only the real host's response; they never
                        // fabricate host execution or a successful action result.
                        byte[] output = rewriteReply?.Invoke(commandDoc.RootElement, response.RootElement) ?? reply;
                        await downstream.SendAsync(output.AsMemory(), WebSocketMessageType.Text, true, shutdown.Token);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or HttpListenerException or ObjectDisposedException) { }
        });
    }
    private static async Task<byte[]> Receive(WebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (frame.MessageType != WebSocketMessageType.Text) throw new WebSocketException();
            data.Write(buffer, 0, frame.Count);
            if (data.Length > 1000000) throw new InvalidDataException("Test frame limit.");
            if (frame.EndOfMessage) return data.ToArray();
        }
    }
    public async ValueTask DisposeAsync()
    { shutdown.Cancel(); listener.Close(); await server; shutdown.Dispose(); }
}
