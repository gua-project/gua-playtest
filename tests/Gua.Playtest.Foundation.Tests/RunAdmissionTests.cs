using System.Security.Cryptography;
using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class RunAdmissionTests
{
    static readonly RunIdentity Identity = new("fixture", "v1", new string('a', 64), "build", new string('b', 64));
    [Fact]
    public void Absent_admission_does_not_add_a_new_cancellation_checkpoint_to_normal_CLI()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        RunAdmission.Require(null, "run", Identity, new string('c', 64), stop.Token);
    }
    [Theory]
    [InlineData("deny", "RunAdmissionRejected")] [InlineData("throw", "RunAdmissionFailed")]
    [InlineData("unrelated-cancel", "RunAdmissionFailed")]
    public void Refusal_and_callback_fault_do_not_expose_exception_contents(string mode, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => RunAdmission.Require((_, _) => mode switch
        { "deny" => false, "unrelated-cancel" => throw new OperationCanceledException("private-fixture"),
            _ => throw new IOException("private-fixture") }, "run", Identity, new string('c', 64), default));
        Assert.Equal(expected, error.Message); Assert.Null(error.InnerException);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Cancellation_before_or_inside_callback_prevents_acceptance(bool inside)
    {
        using var stop = new CancellationTokenSource(); int calls = 0; if (!inside) stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RunAdmission.Require((_, _) =>
        { calls++; stop.Cancel(); return true; }, "run", Identity, new string('c', 64), stop.Token));
        Assert.Equal(inside ? 1 : 0, calls);
    }
    [Theory] [InlineData("report")] [InlineData("--help")] [InlineData("dashboard")]
    public async Task Admission_entrypoint_cannot_execute_an_offline_or_server_command(string command)
    {
        int calls = 0; using var output = new StringWriter();
        Assert.Equal(2, await CliApplication.ExecuteWithRunAdmissionAsync([command, "--json"], output, TextWriter.Null,
            (_, _) => { calls++; return true; }));
        Assert.Contains("RuntimeCommandRequired", output.ToString()); Assert.Equal(0, calls);
    }
}

// Reused by the existing real native fixture success rows for all four runtime lanes.
internal static class RunStartAssertions
{
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, CancellationToken token)
    {
        string Value(string option) => args[Array.IndexOf(args, option) + 1];
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Value("--environment"), token)));
        string pid = args[Enumerable.Range(0, args.Length).Last(i => args[i] == "--argument") + 1];
        RunStartIdentity? captured = null; int calls = 0; bool pidAtStart = false, metadataAtStart = false;
        var exit = await CliApplication.ExecuteWithRunAdmissionAsync(args, output, TextWriter.Null, (start, _) =>
        {
            calls++; captured = start;
            pidAtStart = File.Exists(pid); // ProcessProbe publishes its pid only after launch.
            metadataAtStart = File.Exists(Path.Combine(Value("--output-root"), start.RunId, "run.json"));
            return true;
        }, token);
        Assert.Equal(1, calls); Assert.NotNull(captured);
        Assert.Equal(sourceHash, captured.SourceEnvironmentSha256); Assert.False(pidAtStart); Assert.False(metadataAtStart);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Value("--output-root"), captured.RunId, "run.json"), token));
        var identity = metadata.RootElement.GetProperty("identity");
        Assert.Equal(captured.Identity.ScenarioId, identity.GetProperty("scenarioId").GetString());
        Assert.Equal(captured.Identity.DefinitionVersion, identity.GetProperty("definitionVersion").GetString());
        Assert.Equal(captured.Identity.ScenarioSha256, identity.GetProperty("scenarioSha256").GetString());
        Assert.Equal(captured.Identity.BuildId, identity.GetProperty("buildId").GetString());
        Assert.Equal(captured.Identity.EnvironmentSha256, identity.GetProperty("environmentSha256").GetString());
        return exit;
    }
}
