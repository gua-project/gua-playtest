using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Testing;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>Explicit, deterministic, observation-only Windows Run. Not AI exploration or input dispatch.
/// Uses the existing preparation/execution/result/artifact owners; never creates an alternate evaluator.</summary>
internal static class ObservationRunCommand
{
    private sealed class Clock : IClock
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        public TimeSpan Elapsed => timer.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
    private sealed class ObserverAvailable : IPreparationPlannerCheck
    { public ValueTask<bool> CheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); } }
    private sealed class Connector(Func<Uri, GuaObservationConnection> connect) : IPreparationConnector
    {
        internal GuaObservationConnection? Connection;
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token) => new(Task.Run<IPreparationConnection>(() =>
        {
            token.ThrowIfCancellationRequested();
            var connection = connect(endpoint);
            if (token.IsCancellationRequested)
            { connection.ReleaseAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); token.ThrowIfCancellationRequested(); }
            return Connection = connection;
        }, token));
    }
    private sealed class Launcher : IProcessLauncher
    {
        internal IOwnedProcessIdentity? Process;
        public async ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken token)
        {
            var acquired = await new SystemProcessLauncher().LaunchAsync(command, token);
            if (acquired is not IOwnedProcessIdentity identity)
            { await acquired.ShutdownAsync(CancellationToken.None); throw new InvalidOperationException("ProcessIdentityUnavailable"); }
            Process = identity; return identity;
        }
    }
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics, bool json, CancellationToken token)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidDataException("WindowsRuntimeRequired");
            var options = Parse(args);
            string One(string name) => options.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : throw new InvalidDataException("RuntimeArgumentsRequired");
            var roots = options.GetValueOrDefault("--allow-root") ?? throw new InvalidDataException("RuntimeArgumentsRequired");
            _ = new AllowedPaths(roots);
            var scenarioPath = StaticPathAuthority.File(One("--scenario"), roots, "RuntimeInputOutsideRoots");
            var environmentPath = StaticPathAuthority.File(One("--environment"), roots, "RuntimeInputOutsideRoots");
            var validator = StaticPathAuthority.Validator(roots);
            var scenarioReport = await validator.ValidateFileAsync(scenarioPath, token);
            var environmentReport = await validator.ValidateFileAsync(environmentPath, token);
            if (!scenarioReport.IsValid || !environmentReport.IsValid || scenarioReport.Document is not ScenarioDocument scenario ||
                environmentReport.Document is not EnvironmentDocument environment) throw new InvalidDataException("RuntimeInputInvalid");
            ValidateLane(scenario, environment);
            var executable = StaticPathAuthority.File(One("--executable"), roots, "RuntimeExecutableOutsideRoots");
            var working = StaticPathAuthority.Directory(One("--working-directory"), roots, "RuntimeWorkingDirectoryOutsideRoots");
            var destination = StaticPathAuthority.Directory(One("--output-root"), roots, "RuntimeOutputOutsideRoots");
            var hash = One("--executable-sha256");
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || environment.BuildId != "sha256-" + hash.ToLowerInvariant())
                throw new InvalidDataException("RuntimeBuildIdentityInvalid");
            // Keep the image locked against replacement/write for the entire owned Run on Windows.
            using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token)) != hash.ToLowerInvariant())
                throw new InvalidDataException("RuntimeBuildHashMismatch");
            var endpoint = new Uri(environment.Connection.Endpoint, UriKind.Absolute);
            if (endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/" ||
                endpoint.Port <= 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
                throw new InvalidDataException("WindowsLoopbackRequired");
            var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GUA_BRIDGE_PORT"] = endpoint.Port.ToString(CultureInfo.InvariantCulture) };
            foreach (var binding in options.GetValueOrDefault("--child-env") ?? [])
            {
                var split = binding.IndexOf('=');
                if (split <= 0 || binding[..split] is not ("APPDATA" or "FACTORY_SAVE_DIR") || child.ContainsKey(binding[..split]))
                    throw new InvalidDataException("RuntimeChildEnvironmentInvalid");
                child.Add(binding[..split], StaticPathAuthority.Directory(binding[(split + 1)..], roots, "RuntimeSaveOutsideRoots"));
            }
            var launch = new LaunchCommand(executable, working, options.GetValueOrDefault("--argument") ?? [], child);
            var effective = EffectiveExecution.Resolve(scenario, environment);
            var limits = effective.Limits;
            TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);
            var assertion = new AssertionOptions(checked((int)limits.RegexTimeoutMilliseconds), checked((int)limits.RegexMaxPatternLength));
            var success = PreparedCondition.Create(scenario.Goal.Success!, assertion);
            var clock = new Clock();
            var run = new RunSession(new(Ms(limits.MaxDurationMilliseconds), Ms(limits.PrepareTimeoutMilliseconds), Ms(limits.CleanupTimeoutMilliseconds),
                Ms(limits.PlannerTimeoutMilliseconds), Ms(limits.WaitTimeoutMilliseconds), Ms(limits.MaxSegmentMilliseconds),
                limits.MaxActions, limits.MaxDecisions, limits.RecoveryDecisionLimit, checked((int)Math.Min(100000, limits.TraceMaxEvents))), clock, clock, success);
            // The existing writer reserves a second file plus 64KiB for manifest/summaries.
            // Reduce, never raise the Environment's aggregate ceiling.
            int fileBytes = checked((int)Math.Min(16 * 1024 * 1024, (limits.TraceMaxBytes - 65536) / 2));
            var artifactLimits = new ArtifactLimits(fileBytes, limits.TraceMaxBytes, checked((int)Math.Min(100000, limits.TraceMaxEvents)), Math.Min(65536, fileBytes));
            var redactor = new PersistenceRedactor([], ["password", "token", "authorization", "secretValue"]);
            var store = RunArtifactStore.Create(destination, artifactLimits, redactor);
            var scenarioFile = scenarioReport.Files.Values.Single(); var environmentFile = environmentReport.Files.Values.Single();
            var identity = new RunIdentity(scenario.ScenarioId, scenario.DefinitionVersion, scenarioFile.Sha256, environment.BuildId, environmentFile.Sha256);
            var metadata = new RunArtifactMetadata(environment.Profile.ToLowerInvariant(),
                [new("driver", "observation-only", "explicit-command"), new("clock", "real", "explicit-command"),
                    new("gameExecutableSha256", hash.ToLowerInvariant(), "locked-image-and-OS-listener-owner"),
                    new("maxDurationMilliseconds", limits.MaxDurationMilliseconds.ToString(CultureInfo.InvariantCulture), "scenario-environment-minimum")],
                new Dictionary<string, string> { ["Gua.Testing"] = "1.1.2" },
                [new("scenario", JsonSerializer.SerializeToElement(scenarioFile.CopyJson()), scenarioFile.CopyBytes(), Path.GetExtension(scenarioPath)),
                    new("environment", JsonSerializer.SerializeToElement(environmentFile.CopyJson()), environmentFile.CopyBytes(), Path.GetExtension(environmentPath))], identity, DateTimeOffset.UtcNow);
            await using var artifacts = new GuaRunArtifacts(store, metadata, artifactLimits, new GuaTraceOptions
            {
                CaptureMode = environment.Trace.CaptureMode == "recent" ? GuaTraceCaptureMode.Recent : GuaTraceCaptureMode.Streaming,
                SavePolicy = environment.Trace.SavePolicy == "always" ? GuaTraceSavePolicy.Always : GuaTraceSavePolicy.OnFailure,
                Profile = environment.Profile.ToLowerInvariant(), MaxSteps = checked((int)limits.TraceRecentSteps),
                MaxMemoryBytes = fileBytes, MaxQueueItems = checked((int)limits.TraceQueueCapacity), MaxArtifactBytes = fileBytes,
                MaxAttachmentBytes = checked((int)Math.Min(limits.TraceMaxAttachmentBytes, fileBytes)), MaxEventBytes = checked((int)Math.Min(limits.MaxObservationBytes, fileBytes)),
                FlushTimeout = Ms(limits.CleanupTimeoutMilliseconds)
            }, redactor, token);
            if (!artifacts.PreparationPersistence.Saved) throw new InvalidDataException("RuntimeStorageUnavailable");
            var captureGate = new object();
            bool capturesClosed = false;
            long captureSequence = 0;
            var launcher = new Launcher();
            var connector = new Connector(address =>
            {
                var process = launcher.Process ?? throw new InvalidOperationException("OwnedProcessIdentityRequired");
                WindowsBridgeOwner.Require(address, process);
                return new(address, environment.BuildId, environment.Profile, clock, success, null,
                    checked((int)limits.MaxObservationNodes), checked((int)limits.MaxObservationBytes), Ms(limits.WaitTimeoutMilliseconds),
                    () => WindowsBridgeOwner.Require(address, process), (collection, host) =>
                    {
                        // Worker-safe fact storage only. A late capture has no result authority
                        // and cannot append Before evidence once the primary snapshot begins.
                        lock (captureGate)
                        {
                            if (capturesClosed) return;
                            var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, "Observation capture");
                            artifacts.Observe("capture-" + (++captureSequence).ToString(CultureInfo.InvariantCulture),
                                ObservationBoundary.Before, step, "gua", "fresh-read", collection.Availability.ToString(),
                                new(host.SourceId, host.Epoch), JsonSerializer.SerializeToElement(collection), "snapshot");
                            artifacts.Trace.EndStep(step, GuaTraceOutcome.Passed);
                        }
                    });
            });
            var cleanup = new OwnedCleanup();
            var preparation = new HostPreparation(new(HostMode.Launch, PlayMode.Explore, endpoint, environment.BuildId, "2", environment.Profile,
                "real", ["observe_v1"], true, true, launch, Ms(limits.PrepareTimeoutMilliseconds), TimeSpan.FromMilliseconds(25), 100,
                Ms(limits.CleanupTimeoutMilliseconds)), clock, launcher, connector, artifacts);
            PreparedHost? prepared = null;
            var outcome = await RunExecutor.ExecuteAsync(run, clock, cleanup,
                async (session, owned, cancel) => (prepared = await preparation.PrepareAsync(session, owned, null, new ObserverAvailable(), cancel)).Boundary,
                async (session, cancel) =>
                {
                    await RunMonitor.AwaitAsync(session, clock, clock, prepared!.Feed,
                        // The executor operation token also signals its deadline. Only the
                        // actual caller token may be classified as a user's cancellation.
                        async ct => { await clock.DelayAsync(Ms(limits.MaxDurationMilliseconds), ct); return true; }, _ => [], token);
                    return false;
                }, token, (snapshot, cancel) =>
                {
                    lock (captureGate) { capturesClosed = true; return artifacts.ConfirmPrimaryAsync(snapshot, cancel); }
                });
            var completion = await artifacts.CompleteAsync(outcome, DateTimeOffset.UtcNow);
            return await CommandOutput.WriteAsync(output, diagnostics, json, outcome.Primary.Status.ToString(), outcome.Primary.Cause.Reason.ToString(),
                completion.ExitCode(outcome), new { runId = store.RunId, artifactDirectory = store.DirectoryPath,
                    ownedProcessId = launcher.Process?.ProcessId,
                    driver = "observation-only", phase = outcome.Primary.Cause.Phase.ToString(), origin = outcome.Primary.Cause.Origin.ToString(),
                    postProcessingComplete = outcome.PostProcessingComplete && completion.Persistence.Saved && !completion.Persistence.PostProcessingIncomplete,
                    persistence = completion.Persistence.Failure.ToString(),
                    postProcessing = outcome.PostProcessing.Select(x => x.Reason.ToString()).ToArray() });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Interrupted", "CommandInterrupted", 4); }
        catch (InvalidDataException error) when (Refusals.Contains(error.Message))
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", error.Message, 2); }
        catch (Exception)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", "ObservationRuntimeRejected", 2); }
    }
    private static readonly HashSet<string> Refusals = new(StringComparer.Ordinal)
    {
        "WindowsRuntimeRequired", "RuntimeArgumentsRequired", "RuntimeInputOutsideRoots", "RuntimeInputInvalid",
        "RuntimeExecutableOutsideRoots", "RuntimeWorkingDirectoryOutsideRoots", "RuntimeOutputOutsideRoots", "RuntimeBuildIdentityInvalid",
        "RuntimeBuildHashMismatch", "WindowsLoopbackRequired", "RuntimeChildEnvironmentInvalid", "RuntimeSaveOutsideRoots",
        "RuntimeStorageUnavailable", "ObservationOnlyCapabilityUnavailable", "RuntimeArgumentsInvalid"
    };
    internal static void ValidateLane(ScenarioDocument scenario, EnvironmentDocument environment)
    {
        if (environment.Connection.Credential is not null || scenario.Setup is not null || environment.Fixture is not null ||
            environment.Navigation is not null || environment.Profile is not ("Player" or "Debug") || environment.Permissions.RawInput ||
            environment.Permissions.ActionIds.Length != 0 || scenario.Goal.Failure is not null || scenario.Goal.Success is not { } success ||
            success["kind"]?.GetValue<string>() != "assertion" || success["read"] is not JsonObject read ||
            read["valueType"]?["type"]?.GetValue<string>() is not ("bool" or "integer" or "number" or "string") ||
            !environment.Permissions.Reads.Any(allowed => JsonNode.DeepEquals(allowed, read)))
            throw new InvalidDataException("ObservationOnlyCapabilityUnavailable");
    }
    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var allowed = new[] { "--allow-root", "--scenario", "--environment", "--executable", "--executable-sha256", "--working-directory", "--output-root", "--argument", "--child-env" };
        if (args.Length > 256 || args.Length % 2 != 1) throw new InvalidDataException("RuntimeArgumentsRequired");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || args[i + 1].Length > 4096 || args[i + 1].Contains('\0'))
                throw new InvalidDataException("RuntimeArgumentsInvalid");
            if (!result.TryGetValue(args[i], out var values)) result.Add(args[i], values = []);
            if (values.Count != 0 && args[i] is not ("--allow-root" or "--argument" or "--child-env")) throw new InvalidDataException("RuntimeArgumentsInvalid");
            values.Add(args[i + 1]);
        }
        return result;
    }
}
