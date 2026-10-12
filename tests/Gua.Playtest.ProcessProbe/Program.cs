using System.Reflection;
using Gua.Playtest.Runner.Preparation;
using Gua.Testing;

if (args is ["trace", var root])
{
    using var trace = new GuaTraceSession(new GuaTraceOptions { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always, Profile = "player" });
    var step = trace.BeginStep(GuaTraceStepKind.Mark, "saved offline fixture");
    trace.EndStep(step, GuaTraceOutcome.Passed);
    if (!await trace.CompleteAsync(GuaTraceOutcome.Passed)) return 1;
    Console.WriteLine(trace.ArtifactPath);
    return 0;
}
if (args is ["noisy-child"])
{
    for (var i = 0; i < 8192; i++)
    {
        Console.WriteLine(new string('x', 1024));
        Console.Error.WriteLine(new string('y', 1024));
    }
    return 0;
}
if (args is ["noisy-parent"])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var process = await new SystemProcessLauncher().LaunchAsync(new LaunchCommand(
        Environment.ProcessPath!, Directory.GetCurrentDirectory(), [Assembly.GetExecutingAssembly().Location, "noisy-child"]), deadline.Token);
    await process.WaitForExitAsync(deadline.Token);
    if (!await process.ShutdownAsync(deadline.Token)) return 1;
    Console.WriteLine("{\"status\":\"CommandSucceeded\",\"code\":\"NoisyChildIsolated\"}");
    return 0;
}
return 2;
