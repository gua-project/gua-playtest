using System.Diagnostics;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Cli;

// Bounded, per-command, numeric diagnostics. Hot notifications perform no I/O,
// acquire no application locks, and never inspect RunSession or game contents.
internal sealed class ObservationTiming : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly Sample?[] samples;
    private readonly AsyncLocal<Sample?> active = new();
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly IDisposable all;
    private IDisposable? sdk, evaluation;
    private int count;
    private long primaryObserved;
    internal ObservationTiming(int capacity = 64)
    {
        samples = new Sample[Math.Clamp(capacity, 1, 64)];
        all = DiagnosticListener.AllListeners.Subscribe(this);
    }
    internal Sample? Reserve(CancellationToken token)
    {
        int index = Interlocked.Increment(ref count) - 1;
        if (index >= samples.Length) return null;
        var sample = new Sample(index + 1, token);
        Volatile.Write(ref samples[index], sample);
        return sample;
    }
    internal Sample? Enter(Sample? sample) { var previous = active.Value; active.Value = sample; return previous; }
    internal void Restore(Sample? previous) => active.Value = previous;
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Gua.Playtest.ObservationSdk") sdk = listener.Subscribe(this, _ => active.Value is not null);
        if (listener.Name == "Gua.Playtest.ObservationEvaluation") evaluation = listener.Subscribe(this);
    }
    public void OnNext(KeyValuePair<string, object?> notification)
    {
        if (notification.Key == "Call" && notification.Value is ValueTuple<string, long, int> call)
            active.Value?.Sdk(call.Item1, call.Item2, call.Item3);
        else if (notification.Key == "Evaluation" && notification.Value is ValueTuple<RunObservation, long, int> evaluated)
            for (int i = 0; i < Math.Min(Volatile.Read(ref count), samples.Length); i++)
            {
                var sample = Volatile.Read(ref samples[i]);
                if (sample is not null && ReferenceEquals(Volatile.Read(ref sample.Observation), evaluated.Item1))
                {
                    if (evaluated.Item3 == 0) Interlocked.CompareExchange(ref sample.EvaluationStarted, evaluated.Item2, 0);
                    else Interlocked.CompareExchange(ref sample.EvaluationEnded, evaluated.Item2, 0);
                    break;
                }
            }
    }
    public void OnError(Exception error) { }
    public void OnCompleted() { }
    public void Dispose() { all.Dispose(); sdk?.Dispose(); evaluation?.Dispose(); }
    internal void ObservePrimary() => Interlocked.CompareExchange(ref primaryObserved, Stopwatch.GetTimestamp(), 0);
    internal object Snapshot()
    {
        long at = Stopwatch.GetTimestamp();
        var observations = samples.Select(x => x?.Snapshot(origin)).Where(x => x is not null).ToArray();
        long primary = Volatile.Read(ref primaryObserved);
        return new
        {
            schema = "playtest.observation-timing.v1", frequency = Stopwatch.Frequency,
            snapshotStartedTicks = at - origin, snapshotEndedTicks = Stopwatch.GetTimestamp() - origin,
            primaryObservedTicks = primary == 0 ? (long?)null : primary - origin, limit = samples.Length,
            dropped = Math.Max(0, Volatile.Read(ref count) - samples.Length),
            missingPhaseMeaning = "not-observed-at-snapshot; not proof of never-started",
            sampling = "bounded-best-effort; snapshot may race late work; missing notifications are not negative proof",
            sdkBoundary = "public SDK calls include SDK/transport/host; local-selector-check includes local reconstruction; no internal split",
            observations
        };
    }
    internal sealed class Sample(int number, CancellationToken token)
    {
        internal readonly long Reserved = Stopwatch.GetTimestamp();
        internal long Worker, Gate, Returned, EvaluationStarted, EvaluationEnded;
        internal RunObservation? Observation;
        internal Task? Task;
        internal int Outcome; // 0 not observed terminal, 1 success, 2 exception, 3 cancellation
        private readonly SdkCall?[] calls = new SdkCall[64];
        private int callCount, current = -1;
        internal void Sdk(string name, long timestamp, int phase)
        {
            if (phase == 0)
            {
                int index = callCount++;
                current = index;
                if (index < calls.Length) Volatile.Write(ref calls[index], new(name, timestamp));
            }
            else if (current >= 0 && current < calls.Length && calls[current] is { } call)
            { Volatile.Write(ref call.Ended, timestamp); Volatile.Write(ref call.Outcome, phase); }
        }
        private sealed class SdkCall(string operation, long started)
        {
            internal readonly string Operation = operation;
            internal readonly long Started = started;
            internal long Ended;
            internal int Outcome;
        }
        internal object Snapshot(long origin)
        {
            long worker = Volatile.Read(ref Worker), gate = Volatile.Read(ref Gate), returned = Volatile.Read(ref Returned);
            long evaluationStarted = Volatile.Read(ref EvaluationStarted), evaluationEnded = Volatile.Read(ref EvaluationEnded);
            long? Tick(long timestamp) => timestamp == 0 ? null : timestamp - origin;
            double? Milliseconds(long from, long to) => from == 0 || to < from ? null : (to - from) * 1000d / Stopwatch.Frequency;
            var copied = calls.Select(x => x is null ? null : new
            {
                operation = x.Operation, startedTicks = Tick(x.Started), endedTicks = Tick(Volatile.Read(ref x.Ended)),
                outcome = Volatile.Read(ref x.Outcome) switch { 1 => "returned", 2 => "exception", _ => "not-observed-terminal" },
                milliseconds = Milliseconds(x.Started, Volatile.Read(ref x.Ended))
            }).Where(x => x is not null).ToArray();
            var finished = gate != 0 && returned != 0 && copied.Length > 0 && copied.All(x => x!.milliseconds.HasValue) && Volatile.Read(ref callCount) <= calls.Length;
            double? sdkMs = finished ? copied.Sum(x => x!.milliseconds!.Value) : null;
            double? localMs = finished ? copied.Where(x => x!.operation == "local-selector-check").Sum(x => x!.milliseconds!.Value) : null;
            var inside = Milliseconds(gate, returned);
            return new
            {
                number, reservedTicks = Tick(Reserved), workerTicks = Tick(worker), gateTicks = Tick(gate), returnedTicks = Tick(returned),
                evaluationStartedTicks = Tick(evaluationStarted), evaluationEndedTicks = Tick(evaluationEnded),
                workerOutcome = Volatile.Read(ref Outcome) switch { 1 => "returned", 2 => "exception", 3 => "cancelled", _ => "not-observed-terminal" },
                taskStatus = Volatile.Read(ref Task)?.Status.ToString() ?? "not-observed", tokenCancellationRequested = token.IsCancellationRequested,
                queueMs = Milliseconds(Reserved, worker), gateWaitMs = Milliseconds(worker, gate), sdkMs,
                remoteSdkMs = sdkMs - localMs, localSelectorMs = localMs,
                adapterNonSdkMs = inside.HasValue && sdkMs.HasValue ? Math.Max(0, inside.Value - sdkMs.Value) : (double?)null,
                deliveryToEvaluationMs = Milliseconds(returned, evaluationStarted),
                evaluationMs = Milliseconds(evaluationStarted, evaluationEnded),
                sdkDropped = Math.Max(0, Volatile.Read(ref callCount) - calls.Length), sdkCalls = copied
            };
        }
    }
}
