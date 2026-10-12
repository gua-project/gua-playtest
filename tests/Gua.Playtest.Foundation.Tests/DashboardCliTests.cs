using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Gua.Playtest.Cli;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class DashboardCliTests
{
    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Gua.Playtest.slnx"))) return directory.FullName;
            throw new InvalidOperationException("RepositoryUnavailable");
        }
    }
    private sealed class ReadyWriter : StringWriter
    {
        internal TaskCompletionSource<Uri> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.IsLoopback)
                Ready.TrySetResult(uri);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicEntryServesOnlyExplicitRootsAndKeepsProgressOffStdout(bool json)
    {
        var root = Directory.CreateDirectory(Path.Combine(Repository, "TestResults", "dashboard-cli-" + Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter(); using var error = new ReadyWriter();
        Task<int>? command = null;
        try
        {
            foreach (var name in new[] { "registry.json", "scenario.json" })
                File.Copy(Path.Combine(Repository, "fixtures", "contracts", name), Path.Combine(root.FullName, name));
            var runs = Directory.CreateDirectory(Path.Combine(root.FullName, "explicit-runs"));
            var ignored = Directory.CreateDirectory(Path.Combine(root.FullName, "unlisted-runs", "forged"));
            await File.WriteAllTextAsync(Path.Combine(ignored.FullName, "run.json"), "do-not-discover-this-run");
            var before = Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var args = new List<string> { "dashboard", "--registry", Path.Combine(root.FullName, "registry.json"),
                "--artifact-root", runs.FullName, "--allow-root", root.FullName, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (json) args.Add("--json");
            command = CliApplication.ExecuteAsync(args.ToArray(), output, error, cancellation.Token);
            var ready = await error.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(string.Empty, output.ToString());
            using var client = new HttpClient { BaseAddress = ready, Timeout = TimeSpan.FromSeconds(10) };
            foreach (var path in new[] { "/", "/dashboard.js", "/dashboard.css" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
            }
            foreach (var path in new[] { "/api/snapshot", "/api/refresh" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Ready", document.RootElement.GetProperty("registryState").GetString());
                Assert.Equal(1, document.RootElement.GetProperty("scenarios").GetArrayLength());
                Assert.Equal(0, document.RootElement.GetProperty("runs").GetArrayLength());
            }
            Assert.Equal(string.Empty, output.ToString());
            cancellation.Cancel(); Assert.Equal(4, await command.WaitAsync(TimeSpan.FromSeconds(10)));
            if (json)
            {
                using var final = JsonDocument.Parse(output.ToString());
                Assert.Equal("CommandInterrupted", final.RootElement.GetProperty("code").GetString());
            }
            else Assert.Equal("Interrupted: CommandInterrupted" + output.NewLine, output.ToString());
            Assert.Contains(ready.AbsoluteUri, error.ToString());
            Assert.DoesNotContain("ScenarioPassed", output.ToString() + error);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            cancellation.Cancel();
            if (command is not null) await command.WaitAsync(TimeSpan.FromSeconds(10));
            root.Delete(true);
        }
    }
    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--set", "port=9000")]
    [InlineData("--registry", "unselected.json")]
    public async Task PublicEntryRequiresAllExplicitArgumentsWithoutDefaults(string key, string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteAsync(["dashboard", "--json", key, value], output, error));
        using var final = JsonDocument.Parse(output.ToString());
        Assert.Equal("ExplicitDashboardArgumentsRequired", final.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("http://", error.ToString());
    }
}
