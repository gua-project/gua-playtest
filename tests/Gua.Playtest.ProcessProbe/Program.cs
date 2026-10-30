using System.Reflection;
using Gua.Playtest.Runner.Preparation;
using Gua.Testing;
using Gua.Core;
using Gua.Runtime;

if (args is [var clickMode, var clickPid] && clickMode is "ui-click-host" or "ui-click-host-fail" or "ui-click-host-no-goal" or "ui-click-host-already")
{
    File.WriteAllText(clickPid, Environment.ProcessId.ToString()); File.WriteAllText(clickPid + ".clicks", "0");
    using var host = new GuaRuntime();
    host.BeginFrame("title"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: clickMode == "ui-click-host-already")); host.EndFrame();
    if (!host.StartInspectorBridge(int.Parse(Environment.GetEnvironmentVariable("GUA_BRIDGE_PORT")!))) return 3;
    int count = 0;
    while (true)
    {
        if (host.TryConsumeAction(GuaActionType.Click, "start", out var click))
        {
            File.WriteAllText(clickPid + ".clicks", (++count).ToString());
            if (clickMode == "ui-click-host")
            { host.BeginFrame("play"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: true)); host.EndFrame(); }
            host.EmitActionResult(click, clickMode != "ui-click-host-fail", clickMode == "ui-click-host-fail" ? GuaActionError.InvalidArgument : GuaActionError.None);
        }
        await Task.Delay(5);
    }
}

if (args is [var hostMode, var pidFile] && hostMode is "observation-host" or "observation-host-delayed" or "observation-host-capacity" or "observation-host-ambiguous")
{
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());
    using var runtime = new GuaRuntime();
    runtime.BeginFrame("native-fixture");
    runtime.RegisterNode(new("ready", "button", "Ready", new(0, 0, 10, 10), Checked: false));
    if (hostMode == "observation-host-ambiguous") runtime.RegisterNode(new("other", "button", "Other", new(0, 20, 10, 10), Checked: false));
    runtime.EndFrame();
    using var owner = hostMode == "observation-host-ambiguous" ? runtime.CreateObserveOwner(GuaObserveSource.Ui, "ready") : null;
    using var field = owner?.Observe("flag", () => GuaValue.Bool(false));
    if (hostMode == "observation-host-ambiguous")
    {
        runtime.BeginFrame("native-fixture");
        runtime.RegisterNode(new("ready", "button", "Ready", new(0, 0, 10, 10), Checked: false));
        runtime.RegisterNode(new("other", "button", "Other", new(0, 20, 10, 10), Checked: false));
        runtime.EndFrame();
    }
    if (!runtime.StartInspectorBridge(int.Parse(Environment.GetEnvironmentVariable("GUA_BRIDGE_PORT")!))) return 3;
    if (hostMode is "observation-host-delayed" or "observation-host-capacity")
    {
        await Task.Delay(hostMode == "observation-host-capacity" ? 8000 : 700);
        runtime.BeginFrame("native-fixture");
        runtime.RegisterNode(new("ready", "button", "Ready", new(0, 0, 10, 10), Checked: true));
        runtime.EndFrame();
    }
    await Task.Delay(Timeout.Infinite);
    return 0;
}

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
