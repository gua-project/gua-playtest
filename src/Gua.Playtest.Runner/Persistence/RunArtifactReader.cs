using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Artifacts;

public enum ResultReadState { Missing, Verified, Invalid, Unreadable, Interrupted }
public sealed record ResultReadback(ResultReadState State, ResultDocument? Result = null);
/// <summary>Read the existing public result schema independently of the writer. Missing/truncated
/// results never derive Passed from primary snapshots, observations or temporary files.</summary>
public static class RunArtifactReader
{
    public static ValueTask<ResultReadback> ReadResultAsync(string directory, ArtifactLimits limits,
        CancellationToken cancellationToken = default)
        => ReadWithValidatorAsync(directory, limits,
            (root, file, token) => new StaticContractValidator([root]).ValidateFileAsync(file, token), cancellationToken);

    // Internal boundary permits deterministic second-open race fixtures. Public callers always
    // use the independent static contract validator and cannot replace its authority.
    internal static async ValueTask<ResultReadback> ReadWithValidatorAsync(string directory, ArtifactLimits limits,
        Func<string, string, CancellationToken, ValueTask<StaticValidationReport>> validate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            RunArtifactStore.CheckPath(root);
            var file = Path.Combine(root, "result.json");
            RunArtifactStore.CheckPath(file);
            var opened = FileIdentity.OpenRegular(file);
            using (var stream = opened.Stream)
                if (stream.Length > limits.MaxFileBytes) return new(ResultReadState.Invalid);
            var report = await validate(root, file, cancellationToken).ConfigureAwait(false);
            if (report.Code == "Interrupted") return new(ResultReadState.Interrupted);
            if (report.Code == "ReferenceMissing") return new(ResultReadState.Missing);
            if (report.Code == "ReferenceUnreadable") return new(ResultReadState.Unreadable);
            if (!report.IsValid || report.Document is not ResultDocument result ||
                !MatchesRunIdentity(root, result.RunId))
                return new(ResultReadState.Invalid);
            return new(ResultReadState.Verified, result);
        }
        catch (FileNotFoundException) { return new(ResultReadState.Missing); }
        catch (InvalidDataException) { return new(ResultReadState.Invalid); }
        catch (IOException) { return new(ResultReadState.Unreadable); }
        catch (UnauthorizedAccessException) { return new(ResultReadState.Unreadable); }
    }
    private static bool MatchesRunIdentity(string root, string runId)
    {
        var name = Path.GetFileName(root);
        if (string.Equals(runId, name, StringComparison.Ordinal)) return true;
        if (!string.Equals(runId, name, StringComparison.OrdinalIgnoreCase)) return false;
        var parent = Path.GetDirectoryName(root);
        if (parent is null) return false;
        try
        {
            // Actual lookup and directory identities honor this volume's semantics on every OS.
            // File identity alone would wrongly accept different case-sensitive directories whose
            // result entries are hard links to the same physical file.
            return FileIdentity.ReadDirectory(root) == FileIdentity.ReadDirectory(Path.Combine(parent, runId));
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
