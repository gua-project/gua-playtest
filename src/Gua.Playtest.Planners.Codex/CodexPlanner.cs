using Gua.Playtest.Core;

namespace Gua.Playtest.Planners.Codex;

/// <summary>Explicit unavailable module until #13 supplies a verified App Server transport.</summary>
public sealed class CodexPlanner<TInput, TDecision> : IPlanner<TInput, TDecision>
{
    public ValueTask<TDecision> DecideAsync(TInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Codex App Server integration is not implemented (#13).");
    }
}
