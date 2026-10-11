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

    private static string Canonical(string input)
    {
        var full = Path.GetFullPath(input);
        // Reuse Core's link, device and network-path rules. The volume root here
        // permits only canonicalization, not content access or caller authority:
        // every resulting object must subsequently pass the explicit roots above.
        return new AllowedPaths([Path.GetPathRoot(full)!]).Resolve(full, System.IO.Directory.GetCurrentDirectory());
    }
}
