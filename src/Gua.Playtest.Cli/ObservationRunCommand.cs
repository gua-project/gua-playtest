using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Testing;
using Gua.Playtest.GuaIntegration;
using Gua.Playtest.Core;
using Gua.Playtest.Core.Assertions;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Runner.Artifacts;
using Gua.Playtest.Runner.Conditions;
using Gua.Playtest.Runner.Execution;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>Shared explicit Windows Run composition: observation-only or one guarded UI click.
/// Uses existing preparation/execution/result/artifact owners; never an alternate evaluator or Planner.</summary>
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
        internal readonly PendingConnections Pending = new();
        public ValueTask<IPreparationConnection> ConnectAsync(Uri endpoint, CancellationToken token) => new(Task.Run<IPreparationConnection>(async () =>
        {
            token.ThrowIfCancellationRequested();
            var connection = await ReadyConnection.AcquireAsync(() => Pending.Track(() => connect(endpoint)), token);
            if (!Pending.Handoff(connection))
            {
                await connection.ReleaseAsync(CancellationToken.None);
                throw new OperationCanceledException(token);
            }
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
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics, bool json, CancellationToken token, bool singleClick = false)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidDataException("WindowsRuntimeRequired");
            var options = Parse(args, singleClick);
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
            ValidateLane(scenario, environment, singleClick);
            var targetId = singleClick ? One("--target-id") : null;
            if (targetId is not null && (targetId.Length is < 1 or > 128 || targetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))))
                throw new InvalidDataException("RuntimeArgumentsInvalid");
            var driverName = singleClick ? "single-ui-click" : "observation-only";
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
            var precondition = scenario.Setup is null ? null : PreparedCondition.Create(scenario.Setup, assertion);
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
            // Grouping describes actual explicit launch conditions, not just the source file.
            // Inherited host environment is deliberately not read/persisted (and is not sandboxed).
            var environmentCopy = redactor.RedactDocument(JsonSerializer.SerializeToElement(environmentFile.CopyJson()), artifactLimits);
            var environmentCopyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(environmentCopy)));
            var runtimeConfiguration = EffectiveConfiguration(environmentCopyHash, limits, launch, hash, redactor, artifactLimits, targetId);
            var runtimeHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtimeConfiguration)));
            var identity = new RunIdentity(scenario.ScenarioId, scenario.DefinitionVersion, scenarioFile.Sha256, environment.BuildId, runtimeHash);
            var metadata = new RunArtifactMetadata(environment.Profile.ToLowerInvariant(),
                [new("driver", driverName, "explicit-command"), new("clock", "real", "explicit-command"),
                    new("gameExecutableSha256", hash.ToLowerInvariant(), "locked-image-and-OS-listener-owner"),
                    new("effectiveEnvironmentSha256", runtimeHash, "explicit-runtime-configuration"),
                    new("maxDurationMilliseconds", limits.MaxDurationMilliseconds.ToString(CultureInfo.InvariantCulture), "scenario-environment-minimum")],
                new Dictionary<string, string> { ["Gua.Testing"] = "1.1.2" },
                [new("scenario", JsonSerializer.SerializeToElement(scenarioFile.CopyJson()), scenarioFile.CopyBytes(), Path.GetExtension(scenarioPath)),
                    new("environment", JsonSerializer.SerializeToElement(environmentFile.CopyJson()), environmentFile.CopyBytes(), Path.GetExtension(environmentPath)),
                    new("effective-runtime", runtimeConfiguration)], identity, DateTimeOffset.UtcNow);
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
            using var observationTiming = new ObservationTiming();
            var captureGate = new object();
            bool capturesClosed = false;
            bool capturesDisabled = false;
            long captureSequence = 0;
            GuardedUiReceipt? lastClick = null;
            GuardedClickRecording? recording = null;
            void RecordClick(GuardedUiReceipt receipt)
            {
                lastClick = receipt;
                recording?.ObserveReceipt(receipt);
                lock (captureGate)
                {
                    if (capturesClosed || capturesDisabled) return;
                    capturesDisabled = !CaptureWithoutPrimaryAuthority(() =>
                    {
                        var enqueued = receipt.State == GuaRemoteDispatchState.Enqueued;
                        var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, enqueued ? "Guarded UI click submission" : "Guarded UI click completion");
                        if (string.IsNullOrEmpty(step) || !artifacts.Trace.RecordStructural(step, "runner.ui-click", JsonSerializer.SerializeToElement(receipt)))
                            throw new InvalidOperationException("TraceCapacityReached");
                        artifacts.Trace.EndStep(step, enqueued || receipt.State == GuaRemoteDispatchState.Completed && receipt.Succeeded == true ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed);
                    }, artifacts.MarkCaptureFailure);
                }
            }
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
                            if (capturesClosed || capturesDisabled) return;
                            capturesDisabled = !CaptureWithoutPrimaryAuthority(() =>
                            {
                                var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, "Observation capture");
                                if (string.IsNullOrEmpty(step)) throw new InvalidOperationException("TraceCapacityReached");
                                var observation = artifacts.Observe("capture-" + (++captureSequence).ToString(CultureInfo.InvariantCulture),
                                    ObservationBoundary.Before, step, "gua", "fresh-read", collection.Availability.ToString(),
                                    new(host.SourceId, host.Epoch), JsonSerializer.SerializeToElement(collection), "snapshot");
                                if (string.IsNullOrEmpty(observation)) throw new InvalidOperationException("TraceCapacityReached");
                                artifacts.Trace.EndStep(step, GuaTraceOutcome.Passed);
                            }, artifacts.MarkCaptureFailure);
                        }
                    }, singleClick, precondition) { Timing = observationTiming };
            });
            var cleanup = new OwnedCleanup();
            cleanup.Register(CleanupStage.InputRelease, connector.Pending.ReleaseAsync);
            var preparation = new HostPreparation(new(HostMode.Launch, PlayMode.Explore, endpoint, environment.BuildId, "2", environment.Profile,
                "real", singleClick ? ["observe_v1", "guarded_dispatch_v1"] : ["observe_v1"], true, true, launch, Ms(limits.PrepareTimeoutMilliseconds), TimeSpan.FromMilliseconds(25), 100,
                Ms(limits.CleanupTimeoutMilliseconds)), clock, launcher, connector, artifacts);
            PreparedHost? prepared = null;
            var outcome = await RunExecutor.ExecuteAsync(run, clock, cleanup,
                async (session, owned, cancel) => (prepared = await preparation.PrepareAsync(session, owned, null, new ObserverAvailable(), cancel)).Boundary,
                async (session, cancel) =>
                {
                    if (singleClick)
                        return await SingleUiDriver.ExecuteAsync(session, clock, connector.Connection!, targetId!, RecordClick,
                            () => WindowsBridgeOwner.Require(endpoint, launcher.Process!), token, click =>
                            {
                                var acquired = new GuardedClickRecording(click, targetId!,
                                    text => text.Length <= artifactLimits.MaxStringChars && redactor.Redact(text) == text,
                                    document => redactor.RedactDocument(document, artifactLimits), checked((int)limits.MaxObservationBytes),
                                    artifactLimits.MaxItems, artifactLimits.MaxStringChars, token);
                                try { cleanup.Register(CleanupStage.InputRelease, acquired.ReleaseAsync); }
                                catch { _ = acquired.ReleaseAsync(CancellationToken.None); throw; }
                                return recording = acquired;
                            });
                    await RunMonitor.AwaitAsync(session, clock, clock, prepared!.Feed,
                        // The executor operation token also signals its deadline. Only the
                        // actual caller token may be classified as a user's cancellation.
                        async ct => { await clock.DelayAsync(Ms(limits.MaxDurationMilliseconds), ct); return true; }, _ => [], token);
                    return false;
                }, token, (snapshot, cancel) =>
                {
                    lock (captureGate) { capturesClosed = true; return artifacts.ConfirmPrimaryAsync(snapshot, cancel); }
                });
            lock (captureGate) capturesClosed = true;
            var capturedRecording = recording?.Freeze();
            var completion = await CompletePreservingOutcomeAsync(artifacts, outcome, capturedRecording);
            return await CommandOutput.WriteAsync(output, diagnostics, json, outcome.Primary.Status.ToString(), outcome.Primary.Cause.Reason.ToString(),
                completion.ExitCode(outcome), new { runId = store.RunId, artifactDirectory = store.DirectoryPath,
                    ownedProcessId = launcher.Process?.ProcessId,
                    effectiveEnvironmentSha256 = runtimeHash,
                    driver = driverName, click = lastClick, actions = run.Budget.Snapshot.Actions,
                    observationTiming = observationTiming.Snapshot(),
                    recordingState = completion.Receipts.SingleOrDefault(x => x.Kind == ArtifactKind.Recording)?.State.ToString(),
                    recordedActions = capturedRecording?.Recording?.Steps.Count ?? 0,
                    phase = outcome.Primary.Cause.Phase.ToString(), origin = outcome.Primary.Cause.Origin.ToString(),
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
    internal static bool CaptureWithoutPrimaryAuthority(Action capture, Action failed)
    {
        try { capture(); return true; }
        catch (Exception) { failed(); return false; }
    }
    internal static async Task<GuaArtifactCompletion> CompletePreservingOutcomeAsync(GuaRunArtifacts artifacts, RunOutcome outcome,
        GuardedRecordingCapture? recording = null)
    {
        try { return await artifacts.CompleteAsync(outcome, DateTimeOffset.UtcNow, recording?.Recording, recording?.CaptureFailed ?? false); }
        catch (Exception)
        {
            // A skipped primary hook or failed finalization owes evidence debt. Do not
            // invent a new primary or start another cleanup budget after its deadline.
            return new(new PersistenceResult(PersistenceFailure.InvalidEvidence, true), []);
        }
    }
    internal static JsonElement EffectiveConfiguration(string sourceEnvironmentCopySha256, object limits, LaunchCommand launch,
        string hash, PersistenceRedactor redactor, ArtifactLimits artifactLimits, string? clickTarget = null)
    {
        // This command has no secret-binding input. Credential-bearing launch arguments
        // are outside its lane; values must be non-secret. Do not inspect inherited env.
        if (launch.Arguments.Any(x => x.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("token", StringComparison.OrdinalIgnoreCase) || x.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("secret", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("RuntimeArgumentsInvalid");
        var configuration = JsonSerializer.SerializeToElement(new
        {
            driver = "observation-only", clock = "real", sourceEnvironmentCopySha256, limits,
            executable = launch.Executable, executableSha256 = hash.ToLowerInvariant(),
            workingDirectory = launch.WorkingDirectory, arguments = launch.Arguments,
            childEnvironment = launch.EnvironmentVariables?.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value)
        });
        if (clickTarget is not null)
        {
            var explicitClick = JsonNode.Parse(configuration.GetRawText())!.AsObject();
            explicitClick["driver"] = "single-ui-click"; explicitClick["clickTargetId"] = clickTarget;
            configuration = JsonSerializer.SerializeToElement(explicitClick);
        }
        return redactor.RedactDocument(configuration, artifactLimits);
    }
    private static readonly HashSet<string> Refusals = new(StringComparer.Ordinal)
    {
        "WindowsRuntimeRequired", "RuntimeArgumentsRequired", "RuntimeInputOutsideRoots", "RuntimeInputInvalid",
        "RuntimeExecutableOutsideRoots", "RuntimeWorkingDirectoryOutsideRoots", "RuntimeOutputOutsideRoots", "RuntimeBuildIdentityInvalid",
        "RuntimeBuildHashMismatch", "WindowsLoopbackRequired", "RuntimeChildEnvironmentInvalid", "RuntimeSaveOutsideRoots",
        "RuntimeStorageUnavailable", "ObservationOnlyCapabilityUnavailable", "RuntimeArgumentsInvalid"
    };
    internal static void ValidateLane(ScenarioDocument scenario, EnvironmentDocument environment, bool singleClick = false)
    {
        bool Initial(JsonObject condition) => condition["kind"]?.GetValue<string>() switch
        {
            "assertion" => condition["read"] is JsonObject read && environment.Permissions.Reads.Any(allowed => JsonNode.DeepEquals(allowed, read)),
            "all" or "any" => condition["conditions"] is JsonArray children && children.All(child => child is JsonObject nested && Initial(nested)),
            _ => false
        };
        if (environment.Connection.Credential is not null || (scenario.Setup is not null && (!singleClick || !Initial(scenario.Setup))) || environment.Fixture is not null ||
            environment.Navigation is not null || environment.Profile is not ("Player" or "Debug") || environment.Permissions.RawInput ||
            (singleClick ? !environment.Permissions.ActionIds.SequenceEqual(["click"]) : environment.Permissions.ActionIds.Length != 0) ||
            (singleClick && scenario.Constraints.AllowedActionIds is { } actions && !actions.Contains("click", StringComparer.Ordinal)) ||
            scenario.Goal.Failure is not null || scenario.Goal.Success is not { } success ||
            success["kind"]?.GetValue<string>() != "assertion" || success["read"] is not JsonObject read ||
            read["valueType"]?["type"]?.GetValue<string>() is not ("bool" or "integer" or "number" or "string") ||
            !environment.Permissions.Reads.Any(allowed => JsonNode.DeepEquals(allowed, read)))
            throw new InvalidDataException("ObservationOnlyCapabilityUnavailable");
    }
    private static Dictionary<string, List<string>> Parse(string[] args, bool singleClick)
    {
        var allowed = new List<string> { "--allow-root", "--scenario", "--environment", "--executable", "--executable-sha256", "--working-directory", "--output-root", "--argument", "--child-env" };
        if (singleClick) allowed.Add("--target-id");
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
