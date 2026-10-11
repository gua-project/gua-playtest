namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Existing directory containment by filesystem identity, including case aliases.</summary>
public static class ArtifactPathAuthority
{
    public static bool ContainsDirectory(string ancestor, string directory)
    {
        var expected = FileIdentity.ReadDirectory(Path.GetFullPath(ancestor));
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            if (FileIdentity.ReadDirectory(current) == expected) return true;
        return false;
    }
}
