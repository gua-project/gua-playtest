using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Testing;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

[Collection("Preparation owner timing")]
public sealed class ReplayProfileTests
{
    [Theory]
    [InlineData("Player", "player")]
    [InlineData("Debug", "debug")]
    public async Task Launch_configuration_matches_actual_native_child_profile(string requested, string expected)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var root = Path.Combine(repository, "artifacts/tests/replay-profile", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(repository, "tests/Gua.Playtest.ProcessProbe/bin", configuration, "net10.0/Gua.Playtest.ProcessProbe.dll");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var launch = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        launch.Environment.Remove("GUA_OBSERVATION_PROFILE");
        foreach (var item in ReplayRunCommand.ChildEnvironment(port, requested)) launch.Environment[item.Key] = item.Value;
        launch.ArgumentList.Add(probe); launch.ArgumentList.Add("scripted-ui-click-host"); launch.ArgumentList.Add(Path.Combine(root, "pid"));
        using var child = Process.Start(launch)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // Read the actual host document without supplying an expected profile.
            // The assertion must detect an omitted Player setting, not just compare dictionaries.
            string? actual = null;
            while (actual is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.False(child.HasExited, "Native profile probe exited before connection");
                try
                {
                    using var context = new GuaWebSocketContext($"ws://127.0.0.1:{port}", TimeSpan.FromSeconds(1));
                    using var snapshot = JsonDocument.Parse(context.GetObserveSnapshotJson());
                    actual = snapshot.RootElement.GetProperty("document").GetProperty("profile").GetString();
                }
                catch (Exception error) when (error is not (Xunit.Sdk.XunitException or OperationCanceledException))
                { await Task.Delay(10, deadline.Token); }
            }
            Assert.Equal(expected, actual);
            if (requested == "Debug") Assert.DoesNotContain("GUA_OBSERVATION_PROFILE", ReplayRunCommand.ChildEnvironment(port, requested).Keys);
        }
        finally
        {
            if (!child.HasExited) child.Kill(true);
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(root, "stdout.txt"), await stdout.WaitAsync(TimeSpan.FromSeconds(5)));
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.txt"), await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void Undefined_profile_cannot_select_a_child_policy()
        => Assert.Throws<ArgumentException>(() => ReplayRunCommand.ChildEnvironment(12345, "Other"));
}
