using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.TestFixtures;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class FixtureSupportTests
{
    [Fact]
    public async Task ScriptSnapshotsPayloadAndCorrelatesCurrentRequestWithoutDecidingOutcome()
    {
        var payload = JsonNode.Parse("{\"kind\":\"finish\",\"reason\":\"cannotProceed\"}")!.AsObject();
        var planner = new ScriptedPlanner([payload, payload]);
        payload["reason"] = "goalClaimed";
        var input = new PlannerInputDocument("run", "request-1", "observation-1", "buy", null!, [], [], [], []);
        var first = await planner.DecideAsync(input, CancellationToken.None);
        Assert.Equal("request-1", first.DecisionRequestId);
        Assert.Equal("observation-1", first.BasedOnObservationId);
        Assert.Equal("cannotProceed", first.Decision["reason"]!.GetValue<string>());
        first.Decision["reason"] = "stuck";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await planner.DecideAsync(input, cancellation.Token));
        var second = await planner.DecideAsync(input with { DecisionRequestId = "request-2" }, CancellationToken.None);
        Assert.Equal("request-2", second.DecisionRequestId);
        Assert.Equal("cannotProceed", second.Decision["reason"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await planner.DecideAsync(input, CancellationToken.None));
    }

    [Fact]
    public void FaultRequiresExactBoundaryAndOccurrenceAndCannotBeCleared()
    {
        var plan = new[] { new AuthorizedFault("drop-purchase-response", FaultBoundary.AfterPurchaseCommitBeforeResponse, 2) };
        var harness = new FaultHarness(plan);
        plan[0] = new AuthorizedFault("changed", FaultBoundary.BeforeGameInput, 1);
        Assert.Null(harness.Reach(FaultBoundary.BeforeGameInput));
        Assert.Null(harness.Reach(FaultBoundary.AfterPurchaseCommitBeforeResponse));
        Assert.Throws<InvalidOperationException>(harness.RequireAllFired);
        Assert.Equal("drop-purchase-response", harness.Reach(FaultBoundary.AfterPurchaseCommitBeforeResponse)!.Id);
        harness.RequireAllFired();
        Assert.Null(harness.Reach(FaultBoundary.AfterPurchaseCommitBeforeResponse));
        Assert.Single(harness.Receipts);
    }

    [Fact]
    public void AmbiguousOrInvalidFaultPlansAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 0)]));
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 1), new("g", FaultBoundary.BeforeCapture, 1)]));
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 1), new("f", FaultBoundary.BeforeCapture, 2)]));
    }
}
