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
using Gua.Playtest.Runner.Replay;
using Gua.Playtest.Runner.Planning;

namespace Gua.Playtest.Cli;

/// <summary>Explicit Windows one-click, condition-synchronized Replay/trial composition.</summary>
internal static class ReplayRunCommand
{
    private sealed class Clock : IClock
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        public TimeSpan Elapsed => timer.Elapsed;
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken token) => new(Task.Delay(duration, token));
    }
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
    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter diagnostics, bool json, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new InvalidDataException("WindowsRuntimeRequired");
            var options = Parse(args);
            string One(string name) => options.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : throw new InvalidDataException("RuntimeArgumentsRequired");
            var roots = options.GetValueOrDefault("--allow-root") ?? throw new InvalidDataException("RuntimeArgumentsRequired");
            _ = new AllowedPaths(roots);
            var planPath = StaticPathAuthority.File(One("--plan"), roots, "RuntimeInputOutsideRoots");
            var managedRoot = StaticPathAuthority.Directory(One("--managed-root"), roots, "RuntimeInputOutsideRoots");
            var environmentPath = StaticPathAuthority.File(One("--environment"), roots, "RuntimeInputOutsideRoots");
            var validator = StaticPathAuthority.Validator(roots);
            var planReport = await validator.ValidateFileAsync(planPath, token);
            var environmentReport = await validator.ValidateFileAsync(environmentPath, token);
            token.ThrowIfCancellationRequested();
            if (!planReport.IsValid || !environmentReport.IsValid || planReport.Document is not ReplayPlanDocument plan ||
                environmentReport.Document is not EnvironmentDocument environment) throw new InvalidDataException("RuntimeInputInvalid");
            var planFile = planReport.Files.Values.Single(f => f.CopyJson()["kind"]?.GetValue<string>() == "replayPlan");
            var scenarioFile = planReport.Files.Values.Single(f => f.Sha256 == plan.Scenario.Sha256);
            var recordingFile = planReport.Files.Values.Single(f => f.Sha256 == plan.Recording.Sha256);
            var scenario = scenarioFile.CopyJson().Deserialize<ScenarioDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            ObservationRunCommand.ValidateLane(scenario, environment, singleClick: true);
            if (plan.Timing != "conditionSynchronized" || plan.Completion != "afterPlan" || plan.Initial is not null || plan.Checkpoints.Length != 0)
                throw new InvalidDataException("ReplayLaneUnsupported");
            // Windows share locks bind acceptance checks, retained playback and trial publication
            // to the same three original inputs. No path is reopened for playback.
            using var planLock = LockInput(planFile);
            using var scenarioLock = LockInput(scenarioFile);
            using var recordingLock = LockInput(recordingFile);
            PackageCompatibility.CheckNative();
            var redactor = new PersistenceRedactor([], ["password", "token", "authorization", "secretValue"]);
            if (!File.Exists(Path.Combine(managedRoot, ".plan-authority"))) throw new InvalidDataException("ManagedPlanRequired");
            var manager = new PlanManager(managedRoot, redactor);
            bool accepted = await manager.IsAcceptedAsync(planPath, token);
            if (!accepted && !options.ContainsKey("--allow-candidate")) throw new InvalidDataException("ReplayCandidateNotAccepted");
            var driverName = "guarded-ui-replay";
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
            var replay = await ResolvedReplay.LoadAsync(planPath, roots, assertion, cancellationToken: token);
            if (replay.PlanSha256 != planFile.Sha256 || replay.ScenarioSha256 != scenarioFile.Sha256 || replay.RecordingSha256 != recordingFile.Sha256)
                throw new InvalidDataException("ReplayInputChanged");
            var batch = new ReplayBatch(0, replay.StepCount, replay.CopyRecording().ToJsonString(), replay.Timing, 0,
                Ms(Math.Min(limits.MaxDurationMilliseconds, Math.Min(limits.MaxSegmentMilliseconds, limits.WaitTimeoutMilliseconds))));
            if (!GuaGuardedClickReplay.SupportsRecording(batch))
                throw new InvalidDataException("ReplayLaneUnsupported");
            var clock = new Clock();
            var runLimits = new RunLimits(Ms(limits.MaxDurationMilliseconds), Ms(limits.PrepareTimeoutMilliseconds), Ms(limits.CleanupTimeoutMilliseconds),
                Ms(limits.PlannerTimeoutMilliseconds), Ms(limits.WaitTimeoutMilliseconds), Ms(limits.MaxSegmentMilliseconds),
                limits.MaxActions, limits.MaxDecisions, limits.RecoveryDecisionLimit, checked((int)Math.Min(100000, limits.TraceMaxEvents)));
            // The existing writer reserves a second file plus 64KiB for manifest/summaries.
            // Reduce, never raise the Environment's aggregate ceiling.
            int fileBytes = checked((int)Math.Min(16 * 1024 * 1024, (limits.TraceMaxBytes - 65536) / 2));
            var artifactLimits = new ArtifactLimits(fileBytes, limits.TraceMaxBytes, checked((int)Math.Min(100000, limits.TraceMaxEvents)), Math.Min(65536, fileBytes));
            var store = RunArtifactStore.Create(destination, artifactLimits, redactor);
            var environmentFile = environmentReport.Files.Values.Single();
            // Grouping describes actual explicit launch conditions, not just the source file.
            // Inherited host environment is deliberately not read/persisted (and is not sandboxed).
            var environmentCopy = redactor.RedactDocument(JsonSerializer.SerializeToElement(environmentFile.CopyJson()), artifactLimits);
            var environmentCopyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(environmentCopy)));
            var runtimeConfiguration = ObservationRunCommand.EffectiveConfiguration(environmentCopyHash, limits, launch, hash, redactor, artifactLimits);
            var configuration = JsonNode.Parse(runtimeConfiguration.GetRawText())!.AsObject();
            configuration["driver"] = driverName; configuration["planSha256"] = replay.PlanSha256; configuration["recordingSha256"] = replay.RecordingSha256;
            runtimeConfiguration = JsonSerializer.SerializeToElement(configuration);
            var runtimeHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtimeConfiguration)));
            var identity = new RunIdentity(scenario.ScenarioId, scenario.DefinitionVersion, scenarioFile.Sha256, environment.BuildId, runtimeHash);
            var metadata = new RunArtifactMetadata(environment.Profile.ToLowerInvariant(),
                [new("driver", driverName, "explicit-command"), new("clock", "real", "explicit-command"),
                    new("gameExecutableSha256", hash.ToLowerInvariant(), "locked-image-and-OS-listener-owner"),
                    new("effectiveEnvironmentSha256", runtimeHash, "explicit-runtime-configuration"),
                    new("maxDurationMilliseconds", limits.MaxDurationMilliseconds.ToString(CultureInfo.InvariantCulture), "scenario-environment-minimum")],
                new Dictionary<string, string> { ["Gua.Testing"] = "1.2.0" },
                [new("scenario", JsonSerializer.SerializeToElement(scenarioFile.CopyJson()), scenarioFile.CopyBytes(), Path.GetExtension(scenarioFile.Path)),
                    new("environment", JsonSerializer.SerializeToElement(environmentFile.CopyJson()), environmentFile.CopyBytes(), Path.GetExtension(environmentPath)),
                    new("plan", JsonSerializer.SerializeToElement(planFile.CopyJson()), planFile.CopyBytes(), Path.GetExtension(planPath)),
                    new("recording-input", JsonSerializer.SerializeToElement(recordingFile.CopyJson()), recordingFile.CopyBytes(), Path.GetExtension(recordingFile.Path)),
                    new("effective-runtime", runtimeConfiguration)], identity, DateTimeOffset.UtcNow);
            await using var artifacts = GuaRunArtifacts.CreateReplay(store, metadata, artifactLimits, new GuaTraceOptions
            {
                CaptureMode = environment.Trace.CaptureMode == "recent" ? GuaTraceCaptureMode.Recent : GuaTraceCaptureMode.Streaming,
                SavePolicy = environment.Trace.SavePolicy == "always" ? GuaTraceSavePolicy.Always : GuaTraceSavePolicy.OnFailure,
                Profile = environment.Profile.ToLowerInvariant(), MaxSteps = checked((int)limits.TraceRecentSteps),
                MaxMemoryBytes = fileBytes, MaxQueueItems = checked((int)limits.TraceQueueCapacity), MaxArtifactBytes = fileBytes,
                MaxAttachmentBytes = checked((int)Math.Min(limits.TraceMaxAttachmentBytes, fileBytes)), MaxEventBytes = checked((int)Math.Min(limits.MaxObservationBytes, fileBytes)),
                FlushTimeout = Ms(limits.CleanupTimeoutMilliseconds)
            }, redactor, identity, token);
            if (!artifacts.PreparationPersistence.Saved) throw new InvalidDataException("RuntimeStorageUnavailable");
            long timingOrigin = Stopwatch.GetTimestamp();
            using var observationTiming = new ObservationTiming(origin: timingOrigin);
            LifecycleTiming? lifecycleTiming = null;
            var captureGate = new object();
            bool capturesClosed = false;
            bool capturesDisabled = false;
            long captureSequence = 0;
            GuardedUiReceipt? lastClick = null;
            void RecordClick(GuardedUiReceipt receipt)
            {
                lastClick = receipt;
                lock (captureGate)
                {
                    if (capturesClosed || capturesDisabled) return;
                    capturesDisabled = !ObservationRunCommand.CaptureWithoutPrimaryAuthority(() =>
                    {
                        var enqueued = receipt.State == GuaRemoteDispatchState.Enqueued;
                        var step = artifacts.Trace.BeginStep(GuaTraceStepKind.Mark, enqueued ? "Guarded UI click submission" : "Guarded UI click completion");
                        if (string.IsNullOrEmpty(step) || !artifacts.Trace.RecordStructural(step, "runner.ui-replay", JsonSerializer.SerializeToElement(new
                            { beforeStep = 0, planSha256 = replay.PlanSha256, recordingSha256 = replay.RecordingSha256, receipt })))
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
                            capturesDisabled = !ObservationRunCommand.CaptureWithoutPrimaryAuthority(() =>
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
                    }, true, precondition) { Timing = observationTiming };
            });
            var cleanup = new OwnedCleanup();
            cleanup.Register(CleanupStage.InputRelease, connector.Pending.ReleaseAsync);
            var preparation = new HostPreparation(new(HostMode.Launch, PlayMode.Replay, endpoint, environment.BuildId, "2", environment.Profile,
                "real", ["observe_v1", "guarded_dispatch_v1"], true, true, launch, Ms(limits.PrepareTimeoutMilliseconds), TimeSpan.FromMilliseconds(25), 100,
                Ms(limits.CleanupTimeoutMilliseconds)), clock, launcher, connector, artifacts);
            var playback = new GuaGuardedClickReplay(() => connector.Connection?.Click ?? throw new InvalidOperationException("PreparedGuardRequired"),
                () => WindowsBridgeOwner.Require(endpoint, launcher.Process!), RecordClick, TimeSpan.FromMilliseconds(25));
            ReplayOutcome outcome;
            try
            {
                outcome = await new ReplayDriver().ExecuteAsync(replay, runLimits, clock, clock, cleanup, playback,
                    async (session, owned, cancel) =>
                    {
                        lifecycleTiming = new LifecycleTiming(session, store, timingOrigin);
                        var prepared = await preparation.PrepareAsync(session, owned, null, null, cancel);
                        return new ReplayPreparedHost(prepared with { Feed = new Feed(prepared.Feed) }, null);
                    }, token, (snapshot, cancel) =>
                    {
                        observationTiming.ObservePrimary();
                        lock (captureGate) { capturesClosed = true; return artifacts.ConfirmPrimaryAsync(snapshot, cancel); }
                    }, trialStore: store);
            }
            finally { lifecycleTiming?.Dispose(); }
            lock (captureGate) capturesClosed = true;
            GuaArtifactCompletion completion;
            try { completion = await artifacts.CompleteReplayAsync(outcome, DateTimeOffset.UtcNow); }
            catch { completion = new(new PersistenceResult(PersistenceFailure.InvalidEvidence, true), []); }
            string? trialId = null;
            var trialState = "NotEligible";
            if (outcome.Run.Primary.Status == ResultStatus.Passed && outcome.Run.PostProcessingComplete && completion.Persistence.Saved &&
                !completion.Persistence.PostProcessingIncomplete && outcome.Progress.PlanCompleted)
            {
                try { trialId = await manager.RecordTrialAsync(planPath, store, outcome, identity, token); trialState = "Saved"; }
                catch { trialState = "SaveFailed"; }
            }
            var exit = completion.ExitCode(outcome.Run);
            if (exit == 0 && trialState != "Saved") exit = 11;
            return await CommandOutput.WriteAsync(output, diagnostics, json, outcome.Run.Primary.Status.ToString(), outcome.Run.Primary.Cause.Reason.ToString(), exit,
                new { runId = store.RunId, artifactDirectory = store.DirectoryPath, ownedProcessId = launcher.Process?.ProcessId,
                    effectiveEnvironmentSha256 = runtimeHash, driver = driverName, click = lastClick,
                    progress = outcome.Progress, planSha256 = replay.PlanSha256, scenarioSha256 = replay.ScenarioSha256, recordingSha256 = replay.RecordingSha256,
                    trialId, trialState, acceptedAtStart = accepted, observationTiming = observationTiming.Snapshot(),
                    lifecycleTiming = lifecycleTiming?.Snapshot(),
                    postProcessingComplete = exit == 0 || outcome.Run.PostProcessingComplete && completion.Persistence.Saved &&
                        !completion.Persistence.PostProcessingIncomplete && trialState != "SaveFailed",
                    persistence = completion.Persistence.Failure.ToString() });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Interrupted", "CommandInterrupted", 4); }
        catch (InvalidDataException error) when (Refusals.Contains(error.Message))
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", error.Message, 2); }
        catch (Exception)
        { return await CommandOutput.WriteAsync(output, diagnostics, json, "Invalid", "ReplayRuntimeRejected", 2); }
    }
    private static FileStream LockInput(ValidatedFile file)
    {
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > 4 * 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256)
                throw new InvalidDataException("ReplayInputChanged");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private sealed class Feed(IRunObservationFeed inner) : IReplayObservationFeed
    {
        public ValueTask<RunObservation> CaptureAsync(CancellationToken token) => inner.CaptureAsync(token);
        public ValueTask WaitForChangeAsync(CancellationToken token) => inner.WaitForChangeAsync(token);
        public ValueTask<ReplayObservation> CaptureAsync(PreparedCondition checkpoint, CancellationToken token)
            => ValueTask.FromException<ReplayObservation>(new NotSupportedException("ReplayCheckpointsUnsupported"));
    }
    private static readonly HashSet<string> Refusals = new(StringComparer.Ordinal)
    {
        "WindowsRuntimeRequired", "RuntimeArgumentsRequired", "RuntimeInputOutsideRoots", "RuntimeInputInvalid",
        "RuntimeExecutableOutsideRoots", "RuntimeWorkingDirectoryOutsideRoots", "RuntimeOutputOutsideRoots", "RuntimeBuildIdentityInvalid",
        "RuntimeBuildHashMismatch", "WindowsLoopbackRequired", "RuntimeChildEnvironmentInvalid", "RuntimeSaveOutsideRoots",
        "RuntimeStorageUnavailable", "ObservationOnlyCapabilityUnavailable", "RuntimeArgumentsInvalid",
        "ReplayLaneUnsupported", "ReplayInputChanged", "ManagedPlanRequired", "ReplayCandidateNotAccepted"
    };
    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var allowed = new List<string> { "--allow-root", "--plan", "--managed-root", "--allow-candidate", "--environment", "--executable", "--executable-sha256", "--working-directory", "--output-root", "--argument", "--child-env" };
        if (args.Length > 256) throw new InvalidDataException("RuntimeArgumentsRequired");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (!allowed.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("RuntimeArgumentsInvalid");
            var value = name == "--allow-candidate" ? "true" : ++i < args.Length ? args[i] : throw new InvalidDataException("RuntimeArgumentsRequired");
            if (value.Length > 4096 || value.Contains('\0'))
                throw new InvalidDataException("RuntimeArgumentsInvalid");
            if (!result.TryGetValue(name, out var values)) result.Add(name, values = []);
            if (values.Count != 0 && name is not ("--allow-root" or "--argument" or "--child-env")) throw new InvalidDataException("RuntimeArgumentsInvalid");
            values.Add(value);
        }
        return result;
    }
}
