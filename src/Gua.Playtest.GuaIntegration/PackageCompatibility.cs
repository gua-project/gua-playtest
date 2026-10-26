using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

public static class PackageCompatibility
{
    public const string SourceCommit = "b398e531558f38220baf0b606e7c3f33904a87a5";

    /// <summary>Checks packaged native core/runtime only. Does not attach, launch or operate a game.</summary>
    public static void CheckNative()
    {
        foreach (var name in new[] { "GUA_NATIVE_DIR", "GUA_RUNTIME_NATIVE_DIR" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                throw new InvalidOperationException("Native package overrides are unsupported.");
        using var core = new GuaContext();
        var version = core.GetVersion();
        version.EnsureCompatible(protocolSchemaVersion: "2", abiVersion: 1);
        if (version.BuildId != SourceCommit) throw new InvalidOperationException("Native core package identity mismatch.");
        using var runtime = new GuaRuntime();
        var runtimeVersion = GuaVersion.Parse(runtime.GetVersionJson());
        runtimeVersion.EnsureCompatible(protocolSchemaVersion: "2", abiVersion: 1);
        if (runtimeVersion.BuildId != SourceCommit) throw new InvalidOperationException("Native runtime package identity mismatch.");
        using var metadata = JsonDocument.Parse(GuaDistribution.ViewerMetadata);
        if (metadata.RootElement.GetProperty("sourceCommit").GetString() != SourceCommit)
            throw new InvalidOperationException("Embedded Viewer package identity mismatch.");
    }
}
