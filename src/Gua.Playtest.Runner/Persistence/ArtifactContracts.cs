using System.Text.Json;
using Gua.Playtest.Runner.Execution;

namespace Gua.Playtest.Runner.Artifacts;

public enum ArtifactKind { Trace, Recording }
public enum ArtifactState { NotExecuted, OmittedOnSuccess, CaptureFailed, SaveFailed, Saved }
public enum PersistenceFailure { None, Cancelled, LimitExceeded, InvalidEvidence, IoFailure }
public enum ObservationBoundary { Before, After, PrimaryDecision, AfterCleanup }
public sealed record ObservationReference(ObservationBoundary Boundary, string ObservationId);
public sealed record DecisionReference(string DecisionId, string ObservationId, string? ActionId, string ReasonCode);
public sealed record EffectiveSetting(string Name, string Value, string Source);
public sealed record InputCopy(string Name, JsonElement Document);
public sealed record RunArtifactMetadata(string Profile, IReadOnlyList<EffectiveSetting> Settings,
    IReadOnlyDictionary<string, string> Versions, IReadOnlyList<InputCopy> Inputs);
/// <summary>Receipt from a Gua-owned writer. Saved bytes must already be redacted by that writer.
/// SHA-256 covers those fixed bytes, never the original secret-bearing input.</summary>
public sealed record ArtifactFileReference(string FileName, long Bytes, string Sha256);
public sealed record ArtifactReceipt(ArtifactKind Kind, ArtifactState State,
    string? FileName = null, long? Bytes = null, string? Sha256 = null,
    IReadOnlyList<ArtifactFileReference>? AdditionalFiles = null);
public sealed record PersistenceResult(PersistenceFailure Failure, bool PostProcessingIncomplete = false)
{
    public bool Saved => Failure == PersistenceFailure.None;
    public int ExitCode(RunOutcome outcome) => outcome.Primary.ExitCode == 0 && (!Saved || PostProcessingIncomplete) ? 11 : outcome.ExitCode;
}
/// <summary>Explicit ceilings supplied by trusted Environment policy; no runtime defaults.</summary>
public sealed record ArtifactLimits
{
    public int MaxFileBytes { get; }
    public long MaxRunBytes { get; }
    public int MaxItems { get; }
    public int MaxStringChars { get; }
    public ArtifactLimits(int maxFileBytes, long maxRunBytes, int maxItems, int maxStringChars)
    {
        if (maxFileBytes < 256 || maxFileBytes > 16 * 1024 * 1024 || maxRunBytes < maxFileBytes ||
            maxRunBytes > 256L * 1024 * 1024 || maxItems < 1 || maxItems > 100000 ||
            maxStringChars < 1 || maxStringChars > maxFileBytes) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        MaxFileBytes = maxFileBytes; MaxRunBytes = maxRunBytes; MaxItems = maxItems; MaxStringChars = maxStringChars;
    }
}
