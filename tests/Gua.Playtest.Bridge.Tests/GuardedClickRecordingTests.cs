using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Testing.Recording;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class GuardedClickRecordingTests
{
    private static string Start(GuaRuntime host)
    {
        host.BeginFrame("title"); host.RegisterNode(new("start", "button", "Start", new(0,0,10,10))); host.EndFrame();
        using var port = new TcpListener(IPAddress.Loopback,0); port.Start(); int number=((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Assert.True(host.StartInspectorBridge(number)); return host.InspectorBridgeUrl;
    }
    private static BridgeObservations Observe(string endpoint) => new(endpoint,GuaObservationProfile.Debug,TimeSpan.FromSeconds(2),100,65536);
    private static GuardedClickRecording Capture(GuardedUiClick click, bool refused=false) =>
        new(click,"start",_=>!refused, document=>document.Clone(),65536,1000,65536,CancellationToken.None);
    private static async Task Release(GuardedClickRecording capture)
    { using var share=new CancellationTokenSource(TimeSpan.FromSeconds(2)); Assert.True(await capture.ReleaseAsync(share.Token)); }

    [Theory]
    [InlineData("depth")] [InlineData("nodes")] [InlineData("chars")]
    public async Task Raw_native_tree_is_admitted_before_projection_or_recorder_buffers(string violation)
    {
        using var host=new GuaRuntime(); int inject=0, rewritten=0, projections=0;
        await using var proxy=new GuardedReplyProxy(Start(host),_=>false,(type,reply)=>
        {
            if(type!="get_ui_tree" || Interlocked.CompareExchange(ref inject,0,1)!=1) return reply;
            var root=JsonNode.Parse(reply)!;
            root["result"]!["credential"]=violation switch
            {
                "depth"=>JsonNode.Parse(new string('[',34)+"0"+new string(']',34)),
                "nodes"=>new JsonArray(Enumerable.Range(0,200).Select(_=>JsonValue.Create(0) as JsonNode).ToArray()),
                _=>JsonValue.Create(new string('x',1000))
            };
            rewritten++; return Encoding.UTF8.GetBytes(root.ToJsonString());
        });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick();
        var capture=new GuardedClickRecording(click,"start",_=>true,document=> { projections++; return document.Clone(); },
            65536,violation=="nodes" ? 100 : 1000,128,default);
        Volatile.Write(ref inject,1); capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        var completed=click.Poll(); capture.ObserveReceipt(completed); await Release(capture);
        Assert.Equal(1,rewritten); Assert.Equal(0,projections); Assert.True(completed.Succeeded);
        Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_failure_diagnostics_do_not_read_the_host_after_dispatch(bool cancel)
    {
        using var host=new GuaRuntime(); int dispatched=0, lateTrees=0;
        await using var proxy=new GuardedReplyProxy(Start(host),type=>
        {
            if(type=="guarded_click_node") Volatile.Write(ref dispatched,1);
            if(type=="get_ui_tree" && Volatile.Read(ref dispatched)!=0) Interlocked.Increment(ref lateTrees);
            return false;
        });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick(); using var caller=new CancellationTokenSource();
        var capture=new GuardedClickRecording(click,"start",_=>true,document=>document.Clone(),65536,1000,65536,caller.Token);
        var sent=capture.Start(()=>{},TimeSpan.FromSeconds(1),caller.Token); capture.ObserveReceipt(sent);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request));
        if(cancel) caller.Cancel();
        else { host.EmitActionResult(request,false,GuaActionError.InvalidArgument); var completed=click.Poll(); Assert.False(completed.Succeeded); capture.ObserveReceipt(completed); }
        await Release(capture);
        Assert.Equal(0,Volatile.Read(ref lateTrees)); Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Recording_event_read_does_not_wait_for_the_owner_while_its_native_poll_reply_is_held()
    {
        using var host=new GuaRuntime();
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy=new GuardedReplyProxy(Start(host),_=>false,afterNativeReply:type=>
        { if(type!="guarded_poll_action") return Task.CompletedTask; reached.TrySetResult(); return resume.Task; });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick(); var capture=Capture(click);
        var sent=capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        var polling=Task.Factory.StartNew(click.Poll,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Task<bool>? reading=null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var read=typeof(GuardedUiClick).GetMethod("TryRecordingEvent",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            reading=Task.Factory.StartNew(()=> (bool)read.Invoke(click,[sent.RequestId!.Value,null])!,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Assert.False(await reading.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            resume.TrySetResult(); capture.ObserveReceipt(await polling.WaitAsync(TimeSpan.FromSeconds(2))); await Release(capture);
            if(reading is not null) await reading.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(capture.Freeze().CaptureFailed); Assert.Equal(request.RequestId,Assert.Single(capture.Freeze().Recording!.Steps).RequestId);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Recorder_never_adds_native_polling_to_the_execution_owners_guarded_connection()
    {
        using var host=new GuaRuntime(); int polls=0;
        await using var proxy=new GuardedReplyProxy(Start(host),type=> { if(type=="guarded_poll_action") Interlocked.Increment(ref polls); return false; });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick(); var capture=Capture(click);
        capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        // Official Recorder synchronously tries its first event read before Start
        // returns. That read must be memory-only, even while no result exists.
        Assert.Equal(0,Volatile.Read(ref polls));
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        capture.ObserveReceipt(click.Poll()); await Release(capture);
        Assert.Equal(1,Volatile.Read(ref polls)); Assert.False(capture.Freeze().CaptureFailed);
        Assert.Equal(request.RequestId,Assert.Single(capture.Freeze().Recording!.Steps).RequestId);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Blocked_actual_send_is_capture_failed_and_release_unconfirmed_without_waiting_for_click_lock()
    {
        using var host=new GuaRuntime();
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int sends=0;
        await using var proxy=new GuardedReplyProxy(Start(host),_=>false,afterNativeReply:type=>
        {
            if(type!="guarded_click_node") return Task.CompletedTask;
            Interlocked.Increment(ref sends); reached.TrySetResult(); return resume.Task;
        });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick(); var capture=Capture(click);
        var starting=Task.Factory.StartNew(()=>capture.Start(()=>{},TimeSpan.FromSeconds(1),default),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
            using var share=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.False(await capture.ReleaseAsync(share.Token));
            // A lock-taking Freeze would block behind the deliberately held send.
            var frozen=await Task.Factory.StartNew(capture.Freeze,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(frozen.CaptureFailed); Assert.Null(frozen.Recording); Assert.Equal(1,sends);
        }
        finally { resume.TrySetResult(); }
        var late=await starting.WaitAsync(TimeSpan.FromSeconds(2)); capture.ObserveReceipt(late);
        Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.Throws<InvalidOperationException>(()=>capture.Start(()=>throw new Exception("No resend"),TimeSpan.FromSeconds(1),default));
        Assert.Equal(1,sends); Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Official_recorder_uses_one_actual_guarded_request_and_survives_borrowed_transport_cleanup()
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); var click=observations.CreateGuardedUiClick();
        var capture=Capture(click); int callbacks=0;
        var sent=capture.Start(()=>callbacks++,TimeSpan.FromSeconds(1),default); capture.ObserveReceipt(sent);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); Assert.Equal(sent.RequestId,request.RequestId);
        host.EmitActionResult(request,true); var completed=click.Poll(); capture.ObserveReceipt(completed);
        click.Dispose(); // The real event was retained before disconnection; no post-cleanup host I/O.
        await Release(capture);
        var result=capture.Freeze(); Assert.False(result.CaptureFailed);
        var step=Assert.Single(result.Recording!.Steps);
        Assert.Equal(request.RequestId,step.RequestId); Assert.Equal(sent.Revision,step.PreRevision);
        Assert.Equal("start",step.Target!.Id); Assert.Equal(GuaRecordedAction.click,step.Action);
        Assert.True(step.PostRevision>=step.PreRevision); Assert.False(step.Sensitive);
        Assert.Throws<InvalidOperationException>(()=>capture.Start(()=>callbacks++,TimeSpan.FromSeconds(1),default));
        Assert.Equal(1,callbacks); Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
        string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".recording.json");
        try { GuaRecordingFile.Save(file,result.Recording); Assert.Equal(step,Assert.Single(GuaRecordingFile.Load(file).Steps)); }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Capture_preflight_failure_preserves_exactly_one_real_operation(bool refuseText)
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); using var click=observations.CreateGuardedUiClick();
        var capture=refuseText ? Capture(click,true) : new GuardedClickRecording(click,"start",_=>true,
            _=>throw new InvalidDataException("ProjectionRefused"),65536,1000,65536,default);
        int callbacks=0; var sent=capture.Start(()=>callbacks++,TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        var completed=click.Poll(); capture.ObserveReceipt(completed); await Release(capture);
        Assert.True(completed.Succeeded); Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.Equal(sent.RequestId,completed.RequestId); Assert.Equal(1,callbacks); Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Cancelled_before_dispatch_is_not_executed_and_never_becomes_a_saved_empty_recording()
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); using var click=observations.CreateGuardedUiClick();
        var capture=Capture(click); using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>capture.Start(()=>throw new Exception("No dispatch"),TimeSpan.FromSeconds(1),cancelled.Token));
        await Release(capture); Assert.False(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lost_reply_stays_unconfirmed_with_one_send_and_no_synthetic_recording(bool completionReply)
    {
        using var host=new GuaRuntime(); int sends=0, consumed=0;
        await using var proxy=new GuardedReplyProxy(Start(host),type=>
        { if(type=="guarded_click_node") Interlocked.Increment(ref sends); return completionReply ? type=="guarded_poll_action" && Volatile.Read(ref consumed)!=0 : type=="guarded_click_node"; });
        using var observations=Observe(proxy.Endpoint); using var click=observations.CreateGuardedUiClick(); var capture=Capture(click);
        var receipt=capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        if(completionReply)
        {
            Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); Volatile.Write(ref consumed,1); host.EmitActionResult(request,true);
            receipt=click.Poll();
        }
        Assert.Equal(GuaRemoteDispatchState.Uncertain,receipt.State); capture.ObserveReceipt(receipt); await Release(capture);
        Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording); Assert.Equal(1,sends);
        Assert.Equal(receipt,click.Click("start",()=>throw new Exception("No resend"),default));
    }

    [Fact]
    public async Task Private_query_text_is_refused_before_recorder_without_losing_the_original_click()
    {
        using var host=new GuaRuntime(); string endpoint=Start(host);
        host.BeginFrame("title"); host.RegisterNode(new("start","button","private-label",new(0,0,10,10))); host.EndFrame();
        using var observations=Observe(endpoint); using var click=observations.CreateGuardedUiClick();
        var capture=new GuardedClickRecording(click,"start",text=>!text.Contains("private-label",StringComparison.Ordinal),document=>document.Clone(),65536,1000,65536,default);
        capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request)); host.EmitActionResult(request,true);
        var completed=click.Poll(); capture.ObserveReceipt(completed); await Release(capture);
        Assert.True(completed.Succeeded); Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Cancellation_at_actual_guard_boundary_does_not_fall_back_to_another_send()
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); using var click=observations.CreateGuardedUiClick();
        var capture=Capture(click); int calls=0;
        var receipt=capture.Start(()=> { calls++; throw new OperationCanceledException(); },TimeSpan.FromSeconds(1),default);
        capture.ObserveReceipt(receipt); await Release(capture);
        Assert.False(receipt.DispatchBegan); Assert.Equal(1,calls); Assert.Null(capture.Freeze().Recording); Assert.False(capture.Freeze().CaptureFailed);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Private_native_completion_is_refused_before_official_recording_buffers()
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); using var click=observations.CreateGuardedUiClick();
        var capture=new GuardedClickRecording(click,"start",text=>!text.Contains("private-marker",StringComparison.Ordinal),document=>document.Clone(),65536,1000,65536,default);
        capture.Start(()=>{},TimeSpan.FromSeconds(1),default);
        Assert.True(host.TryConsumeAction(GuaActionType.Click,"start",out var request));
        host.EmitActionResult(request with { Sensitive=true, Value="private-marker" },true);
        var completed=click.Poll(); capture.ObserveReceipt(completed); await Release(capture);
        Assert.True(completed.Succeeded); Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }

    [Fact]
    public async Task Stale_revision_refusal_does_not_create_a_successful_recording()
    {
        using var host=new GuaRuntime(); using var observations=Observe(Start(host)); using var click=observations.CreateGuardedUiClick(); var capture=Capture(click);
        var receipt=capture.Start(()=> { host.BeginFrame("changed"); host.RegisterNode(new("start","button","Changed",new(0,0,10,10))); host.EndFrame(); },TimeSpan.FromSeconds(1),default);
        capture.ObserveReceipt(receipt); await Release(capture);
        Assert.Equal(GuaRemoteDispatchState.Rejected,receipt.State); Assert.True(receipt.DispatchBegan);
        Assert.True(capture.Freeze().CaptureFailed); Assert.Null(capture.Freeze().Recording);
        Assert.False(host.TryConsumeAction(GuaActionType.Click,"start",out _));
    }
}
