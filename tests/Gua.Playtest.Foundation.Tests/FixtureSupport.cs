using System.Text.Json.Nodes;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.TestFixtures;

// Test-only: compile into tests, never a product assembly or CLI option.
public sealed class ScriptedPlanner : IPlanner<PlannerInputDocument, PlannerDecisionDocument>
{
    private readonly Queue<JsonObject> script;
    public ScriptedPlanner(IEnumerable<JsonObject> decisions) =>
        script = new Queue<JsonObject>(decisions.Select(d => (JsonObject)d.DeepClone()));

    public ValueTask<PlannerDecisionDocument> DecideAsync(PlannerInputDocument input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (script.Count == 0) throw new InvalidOperationException("script-exhausted");
        return ValueTask.FromResult(new PlannerDecisionDocument(input.RunId, input.DecisionRequestId,
            input.BasedOnObservationId, script.Dequeue()));
    }
}

public enum FaultBoundary { BeforeGameInput, AfterPurchaseCommitBeforeResponse, BeforeObservation,
    BeforePlannerReply, BeforeCapture, BeforeArtifactWrite, DuringCleanup }
public sealed record AuthorizedFault(string Id, FaultBoundary Boundary, int Occurrence);
public sealed record FaultReceipt(string Id, FaultBoundary Boundary, int Occurrence);

// Held by trusted fixture setup only. Never passed to ScriptedPlanner or serialized into its input.
public sealed class FaultHarness
{
    private readonly AuthorizedFault[] plan;
    private readonly Dictionary<FaultBoundary, int> occurrences = [];
    private readonly List<FaultReceipt> receipts = [];
    public FaultHarness(IEnumerable<AuthorizedFault> authorizedBeforeStart)
    {
        plan = authorizedBeforeStart.ToArray();
        if (plan.Any(f => string.IsNullOrWhiteSpace(f.Id) || f.Occurrence <= 0 || !Enum.IsDefined(f.Boundary)) ||
            plan.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != plan.Length ||
            plan.Select(f => (f.Boundary, f.Occurrence)).Distinct().Count() != plan.Length)
            throw new ArgumentException("invalid-fault-plan");
    }
    public IReadOnlyList<FaultReceipt> Receipts => receipts.ToArray();
    public FaultReceipt? Reach(FaultBoundary boundary)
    {
        if (!Enum.IsDefined(boundary)) throw new ArgumentOutOfRangeException(nameof(boundary));
        int occurrence = occurrences.GetValueOrDefault(boundary) + 1;
        occurrences[boundary] = occurrence;
        var fault = plan.SingleOrDefault(f => f.Boundary == boundary && f.Occurrence == occurrence);
        if (fault is null) return null;
        var receipt = new FaultReceipt(fault.Id, boundary, occurrence);
        receipts.Add(receipt);
        return receipt;
    }
    public void RequireAllFired()
    {
        if (receipts.Count != plan.Length) throw new InvalidOperationException("fault-not-fired");
    }
}
