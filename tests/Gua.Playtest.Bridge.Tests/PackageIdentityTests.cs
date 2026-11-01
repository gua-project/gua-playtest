using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Playtest.GuaIntegration;
using Xunit;

namespace Gua.Playtest.Bridge.Tests;

public sealed class PackageIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "package-identity-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] ManifestNames = ["gua-core-build-manifest.json", "gua-runtime-build-manifest.json"];
    private static JsonObject Build() => new()
    {
        ["schemaVersion"] = 1, ["flavor"] = "development", ["configuration"] = "Release",
        ["remoteEnabled"] = true, ["remotePermissionCeiling"] = "debug",
        ["sourceCommit"] = PackageCompatibility.SourceCommit, ["buildId"] = PackageCompatibility.SourceCommit,
        ["transports"] = new JsonArray("websocket")
    };
    private JsonObject Prepare()
    {
        Directory.CreateDirectory(root);
        var names = NativePackageIdentity.Names();
        foreach (var name in new[] { names.Core, names.Runtime }) File.WriteAllBytes(Path.Combine(root, name), [1, 2, 3]);
        var files = new JsonArray();
        foreach (var name in new[] { names.Core, names.Runtime }) files.Add(new JsonObject
        { ["path"] = name, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) });
        // Public NuGet's producer license entry is not copied as a runtime asset.
        files.Add(new JsonObject { ["path"] = "LICENSE", ["sha256"] = new string('a', 64) });
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["build"] = Build(), ["engineBuildKind"] = "release", ["files"] = files };
        Write(manifest); return manifest;
    }
    private void Write(JsonObject manifest)
    { foreach (var name in ManifestNames) File.WriteAllText(Path.Combine(root, name), manifest.ToJsonString()); }

    [Fact]
    public void InstalledPublicTestingSdkHasMatchingRemoteEnabledNativeIdentityAndManifests()
    {
        PackageCompatibility.CheckNative();
        using var build = JsonDocument.Parse(GuaBuildInfo.GetJson());
        Assert.Equal("development", build.RootElement.GetProperty("flavor").GetString());
        Assert.True(build.RootElement.GetProperty("remoteEnabled").GetBoolean());
        Assert.Equal("debug", build.RootElement.GetProperty("remotePermissionCeiling").GetString());
        Assert.Equal(PackageCompatibility.SourceCommit, build.RootElement.GetProperty("sourceCommit").GetString());
    }
    [Fact]
    public void NativePairManifestVerifiesBothPayloadsWithoutInventingMissingRuntimeLicense()
    {
        Prepare(); NativePackageIdentity.ValidateDirectory(root);
        Assert.False(File.Exists(Path.Combine(root, "LICENSE")));
    }
    [Theory]
    [InlineData("flavor", "shipping")]
    [InlineData("flavor", "qa")]
    [InlineData("flavor", "ai-player")]
    [InlineData("remotePermissionCeiling", "player")]
    [InlineData("configuration", "Debug")]
    [InlineData("sourceCommit", "0000000000000000000000000000000000000000")]
    [InlineData("buildId", "0000000000000000000000000000000000000000")]
    public void TestingSdkCannotSilentlySwitchDistributionPurposeOrBuild(string field, string value)
    {
        var manifest = Prepare(); manifest["build"]![field] = value; Write(manifest);
        Assert.Throws<InvalidOperationException>(() => NativePackageIdentity.ValidateDirectory(root));
    }
    [Theory]
    [InlineData("remote-off")]
    [InlineData("transport-missing")]
    [InlineData("schema")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    public void InvalidOrRemoteDisabledBuildIdentityFailsClosed(string mode)
    {
        var manifest = Prepare(); var build = manifest["build"]!.AsObject();
        if (mode == "remote-off") build["remoteEnabled"] = false;
        if (mode == "transport-missing") build["transports"] = new JsonArray();
        if (mode == "schema") build["schemaVersion"] = 2;
        if (mode == "unknown-field") build["assumeRemote"] = true;
        if (mode == "missing-field") build.Remove("remoteEnabled");
        Write(manifest); Assert.Throws<InvalidOperationException>(() => NativePackageIdentity.ValidateDirectory(root));
    }
    [Theory]
    [InlineData("hash")]
    [InlineData("payload")]
    [InlineData("path")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("manifest")]
    [InlineData("mismatched-manifest")]
    public void CorruptOrMixedNativePairCannotPassConsumerAdmission(string mode)
    {
        var manifest = Prepare(); var files = manifest["files"]!.AsArray(); var names = NativePackageIdentity.Names();
        if (mode == "hash") files[0]!["sha256"] = new string('0', 64);
        if (mode == "path") files[0]!["path"] = "../" + names.Core;
        if (mode == "duplicate") files[1]!["path"] = names.Core;
        if (mode == "missing") files.RemoveAt(0);
        Write(manifest);
        if (mode == "payload") File.WriteAllBytes(Path.Combine(root, names.Runtime), [4, 5, 6]);
        if (mode == "extra") File.WriteAllBytes(Path.Combine(root, "gua_unexpected.dll"), [7]);
        if (mode == "manifest") File.Delete(Path.Combine(root, ManifestNames[0]));
        if (mode == "mismatched-manifest")
        {
            manifest["build"]!["flavor"] = "qa";
            File.WriteAllText(Path.Combine(root, ManifestNames[1]), manifest.ToJsonString());
        }
        Assert.ThrowsAny<Exception>(() => NativePackageIdentity.ValidateDirectory(root));
    }
    [Fact]
    public void DuplicateBuildFieldsAndOversizedManifestAreRefused()
    {
        Prepare();
        using var duplicate = JsonDocument.Parse(Build().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
        Assert.Throws<InvalidOperationException>(() => NativePackageIdentity.ValidateBuild(duplicate.RootElement));
        File.WriteAllText(Path.Combine(root, ManifestNames[0]), new string(' ', 65537));
        Assert.Throws<InvalidOperationException>(() => NativePackageIdentity.ValidateDirectory(root));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
