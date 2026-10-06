using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Gua.Testing;

namespace Gua.Playtest.Foundation.Tests;

// Failure-only diagnostic probe of the pinned package, on a dedicated thread so a blocked worker
// pool cannot suppress its samples. Never changes Trace limits, assertions, CPU count or pool settings.
internal sealed class TraceFlushTelemetry : IDisposable
{
    private readonly GuaTraceSession trace;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly ManualResetEventSlim stop = new();
    private readonly Thread thread;
    private readonly List<object> samples = [];
    private static readonly FieldInfo Writer = typeof(GuaTraceSession).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Queue = typeof(GuaTraceSession).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal TraceFlushTelemetry(GuaTraceSession trace)
    {
        this.trace = trace;
        thread = new Thread(() =>
        {
            do { Sample(); } while (!stop.Wait(TimeSpan.FromMilliseconds(100)) && elapsed.Elapsed < TimeSpan.FromSeconds(10));
        }) { IsBackground = true, Name = "Trace flush acceptance telemetry" };
        thread.Start();
    }
    private void Sample()
    {
        if (Writer?.GetValue(trace) is not Task writer || Queue?.GetValue(trace) is not object queue) return;
        int? count = null;
        try { count = (int?)queue.GetType().GetProperty("Count")?.GetValue(queue); }
        catch (TargetInvocationException) { } // The package may dispose its queue during shutdown.
        lock (samples)
            if (samples.Count < 100) samples.Add(new { elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                workers = ThreadPool.ThreadCount, pending = ThreadPool.PendingWorkItemCount,
                completed = ThreadPool.CompletedWorkItemCount, writer = writer.Status.ToString(), queueCount = count });
    }
    internal void ReportFailure()
    {
        Sample();
        lock (samples) Console.WriteLine("TraceFlushSchedulerEvidence " + JsonSerializer.Serialize(new
            { processorCount = Environment.ProcessorCount, samples }));
    }
    public void Dispose() { stop.Set(); thread.Join(TimeSpan.FromSeconds(1)); stop.Dispose(); }
}
