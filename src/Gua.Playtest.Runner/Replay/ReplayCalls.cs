using System.Threading.Channels;
using Gua.Playtest.Core;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Replay;

/// <summary>Gua may resume on workers; only this queue's owner pump touches Run authority/transport.
/// Closing cancels pending callbacks and leaves sent/uncertain requests consumed.</summary>
internal sealed class ReplayCalls(RunSession run, RunSession.ApprovedOperation operation,
    Func<ReplayCheck> check, Action dispatched) : IReplayCalls, IDisposable
{
    private sealed record Call(Action Execute, Action Cancel);
    private readonly Channel<Call> channel = Channel.CreateBounded<Call>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource closed = new();
    private int next;
    public ValueTask<T> ReadAsync<T>(Func<T> callback, CancellationToken token)
        => InvokeAsync(callback, token);
    public ValueTask<T> SendAsync<T>(int index, Func<Action, ReplaySend<T>> callback, CancellationToken token)
        => InvokeAsync(() =>
        {
            if (operation.DispatchClosedByGoal) throw new ReplayDispatchClosedException();
            if (index != next || index < 0 || index >= operation.Actions!.Deliveries.Count)
                throw Failure(RunReason.InvalidContract, RunOrigin.Contract);
            if (check() != ReplayCheck.Approved) throw Failure(RunReason.ActionFailed, RunOrigin.Host);
            token.ThrowIfCancellationRequested();
            bool began = false;
            var receipt = callback(() =>
            {
                if (began) throw Failure(RunReason.InvalidContract, RunOrigin.Contract);
                token.ThrowIfCancellationRequested();
                if (check() != ReplayCheck.Approved) throw Failure(RunReason.ActionFailed, RunOrigin.Host);
                operation.BeginDispatch(index); began = true; next++; dispatched();
            });
            if (receipt.Enqueued && !began)
            {
                // This is evidence of a possible side effect, never retrospective send authority.
                // Consume the attempt before immediately rejecting the missing boundary contract.
                operation.RecordUnconfirmedSend(index); dispatched();
                throw Failure(RunReason.InvalidContract, RunOrigin.Contract);
            }
            if (receipt.Enqueued) operation.Actions.ConfirmSent(index);
            return receipt.Value;
        }, token);

    private async ValueTask<T> InvokeAsync<T>(Func<T> callback, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
        var effectiveToken = linked.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new Call(() =>
        {
            try
            {
                effectiveToken.ThrowIfCancellationRequested();
                if (run.State != Core.Contracts.ExecutionState.Running || !operation.IsOpen || run.HasPendingTerminalEvidence)
                    throw Failure(RunReason.ActionFailed, RunOrigin.Host);
                completion.TrySetResult(callback());
            }
            catch (Exception exception)
            {
                if (exception is not (OperationCanceledException or ReplayDispatchClosedException)) run.PostProviderException(exception is RunFailureException ? exception
                    : new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host), exception));
                completion.TrySetException(exception);
            }
        }, () => completion.TrySetCanceled());
        await channel.Writer.WriteAsync(call, effectiveToken).ConfigureAwait(false);
        // Capture the token value before disposal; a cancelled queued callback may outlive
        // this waiter, and may only test the captured token before invoking transport.
        using var cancellation = effectiveToken.Register(() => completion.TrySetCanceled(effectiveToken));
        return await completion.Task.ConfigureAwait(false);
    }
    public void Pump(CancellationToken token)
    {
        if (!channel.Reader.TryRead(out var call)) return;
        if (token.IsCancellationRequested || closed.IsCancellationRequested) call.Cancel(); else call.Execute();
    }
    public async ValueTask WaitAsync(CancellationToken token) => _ = await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false);
    public void Dispose()
    {
        closed.Cancel(); channel.Writer.TryComplete();
        while (channel.Reader.TryRead(out var call)) call.Cancel();
        // Late provider callbacks may still hold this revocation token; do not dispose its source.
    }
    private static RunFailureException Failure(RunReason reason, RunOrigin origin) => new(new(reason, RunPhase.Execution, origin));
}
