using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Playtest.Runner.Dashboard;

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
        using var listener = new HttpListener(); listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var registration = cancellationToken.Register(listener.Stop);
        var snapshot = await index.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ready?.Invoke(new(origin));
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (Exception e) when (cancellationToken.IsCancellationRequested &&
                e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; }
            var request = context.Request; var response = context.Response;
            try
            {
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                // DNS rebinding, cross-origin refresh and arbitrary network clients cannot widen authority.
                if (request.Url?.Authority != $"127.0.0.1:{port}" || request.HttpMethod != "GET" ||
                    request.Headers["Origin"] is { } caller && caller != origin ||
                    request.Headers["Sec-Fetch-Site"] is "cross-site")
                { response.StatusCode = 403; continue; }
                var path = request.Url.AbsolutePath;
                byte[] body;
                if (path is "/api/snapshot" or "/api/refresh")
                {
                    if (path == "/api/refresh") snapshot = await index.ReadAsync(cancellationToken);
                    body = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json); response.ContentType = "application/json; charset=utf-8";
                }
                else if (path.StartsWith("/reports/", StringComparison.Ordinal))
                {
                    body = index.ReadReport(path); response.ContentType = "text/html; charset=utf-8";
                    // Reuse the Gua-produced standalone report, isolated from the dashboard origin.
                    response.Headers["Content-Security-Policy"] = "sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; frame-ancestors 'none'";
                }
                else if (path is "/" or "/dashboard.js" or "/dashboard.css")
                {
                    var file = path == "/" ? "dashboard.html" : path[1..];
                    using var asset = typeof(DashboardApplication).Assembly.GetManifestResourceStream("Gua.Playtest.Cli.DashboardAssets." + file)
                        ?? throw new InvalidDataException("DashboardAssetMissing");
                    using var buffer = new MemoryStream(); await asset.CopyToAsync(buffer, cancellationToken); body = buffer.ToArray();
                    response.ContentType = file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" :
                        file.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/html; charset=utf-8";
                    response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                }
                else { response.StatusCode = 404; continue; }
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or HttpListenerException)
            {
                try
                {
                    response.StatusCode = 409; response.ContentType = "text/plain; charset=utf-8";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Saved report unavailable; refresh the index."), cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or HttpListenerException or OperationCanceledException) { }
            }
            finally { response.Close(); }
        }
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
}
