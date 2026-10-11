using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Playtest.Runner.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gua.Playtest.Cli;

/// <summary>CLI #15 calls this entry after resolving explicit registry, roots and limits.
/// No dispatch wiring, game, Planner, Replay or persistence write authority is needed.</summary>
public static class DashboardApplication
{
    public static async Task RunAsync(DashboardIndex index, int port, Action<Uri>? ready = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        cancellationToken.ThrowIfCancellationRequested();
        var origin = $"http://127.0.0.1:{port}";
        var snapshot = await index.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // No ambient appsettings, URLs, certificates or logging providers can expand authority.
        var builder = WebApplication.CreateEmptyBuilder(new() { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestLineSize = 2048;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        using var indexGate = new SemaphoreSlim(1);
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
            var token = canceled.Token; var entered = false;
            var request = context.Request; var response = context.Response;
            try
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                if (request.Host.Value != $"127.0.0.1:{port}" || request.Method != "GET" ||
                    request.Headers.TryGetValue("Origin", out var caller) && caller.ToString() != origin ||
                    request.Headers["Sec-Fetch-Site"] == "cross-site")
                { response.StatusCode = 403; return; }
                await indexGate.WaitAsync(token); entered = true;
                var path = request.Path.Value ?? "";
                byte[] body;
                if (path is "/api/snapshot" or "/api/refresh")
                {
                    if (path == "/api/refresh") snapshot = await index.ReadAsync(token);
                    body = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json); response.ContentType = "application/json; charset=utf-8";
                }
                else if (path.StartsWith("/reports/", StringComparison.Ordinal))
                {
                    body = index.ReadReport(path); response.ContentType = "text/html; charset=utf-8";
                    response.Headers.ContentSecurityPolicy = "sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; frame-ancestors 'none'";
                }
                else if (path is "/" or "/dashboard.js" or "/dashboard.css")
                {
                    var file = path == "/" ? "dashboard.html" : path[1..];
                    using var asset = typeof(DashboardApplication).Assembly.GetManifestResourceStream("Gua.Playtest.Cli.DashboardAssets." + file)
                        ?? throw new InvalidDataException("DashboardAssetMissing");
                    using var buffer = new MemoryStream(); await asset.CopyToAsync(buffer, token); body = buffer.ToArray();
                    response.ContentType = file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" :
                        file.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/html; charset=utf-8";
                    response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                }
                else { response.StatusCode = 404; return; }
                response.ContentLength = body.Length;
                await response.Body.WriteAsync(body, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { context.Abort(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (response.HasStarted) { context.Abort(); return; }
                response.StatusCode = 409; response.ContentType = "text/plain; charset=utf-8";
                try { await response.WriteAsync("Saved report unavailable; refresh the index.", token); }
                catch (Exception failure) when (failure is IOException or OperationCanceledException) { context.Abort(); }
            }
            finally { if (entered) indexGate.Release(); }
        });
        try
        {
            await app.StartAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ready?.Invoke(new(origin));
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
}
