using System.Threading.Channels;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Runner.Exploration;

/// <summary>One bounded worker-to-owner queue; no alternate lifecycle, budget or adoption logic.</summary>
internal sealed class ExploreCalls(ApprovedDecision decision) : IExploreCalls, IDisposable
{
    private readonly Channel<Action> queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource closed = new();
    public ValueTask<T> ReadAsync<T>(Func<T> read, CancellationToken token) => Enqueue(read, token);
    public ValueTask<T> SendAsync<T>(int index, Func<Action, ExploreSend<T>> send, CancellationToken token)
        => Enqueue(() =>
        {
            bool began = false;
            var reply = send(() =>
            {
                if (began) throw new InvalidOperationException("ExploreSendRepeated");
                if (decision.BeginDispatch(index) != PlannerFeedbackCode.Approved)
                    throw new RunFailureException(new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host));
                began = true;
            });
            if (reply.Sent && !began) throw new RunFailureException(new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host));
            if (reply.Sent) decision.ConfirmSent(index);
            return reply.Value;
        }, token);

    private async ValueTask<T> Enqueue<T>(Func<T> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
        var callToken = linked.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await queue.Writer.WriteAsync(() =>
            {
                if (callToken.IsCancellationRequested) { completion.TrySetCanceled(callToken); return; }
                try { completion.TrySetResult(action()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, linked.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == callToken)
        { throw new OperationCanceledException("ExploreCallCancelled", exception, token); }
    }
    public void Pump()
    {
        // A completed callback may wake a worker that immediately queues the next segment request.
        // Bound this drain; monitoring takes a fresh capture between subsequent batches.
        for (int count = 0; count < 1000 && queue.Reader.TryRead(out var call); count++) call();
    }
    public async ValueTask WaitAsync(CancellationToken token)
    { await queue.Reader.WaitToReadAsync(token).ConfigureAwait(false); }
    public void Dispose()
    {
        closed.Cancel(); queue.Writer.TryComplete();
        // Keep the CTS valid for workers still unwinding cancellation; no late queue call is pumped.
    }
}
