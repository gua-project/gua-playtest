using System.Globalization;
using Gua.Playtest.Runner.Dashboard;

namespace Gua.Playtest.Cli;

/// <summary>Explicit read-only loopback entry; never composes a game or Planner.</summary>
internal static class DashboardCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics,
        bool json, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (var position = 1; position < args.Length; position++)
            {
                var key = args[position];
                if (key is not ("--registry" or "--artifact-root" or "--allow-root" or "--port") ||
                    ++position >= args.Length || args[position].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("ExplicitDashboardArgumentsRequired");
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                if (list.Count != 0 && key is not ("--artifact-root" or "--allow-root"))
                    throw new ArgumentException("ExplicitDashboardArgumentsRequired");
                list.Add(args[position]);
            }
            string Required(string key) => values.TryGetValue(key, out var list) && list.Count == 1
                ? list[0] : throw new ArgumentException("ExplicitDashboardArgumentsRequired");
            string[] Roots(string key) => values.TryGetValue(key, out var list) && list.Count > 0
                ? list.ToArray() : throw new ArgumentException("ExplicitDashboardArgumentsRequired");
            if (!int.TryParse(Required("--port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1024 or > 65535) throw new ArgumentException("ExplicitDashboardArgumentsRequired");
            var index = new DashboardIndex(Required("--registry"), Roots("--artifact-root"), Roots("--allow-root"));
            await DashboardApplication.RunAsync(index, port, uri => diagnostics.WriteLine(uri.AbsoluteUri), token);
            token.ThrowIfCancellationRequested();
            return await CommandOutput.WriteAsync(output, diagnostics, json, "CommandSucceeded", "DashboardStopped", 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Interrupted", "CommandInterrupted", 4); }
        catch (Exception) when (token.IsCancellationRequested)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Interrupted", "DashboardShutdownIncomplete", 4); }
        catch (ArgumentException)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", "ExplicitDashboardArgumentsRequired", 2); }
        catch (Exception)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Unavailable", "DashboardUnavailable", 10); }
    }
}
