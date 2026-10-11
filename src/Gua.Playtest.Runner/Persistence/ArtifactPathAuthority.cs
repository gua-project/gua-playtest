namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Filesystem identity authority for explicit files and directory containment.</summary>
public static class ArtifactPathAuthority
{
    public static string AuthorizeDirectory(string input, IEnumerable<string> roots, string refusal)
    {
        var path = LocalPath(input, refusal);
        CheckExistingTree(path);
        var requested = FileIdentity.ReadDirectory(path);
        foreach (var root in roots)
        {
            var allowed = Path.TrimEndingDirectorySeparator(LocalPath(root, refusal));
            CheckExistingTree(allowed);
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
        var path = LocalPath(input, refusal);
        CheckExistingTree(path);
        foreach (var root in roots)
        {
            var allowed = Path.TrimEndingDirectorySeparator(LocalPath(root, refusal));
            CheckExistingTree(allowed);
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
        var allowed = LocalPath(ancestor, "ArtifactPathNamespaceRejected");
        var path = LocalPath(directory, "ArtifactPathNamespaceRejected");
        CheckExistingTree(allowed); CheckExistingTree(path);
        var expected = FileIdentity.ReadDirectory(allowed);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (FileIdentity.ReadDirectory(current) == expected) return true;
        return false;
    }
    /// <summary>Pure local namespace check and path normalization; performs no target access.</summary>
    public static string LocalPath(string input, string refusal)
    {
        // This check performs no filesystem access, including malformed namespaces.
        if (input.StartsWith("\\\\", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException(refusal);
        var full = Path.GetFullPath(input);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal) ||
            OperatingSystem.IsWindows() && full[(Path.GetPathRoot(full)?.Length ?? 0)..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.Contains(':')))
            throw new InvalidDataException(refusal);
        return full;
    }
    public static string LocalStorageRoot(string input)
    {
        var full = LocalPath(input, "LocalStorageRequired");
        CheckExistingTree(full, allowMissing: true);
        return full;
    }
    private static void CheckExistingTree(string full, bool allowMissing = false)
    {
        var current = Path.GetPathRoot(full)!;
        // Inspect parents first: never probe through a junction/symlink before
        // discovering that its earlier component is outside artifact rules.
        foreach (var part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception e) when (allowMissing && e is FileNotFoundException or DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("LinkedArtifactPath");
        }
    }
}
