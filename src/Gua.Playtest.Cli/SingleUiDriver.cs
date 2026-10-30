using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Cli;

/// <summary>Trusted explicit one-operation driver, not a Planner or Replay engine.</summary>
internal static class SingleUiDriver
{
    internal static async ValueTask<bool> ExecuteAsync(RunSession run, IClock clock, GuaObservationConnection feed,
        string targetId, Action<GuardedUiReceipt> record, Action verifyOwner, CancellationToken caller)
    {
        var click = feed.Click ?? throw new RunFailureException(new(RunReason.ExecutionError, RunPhase.Execution, RunOrigin.Host));
        var operation = run.ApproveOperation(1, run.Limits.WaitTimeout);
        if (operation is null) { run.Evaluate(cancelled: caller.IsCancellationRequested); return false; }
        try
        {
            // Synchronous public SDK send on the serialized owner. Its hook runs once,
            // immediately before actual transport, and checks the existing Run authority.
            var receipt = click.Click(targetId, () => { caller.ThrowIfCancellationRequested(); verifyOwner(); operation.BeginDispatch(0); }, caller);
            record(receipt);
            if (receipt.DispatchBegan)
            {
                if (receipt.State != GuaRemoteDispatchState.Uncertain) operation.Actions!.ConfirmSent(0);
            }
            else operation.Actions!.ConfirmNotSent(0);
            async ValueTask<GuardedUiReceipt> Settle(CancellationToken token)
            {
                while (receipt.State == GuaRemoteDispatchState.Enqueued)
                {
                    token.ThrowIfCancellationRequested();
                    receipt = click.Poll();
                    if (receipt.State == GuaRemoteDispatchState.Enqueued) await clock.DelayAsync(TimeSpan.FromMilliseconds(25), token);
                }
                return receipt;
            }
            IReadOnlyList<RunEvent> Events(GuardedUiReceipt value) => value.State switch
            {
                GuaRemoteDispatchState.Uncertain => [new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host)],
                GuaRemoteDispatchState.Completed when value.Succeeded == true => [],
                _ => [new(RunReason.ActionFailed, RunPhase.Execution, RunOrigin.Host)]
            };
            await RunMonitor.AwaitAsync(run, clock, clock, feed, Settle, Events, caller, completed =>
            {
                record(completed);
                if (completed.State != GuaRemoteDispatchState.Uncertain && !operation.ConfirmResult())
                    throw new RunFailureException(new(RunReason.ActionUnconfirmed, RunPhase.Execution, RunOrigin.Host));
            });
        }
        finally { operation.Complete(); }
        if (run.Primary is null)
            await RunMonitor.AwaitAsync(run, clock, clock, feed,
                async token => { await clock.DelayAsync(run.Limits.MaxDuration, token); return true; }, _ => [], caller);
        return false;
    }
}
