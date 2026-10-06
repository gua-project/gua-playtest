using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Runner.Artifacts;

public enum ResultReadState { Missing, Verified, Invalid, Unreadable }
public sealed record ResultReadback(ResultReadState State, ResultDocument? Result = null);
/// <summary>Read the existing public result schema independently of the writer. Missing/truncated
/// results never derive Passed from primary snapshots, observations or temporary files.</summary>
public static class RunArtifactReader
{
    public static async ValueTask<ResultReadback> ReadResultAsync(string directory, ArtifactLimits limits,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            RunArtifactStore.CheckPath(root);
            var file = Path.Combine(root, "result.json");
            if (!File.Exists(file)) return new(ResultReadState.Missing);
            RunArtifactStore.CheckPath(file);
            if (new FileInfo(file).Length > limits.MaxFileBytes) return new(ResultReadState.Invalid);
            var report = await new StaticContractValidator([root]).ValidateFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (!report.IsValid || report.Document is not ResultDocument result || result.RunId != Path.GetFileName(root))
                return new(ResultReadState.Invalid);
            return new(ResultReadState.Verified, result);
        }
        catch (InvalidDataException) { return new(ResultReadState.Invalid); }
        catch (IOException) { return new(ResultReadState.Unreadable); }
        catch (UnauthorizedAccessException) { return new(ResultReadState.Unreadable); }
    }
}
