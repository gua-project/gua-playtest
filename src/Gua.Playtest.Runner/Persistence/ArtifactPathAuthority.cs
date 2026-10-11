namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Filesystem identity authority for explicit files and directory containment.</summary>
public static class ArtifactPathAuthority
{
    public static string AuthorizeDirectory(string input, IEnumerable<string> roots, string refusal)
    {
        var path = Path.GetFullPath(input);
        RunArtifactStore.CheckPath(path);
        var requested = FileIdentity.ReadDirectory(path);
        foreach (var root in roots)
        {
            var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!Directory.Exists(allowed)) continue;
            var identity = FileIdentity.ReadDirectory(allowed);
            for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (FileIdentity.ReadDirectory(ancestor) != identity) continue;
                var canonical = Path.GetFullPath(Path.Combine(allowed, Path.GetRelativePath(ancestor, path)));
                RunArtifactStore.CheckPath(canonical);
                if (FileIdentity.ReadDirectory(canonical) != requested) throw new InvalidDataException(refusal);
                return canonical;
            }
        }
        throw new InvalidDataException(refusal);
    }
    public static string AuthorizeFile(string input, IEnumerable<string> roots, string refusal)
    {
        var path = Path.GetFullPath(input);
        RunArtifactStore.CheckPath(path);
        foreach (var root in roots)
        {
            var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!Directory.Exists(allowed)) continue;
            var identity = FileIdentity.ReadDirectory(allowed);
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (FileIdentity.ReadDirectory(ancestor) != identity) continue;
                // Rebase spelling only after the actual root and file objects match.
                var canonical = Path.GetFullPath(Path.Combine(allowed, Path.GetRelativePath(ancestor, path)));
                var requested = FileIdentity.OpenRegular(path);
                using (requested.Stream)
                {
                    var expected = FileIdentity.OpenRegular(canonical);
                    using (expected.Stream)
                        if (requested.Identity != expected.Identity) throw new InvalidDataException(refusal);
                }
                return canonical;
            }
        }
        throw new InvalidDataException(refusal);
    }
    public static bool ContainsDirectory(string ancestor, string directory)
    {
        var expected = FileIdentity.ReadDirectory(Path.GetFullPath(ancestor));
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            if (FileIdentity.ReadDirectory(current) == expected) return true;
        return false;
    }
}
