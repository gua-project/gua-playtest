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
        bool responseDropped = false;
        bool ApplyDrop() { responseDropped = true; return true; }
        Assert.Null(harness.ApplyAt(FaultBoundary.BeforeGameInput, ApplyDrop));
        Assert.Null(harness.ApplyAt(FaultBoundary.AfterPurchaseCommitBeforeResponse, ApplyDrop));
        Assert.False(responseDropped);
        Assert.Throws<InvalidOperationException>(harness.RequireAllFired);
        Assert.Equal("drop-purchase-response", harness.ApplyAt(FaultBoundary.AfterPurchaseCommitBeforeResponse, ApplyDrop)!.Id);
        Assert.True(responseDropped);
        harness.RequireAllFired();
        Assert.Null(harness.ApplyAt(FaultBoundary.AfterPurchaseCommitBeforeResponse, ApplyDrop));
        Assert.Single(harness.Receipts);
    }

    [Fact]
    public void FalseOrThrowingFaultEffectNeverProducesReceiptOrPassesAllFired()
    {
        var noEffect = new FaultHarness([new("drop", FaultBoundary.AfterPurchaseCommitBeforeResponse, 1)]);
        Assert.Null(noEffect.ApplyAt(FaultBoundary.AfterPurchaseCommitBeforeResponse, () => false));
        Assert.Empty(noEffect.Receipts);
        Assert.Throws<InvalidOperationException>(noEffect.RequireAllFired);
        var failed = new FaultHarness([new("drop", FaultBoundary.AfterPurchaseCommitBeforeResponse, 1)]);
        Assert.Throws<IOException>(() => failed.ApplyAt(FaultBoundary.AfterPurchaseCommitBeforeResponse,
            () => throw new IOException("effect-failed")));
        Assert.Empty(failed.Receipts);
        Assert.Throws<InvalidOperationException>(failed.RequireAllFired);
    }

    [Fact]
    public void AmbiguousOrInvalidFaultPlansAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 0)]));
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 1), new("g", FaultBoundary.BeforeCapture, 1)]));
        Assert.Throws<ArgumentException>(() => new FaultHarness([new("f", FaultBoundary.BeforeCapture, 1), new("f", FaultBoundary.BeforeCapture, 2)]));
    }
}
