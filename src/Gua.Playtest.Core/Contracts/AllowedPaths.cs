namespace Gua.Playtest.Core.Contracts;

/// <summary>Explicit caller-supplied filesystem authority; never expanded by a document.</summary>
public sealed class AllowedPaths
{
    private readonly string[] roots;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public AllowedPaths(IEnumerable<string> allowedRoots)
    {
        roots = allowedRoots.Select(root => Canonical(Path.GetFullPath(root))).ToArray();
        if (roots.Length == 0 || roots.Any(root => !Directory.Exists(root))) throw new ArgumentException("Explicit existing allowed roots required.");
    }

    public string Resolve(string path, string baseDirectory)
    {
        var resolved = Canonical(Path.GetFullPath(path, baseDirectory));
        if (!roots.Any(root => resolved.Equals(root, Comparison) || resolved.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison)))
            throw new ContractException("PathOutsideAllowedRoots");
        return resolved;
    }

    private static string Canonical(string path, int links = 0)
    {
        if (links > 40) throw new ContractException("InvalidPath");
        // Static validation must not trigger network or Windows device namespace access.
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw new ContractException("InvalidPath");
        var root = Path.GetPathRoot(path) ?? throw new ContractException("InvalidPath");
        var current = root;
        foreach (var part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (OperatingSystem.IsWindows() && part.Contains(':')) throw new ContractException("InvalidPath");
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // LinkTarget also detects broken links; normal absent paths are rejected explicitly.
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(true) ?? throw new ContractException("InvalidPath");
                current = Canonical(Path.GetFullPath(target.FullName), links + 1);
            }
            else if (!info.Exists) throw new ContractException("ReferenceMissing");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
