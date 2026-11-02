using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gua.Playtest.GuaIntegration;

// Consumer policy for the public testing SDK, not an attestation of a game's build.
internal static class NativePackageIdentity
{
    internal static (string Core, string Runtime, string Rid) Names()
        => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, _, _, Architecture.X64) => ("gua.dll", "gua_runtime.dll", "win-x64"),
            (_, true, _, Architecture.X64) => ("libgua.so", "libgua_runtime.so", "linux-x64"),
            (_, _, true, Architecture.X64) => ("libgua.dylib", "libgua_runtime.dylib", "osx-x64"),
            (_, _, true, Architecture.Arm64) => ("libgua.dylib", "libgua_runtime.dylib", "osx-arm64"),
            _ => throw new PlatformNotSupportedException("Native testing package RID unsupported.")
        };

    internal static string FindDirectory()
    {
        var names = Names();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(Gua.Core.GuaContext).Assembly.Location),
            Path.GetDirectoryName(typeof(Gua.Runtime.GuaRuntime).Assembly.Location) }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value!))).Distinct(comparer).ToArray();
        var directories = roots.SelectMany(root => new[] { root, Path.Combine(root, "runtimes", names.Rid, "native") })
            .Distinct(comparer).Where(path => File.Exists(Path.Combine(path, names.Core)) || File.Exists(Path.Combine(path, names.Runtime))).ToArray();
        if (directories.Length != 1) throw Invalid();
        // The SDK also searches the current directory before its deps.json fallback.
        // An unrelated game directory must not silently replace the client payload.
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        if (!comparer.Equals(current.TrimEnd(Path.DirectorySeparatorChar), directories[0].TrimEnd(Path.DirectorySeparatorChar)) &&
            (File.Exists(Path.Combine(current, names.Core)) || File.Exists(Path.Combine(current, names.Runtime)))) throw Invalid();
        return directories[0];
    }

    internal static void ValidateDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        var names = Names();
        var expected = new HashSet<string>([names.Core, names.Runtime], StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if ((name.StartsWith("gua_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("libgua", StringComparison.OrdinalIgnoreCase)) &&
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)) &&
                !expected.Contains(name)) throw Invalid();
        }
        foreach (var manifest in new[] { "gua-core-build-manifest.json", "gua-runtime-build-manifest.json" })
        {
            using var document = ReadManifest(Path.Combine(directory, manifest));
            var root = document.RootElement;
            Fields(root, ["schemaVersion", "build", "engineBuildKind", "files"]);
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("engineBuildKind").GetString() != "release") throw Invalid();
            ValidateBuild(root.GetProperty("build"));
            var files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 2 or > 3) throw Invalid();
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in files.EnumerateArray())
            {
                Fields(entry, ["path", "sha256"]);
                var path = entry.GetProperty("path").GetString();
                var hash = entry.GetProperty("sha256").GetString();
                if (path is null || !found.Add(path) || hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) ||
                    !expected.Contains(path) && path != "LICENSE") throw Invalid();
                // LICENSE describes producer staging; NuGet does not copy it as a runtime
                // asset. Both actual native payloads are always verified in the consumer.
                if (path == "LICENSE") continue;
                using var payload = OpenRegular(Path.Combine(directory, path), 256 * 1024 * 1024);
                if (!Convert.ToHexStringLower(SHA256.HashData(payload)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            }
            if (!expected.IsSubsetOf(found)) throw Invalid();
        }
    }

    internal static void ValidateBuild(JsonElement build)
    {
        Fields(build, ["schemaVersion", "flavor", "configuration", "remoteEnabled", "remotePermissionCeiling", "buildId", "sourceCommit", "transports"]);
        if (build.GetProperty("schemaVersion").GetInt32() != 1 || build.GetProperty("flavor").GetString() != "development" ||
            build.GetProperty("configuration").GetString() != "Release" || !build.GetProperty("remoteEnabled").GetBoolean() ||
            build.GetProperty("remotePermissionCeiling").GetString() != "debug" ||
            build.GetProperty("buildId").GetString() != PackageCompatibility.SourceCommit ||
            build.GetProperty("sourceCommit").GetString() != PackageCompatibility.SourceCommit) throw Invalid();
        var transports = build.GetProperty("transports");
        if (transports.ValueKind != JsonValueKind.Array || transports.GetArrayLength() != 1 || transports[0].GetString() != "websocket") throw Invalid();
    }

    private static void Fields(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!expected.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name)) throw Invalid();
        if (found.Count != expected.Length) throw Invalid();
    }
    private static JsonDocument ReadManifest(string path)
    {
        using var input = OpenRegular(path, 65536);
        return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 12 });
    }
    private static FileStream OpenRegular(string path, int limit)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Invalid();
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > limit) throw Invalid();
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > limit) { file.Dispose(); throw Invalid(); }
        return file;
    }
    private static InvalidOperationException Invalid() => new("Native testing package manifest or build identity mismatch.");
}
