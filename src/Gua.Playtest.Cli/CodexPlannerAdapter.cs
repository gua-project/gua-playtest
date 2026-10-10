using Gua.Playtest.Core;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Planners.Codex;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Cli;

/// <summary>Composition adapter only. No process launch, authentication, adoption or game authority.</summary>
public sealed class CodexPlannerAdapter(ICodexDecisionBackend backend) : IPlanner<PlannerInputDocument, PlannerReply>
{
    public async ValueTask<PlannerReply> DecideAsync(PlannerInputDocument input, CancellationToken cancellationToken)
    {
        var reply = await backend.DecideAsync(input, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return reply.Status switch
        {
            CodexReplyStatus.Completed when reply.CompletedJson is { Length: > 0 } =>
                new(PlannerReplyStatus.Completed, (byte[])reply.CompletedJson.Clone()),
            CodexReplyStatus.UsageLimit => new(PlannerReplyStatus.UsageLimit),
            CodexReplyStatus.ConnectionFailure => new(PlannerReplyStatus.ConnectionFailure),
            _ => new(PlannerReplyStatus.OutputInvalid)
        };
    }
}
