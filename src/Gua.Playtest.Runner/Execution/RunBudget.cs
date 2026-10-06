namespace Gua.Playtest.Runner.Execution;

public enum DeliveryState { Reserved, NotSent, Sent, Uncertain }
public sealed record BudgetSnapshot(long Actions, long ReservedActions, long Decisions, long ActionDecisions, long RecoveryDecisions);

/// <summary>Owned by the serialized RunSession. Reserve the complete segment before invoking transport.</summary>
public sealed class RunBudget
{
    private readonly RunLimits limits;
    private long actions, reserved, decisions, actionDecisions, recovery;
    private readonly List<ActionReservation> reservations = [];
    internal RunBudget(RunLimits limits) => this.limits = limits;
    public BudgetSnapshot Snapshot => new(actions, reserved, decisions, actionDecisions, recovery);
    internal bool Closed { get; private set; }
    internal RunReason? Exhaustion => actions + reserved >= limits.MaxActions ? RunReason.ActionsExhausted :
        decisions >= limits.MaxDecisions ? RunReason.DecisionsExhausted :
        recovery >= limits.RecoveryDecisions ? RunReason.RecoveryExhausted : null;
    internal bool RequestDecision(bool recovering)
    {
        if (Closed || Exhaustion.HasValue) return false;
        decisions++;
        if (recovering) recovery++;
        return true;
    }
    internal ActionReservation? Reserve(int count)
    {
        if (Closed || count <= 0 || count > 1000 || count > limits.MaxActions - actions - reserved) return null;
        reserved += count;
        actionDecisions++;
        reservations.RemoveAll(x => x.IsSettled);
        var item = new ActionReservation(this, count);
        reservations.Add(item);
        return item;
    }
    internal void Close() => Closed = true;
    internal void Settle()
    {
        Close();
        foreach (var item in reservations) item.CancelUnsent();
    }
    internal void Resolve(DeliveryState state)
    {
        reserved--;
        if (state is DeliveryState.Sent or DeliveryState.Uncertain) actions++;
    }
}

public sealed class ActionReservation
{
    private readonly RunBudget budget;
    private readonly DeliveryState[] states;
    internal ActionReservation(RunBudget budget, int count) { this.budget = budget; states = new DeliveryState[count]; }
    public IReadOnlyList<DeliveryState> Deliveries => Array.AsReadOnly((DeliveryState[])states.Clone());
    internal bool IsSettled => states.All(x => x != DeliveryState.Reserved);
    /// <summary>Called at the transport boundary before invoking a potentially side-effecting request.
    /// Until authoritative acknowledgement, delivery is uncertain and consumes an attempt.</summary>
    internal void BeginDispatch(int index)
    {
        if (budget.Closed) throw new InvalidOperationException("RunActionsClosed");
        if (states[index] != DeliveryState.Reserved) throw new InvalidOperationException("ActionAlreadySettled");
        states[index] = DeliveryState.Uncertain;
        budget.Resolve(DeliveryState.Uncertain);
    }
    public void ConfirmSent(int index)
    {
        if (states[index] != DeliveryState.Uncertain) throw new InvalidOperationException("ActionNotDispatched");
        states[index] = DeliveryState.Sent;
    }
    public void ConfirmNotSent(int index)
    {
        // Only before dispatch. Timeout/abort or a missing reply cannot refund a possibly applied action.
        if (states[index] != DeliveryState.Reserved) throw new InvalidOperationException("ActionDeliveryUncertain");
        states[index] = DeliveryState.NotSent;
        budget.Resolve(DeliveryState.NotSent);
    }
    public void CancelUnsent()
    {
        for (var i = 0; i < states.Length; i++) if (states[i] == DeliveryState.Reserved) ConfirmNotSent(i);
    }
}
