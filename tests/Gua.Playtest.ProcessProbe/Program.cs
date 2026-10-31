using System.Reflection;
using Gua.Playtest.Runner.Preparation;
using Gua.Testing;
using Gua.Core;
using Gua.Runtime;

if (args is ["capture-pool-pressure"])
{
    using var host=new GuaRuntime(); host.BeginFrame("capture"); host.RegisterNode(new("start","button","Start",new(0,0,10,10))); host.EndFrame();
    using var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start(); int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    if(!host.StartInspectorBridge(port)) return 3;
    var condition=Gua.Playtest.Runner.Conditions.PreparedCondition.Create(System.Text.Json.Nodes.JsonNode.Parse("""{"kind":"assertion","read":{"target":{"source":"ui","selector":{"id":{"value":"start"}}},"region":"standard","field":"state.checked","valueType":{"type":"bool"}},"quantifier":"one","operator":"equals","expected":{"type":"bool","value":false}}""")!.AsObject(),new Gua.Playtest.Core.Assertions.AssertionOptions(100,1024));
    using var reached=new ManualResetEventSlim(); int measure=0, poolWorker=-1;
    var workers=new System.Collections.Concurrent.ConcurrentDictionary<int,Thread>();
    using var cancelEntered=new ManualResetEventSlim(); using var cancelResume=new ManualResetEventSlim(); int hold=0;
    void Verify() { if(Volatile.Read(ref measure)!=0) { workers.TryAdd(Environment.CurrentManagedThreadId,Thread.CurrentThread); Volatile.Write(ref poolWorker,Thread.CurrentThread.IsThreadPoolThread?1:0); reached.Set(); if(Interlocked.Exchange(ref hold,0)!=0) { cancelEntered.Set(); cancelResume.Wait(TimeSpan.FromSeconds(5)); } } }
    var type=typeof(Gua.Playtest.Cli.CliApplication).Assembly.GetType("Gua.Playtest.Cli.GuaObservationConnection",true)!;
    var connection=(IPreparationConnection)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
        [new Uri($"ws://127.0.0.1:{port}"),"build","Debug",new ProbeClock(),condition,null,100,100000,TimeSpan.FromSeconds(2),new Action(Verify),new Action<Gua.Playtest.GuaIntegration.BridgeReadCollection,HostIdentity>((_,_)=>{}),false,null],null)!;
    var identity=connection.IdentifyAsync(default).AsTask().GetAwaiter().GetResult();
    ThreadPool.GetMinThreads(out int minWorkers,out int minIo); ThreadPool.GetMaxThreads(out int maxWorkers,out int maxIo);
    using var occupied=new ManualResetEventSlim(); using var unblock=new ManualResetEventSlim(); using var ended=new ManualResetEventSlim();
    bool started=false,completedDuring=false; int available=-1; Task<Gua.Playtest.Runner.Execution.RunObservation>? capture=null;
    try
    {
        // This disposable process alone controls the pool. Observe actual entry
        // through the existing identity hook; do not infer it from TaskStatus.
        if(!ThreadPool.SetMinThreads(1,minIo)||!ThreadPool.SetMaxThreads(1,maxIo)) return 4;
        ThreadPool.QueueUserWorkItem(_=> { occupied.Set(); unblock.Wait(); ended.Set(); }); if(!occupied.Wait(TimeSpan.FromSeconds(5))) return 5;
        ThreadPool.GetAvailableThreads(out available,out _); Volatile.Write(ref measure,1);
        capture=((Gua.Playtest.Runner.Execution.IRunObservationFeed)connection).CaptureAsync(default).AsTask();
        started=reached.Wait(TimeSpan.FromMilliseconds(100)); completedDuring=capture.IsCompleted;
    }
    finally
    {
        unblock.Set(); ended.Wait(TimeSpan.FromSeconds(5)); ThreadPool.SetMaxThreads(maxWorkers,maxIo); ThreadPool.SetMinThreads(minWorkers,minIo);
    }
    var result=capture!.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    var feed=(Gua.Playtest.Runner.Execution.IRunObservationFeed)connection;
    for(int index=0;index<16;index++)
    {
        feed.CaptureAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        foreach(var worker in workers.Values) if(!worker.Join(TimeSpan.FromSeconds(1))) return 6;
    }
    int entries=workers.Count; bool cancelBefore=false,cancelAfter=false,closedFailure=false;
    using(var cancelled=new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { feed.CaptureAsync(cancelled.Token).AsTask().GetAwaiter().GetResult(); }
        catch(OperationCanceledException error) { cancelBefore=error.CancellationToken==cancelled.Token && workers.Count==entries; }
    }
    using(var cancelled=new CancellationTokenSource())
    {
        Volatile.Write(ref hold,1); var pending=feed.CaptureAsync(cancelled.Token).AsTask();
        try { if(!cancelEntered.Wait(TimeSpan.FromSeconds(2))) return 7; cancelled.Cancel(); }
        finally { cancelResume.Set(); }
        try { pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch(OperationCanceledException error) { cancelAfter=error.CancellationToken==cancelled.Token; }
    }
    bool workersEnded=workers.Values.All(worker=>worker.Join(TimeSpan.FromSeconds(1)));
    bool released=connection.ReleaseAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    try { feed.CaptureAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
    catch(ObjectDisposedException) { closedFailure=true; }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { started,available,poolWorker,completedDuring,releaseConfirmed=released,source=identity.SourceId,capturedTicks=result.CapturedAt.Ticks,serialCaptures=16,workersEnded,cancelBefore,cancelAfter,closedFailure }));
    return started && available==0 && poolWorker==0 && released && workersEnded && cancelBefore && cancelAfter && closedFailure ? 0 : 1;
}

if (args is ["release-pool-pressure"])
{
    // Only this disposable process changes its ThreadPool limits. Establish both
    // real SDK sockets first, then leave no shared worker for a queued release.
    using var host = new GuaRuntime();
    host.BeginFrame("release"); host.RegisterNode(new("start", "button", "Start", new(0,0,10,10))); host.EndFrame();
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
    listener.Start(); int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    if(!host.StartInspectorBridge(port)) return 3;
    var type=typeof(Gua.Playtest.Cli.CliApplication).Assembly.GetType("Gua.Playtest.Cli.GuaObservationConnection",true)!;
    var connection=(IPreparationConnection)Activator.CreateInstance(type, BindingFlags.Instance|BindingFlags.NonPublic,
        null, [new Uri($"ws://127.0.0.1:{port}"),"build","Debug",new ProbeClock(),null,null,100,100000,
            TimeSpan.FromMilliseconds(500),new Action(()=>{}),new Action<Gua.Playtest.GuaIntegration.BridgeReadCollection,HostIdentity>((_,_)=>{}),true,null],null)!;
    var identity=connection.IdentifyAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    using var entered=new ManualResetEventSlim(); using var unblock=new ManualResetEventSlim(); using var ended=new ManualResetEventSlim();
    ThreadPool.GetMinThreads(out int minWorkers,out int minIo); ThreadPool.GetMaxThreads(out int maxWorkers,out int maxIo);
    if(!ThreadPool.SetMinThreads(1,minIo) || !ThreadPool.SetMaxThreads(1,maxIo)) return 4;
    bool confirmed=false; double duration=0; int available=0;
    Task<bool>? release=null;
    try
    {
        ThreadPool.QueueUserWorkItem(_=>{entered.Set();unblock.Wait();ended.Set();});
        if(!entered.Wait(TimeSpan.FromSeconds(5))) return 5;
        ThreadPool.GetAvailableThreads(out available,out _);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        release=connection.ReleaseAsync(CancellationToken.None).AsTask();
        confirmed=release.Wait(TimeSpan.FromMilliseconds(500)) && release.GetAwaiter().GetResult(); duration=timer.Elapsed.TotalMilliseconds;
    }
    finally
    {
        unblock.Set(); ended.Wait(TimeSpan.FromSeconds(5));
        ThreadPool.SetMaxThreads(maxWorkers,maxIo); ThreadPool.SetMinThreads(minWorkers,minIo);
        release?.Wait(TimeSpan.FromSeconds(5));
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { confirmed,duration,available,source=identity.SourceId,
        socketsDisposed=type.GetField("closed",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(connection) }));
    return confirmed && available==0 ? 0 : 1;
}

if (args is [var clickMode, var clickPid] && clickMode is "ui-click-host" or "ui-click-host-private-event" or "ui-click-host-fail" or "ui-click-host-no-goal" or "ui-click-host-already" or "ui-click-host-delayed" or "ui-click-host-late-goal")
{
    File.WriteAllText(clickPid, Environment.ProcessId.ToString()); File.WriteAllText(clickPid + ".clicks", "0");
    using var host = new GuaRuntime();
    host.BeginFrame("title"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: clickMode == "ui-click-host-already")); host.EndFrame();
    if (!host.StartInspectorBridge(int.Parse(Environment.GetEnvironmentVariable("GUA_BRIDGE_PORT")!))) return 3;
    int count = 0;
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    long? goalAt = null;
    while (true)
    {
        if (goalAt is { } due && elapsed.ElapsedMilliseconds >= due)
        {
            host.BeginFrame("play"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: true)); host.EndFrame();
            File.WriteAllText(clickPid + ".goal", "published-after-completion"); goalAt = null;
        }
        if (host.TryConsumeAction(GuaActionType.Click, "start", out var click))
        {
            File.WriteAllText(clickPid + ".clicks", (++count).ToString());
            if (clickMode is "ui-click-host" or "ui-click-host-private-event")
            { host.BeginFrame("play"); host.RegisterNode(new("start", "button", "Start", new(0, 0, 10, 10), Checked: true)); host.EndFrame(); }
            host.EmitActionResult(clickMode == "ui-click-host-private-event" ? click with { Sensitive=true, Value="private-completion-marker" } : click,
                clickMode != "ui-click-host-fail", clickMode == "ui-click-host-fail" ? GuaActionError.InvalidArgument : GuaActionError.None);
            if (clickMode is "ui-click-host-delayed" or "ui-click-host-late-goal")
            { goalAt = elapsed.ElapsedMilliseconds + (clickMode == "ui-click-host-delayed" ? 300 : 1500); File.WriteAllText(clickPid + ".completion", "emitted-before-goal"); }
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

sealed class ProbeClock : Gua.Playtest.Core.IClock
{
    private readonly System.Diagnostics.Stopwatch timer=System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed=>timer.Elapsed;
    public ValueTask DelayAsync(TimeSpan duration,CancellationToken token)=>new(Task.Delay(duration,token));
}
