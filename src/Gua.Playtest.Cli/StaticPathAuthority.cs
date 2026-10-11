using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;

namespace Gua.Playtest.Cli;

/// <summary>Preserves static link canonicalization before applying physical root authority.</summary>
internal static class StaticPathAuthority
{
    internal static string File(string input, IEnumerable<string> roots, string refusal)
        => ArtifactPathAuthority.AuthorizeFile(Canonical(input), roots.Select(Canonical), refusal);

    internal static string Directory(string input, IEnumerable<string> roots, string refusal)
        => ArtifactPathAuthority.AuthorizeDirectory(Canonical(input), roots.Select(Canonical), refusal);

    internal static string ExplicitLocalFile(string input)
    {
        var canonical = Canonical(input);
        // Legacy packaged-schema mode admits precisely the selected local file.
        return ArtifactPathAuthority.AuthorizeFile(canonical, [Path.GetPathRoot(canonical)!], "LocalFileRequired");
    }

    private static string Canonical(string input)
    {
        if (input.StartsWith("\\\\", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("InvalidPath");
        var full = Path.GetFullPath(input);
        // Reuse Core's link, device and network-path rules. The volume root here
        // permits only canonicalization, not content access or caller authority:
        // every resulting object must subsequently pass the explicit roots above.
        try { return new AllowedPaths([Path.GetPathRoot(full)!]).Resolve(full, System.IO.Directory.GetCurrentDirectory()); }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException and not ArgumentException)
        { throw new InvalidDataException("InvalidPath", error); }
    }
}
