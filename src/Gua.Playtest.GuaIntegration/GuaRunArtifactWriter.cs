using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Testing;
using Gua.Testing.Recording;

namespace Gua.Playtest.GuaIntegration;

public enum GuaArtifactState { NotExecuted, OmittedOnSuccess, CaptureFailed, SaveFailed, Saved }
public sealed record GuaArtifactFile(string Name, long Bytes, string Sha256);
public sealed record GuaArtifactBundle(GuaArtifactState State, GuaArtifactFile? Entry = null,
    IReadOnlyList<GuaArtifactFile>? AdditionalFiles = null);
public sealed record GuaRunArtifactBundles(GuaArtifactBundle Trace, GuaArtifactBundle Recording);

/// <summary>Gua owns every saved format. This adapter inventories completed output only;
/// it does not acquire a context, dispatch actions, or certify production host isolation.
/// CompleteAsync/DisposeAsync retain 1.1.1 scheduling and synchronous filesystem limitations.</summary>
public sealed class GuaRunArtifactWriter : IAsyncDisposable
{
    private readonly string runDirectory;
    private readonly string stagingDirectory, stagingParent;
    private readonly GuaTraceSavePolicy policy;
    private readonly string profile;
    private readonly int maxFileBytes, maxFiles, maxStringChars, maxJsonNodes;
    private readonly long maxBundleBytes;
    private readonly Func<string, bool> safeRecordingText, sensitiveField;
    private readonly Func<ReadOnlyMemory<byte>, bool> safeRecordingEncoding;
    private GuaTraceOutcome? primary;
    private bool completed, unsafeRecording, flushedSuccessfully;
    public GuaRunTrace Trace { get; }

    public GuaRunArtifactWriter(string runDirectory, GuaTraceOptions options,
        int maxFileBytes, long maxBundleBytes, int maxFiles, Func<string, bool> safeRecordingText,
        Func<ReadOnlyMemory<byte>, bool> safeRecordingEncoding, Func<JsonElement, JsonElement> redact,
        Func<string, bool> sensitiveField, Func<string, string> redactText, int maxStringChars, int maxJsonNodes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(safeRecordingText);
        ArgumentNullException.ThrowIfNull(safeRecordingEncoding);
        ArgumentNullException.ThrowIfNull(redact); ArgumentNullException.ThrowIfNull(sensitiveField);
        ArgumentNullException.ThrowIfNull(redactText);
        // 1.1.1 bounds aggregate events/blobs, not each events.jsonl independently.
        // Its artifact budget must fit one file; its separate manifest reserve must too.
        if (maxFileBytes < 65536 || maxBundleBytes < maxFileBytes || maxFiles < 3 ||
            maxStringChars < 1 || maxStringChars > maxFileBytes || maxJsonNodes < 1 ||
            options.MaxArtifactBytes > maxFileBytes ||
            options.MaxArtifactBytes > maxBundleBytes - 65536L - maxFileBytes ||
            options.MaxAttachmentBytes > maxFileBytes || options.MaxEventBytes > maxFileBytes)
            throw new ArgumentException("ArtifactWriterLimitsInvalid");
        this.runDirectory = Path.GetFullPath(runDirectory);
        this.maxFileBytes = maxFileBytes; this.maxBundleBytes = maxBundleBytes; this.maxFiles = maxFiles;
        this.maxStringChars = maxStringChars;
        this.maxJsonNodes = maxJsonNodes;
        this.safeRecordingText = safeRecordingText;
        this.sensitiveField = sensitiveField;
        this.safeRecordingEncoding = safeRecordingEncoding;
        policy = options.SavePolicy; profile = options.Profile;
        // The supplied parent is the trusted private artifact root. Gua writes its
        // native formats in an isolated, bounded pending directory; publication into
        // the Run is admitted by its summary owner after actual native completion.
        Directory.CreateDirectory(this.runDirectory); CheckNoLinks(this.runDirectory);
        stagingParent = Path.GetDirectoryName(this.runDirectory)!;
        stagingDirectory = Path.Combine(stagingParent, ".gua-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory); CheckNoLinks(stagingDirectory);
        try
        {
            Trace = new GuaRunTrace(new GuaTraceSession(new GuaTraceOptions
            {
                OutputDirectory = Path.Combine(stagingDirectory, "trace"), CaptureMode = options.CaptureMode,
                SavePolicy = options.SavePolicy, Profile = options.Profile, Secrets = options.Secrets.ToArray(),
                MaxSteps = options.MaxSteps, MaxMemoryBytes = options.MaxMemoryBytes,
                MaxQueueItems = options.MaxQueueItems, MaxArtifactBytes = options.MaxArtifactBytes,
                MaxAttachmentBytes = options.MaxAttachmentBytes, MaxEventBytes = options.MaxEventBytes,
                FlushTimeout = options.FlushTimeout
            }), redact, sensitiveField, redactText, maxStringChars, maxFileBytes, maxJsonNodes);
        }
        catch
        {
            // Native option validation can reject before an instance is returned.
            // No caller can Dispose that failed construction, so release our staging.
            RemoveStaging();
            throw;
        }
    }

    public bool ConfirmPrimary(GuaTraceOutcome outcome)
    {
        if (primary.HasValue || !Trace.Session.SetPrimaryOutcome(outcome)) return false;
        primary = outcome; return true;
    }

    /// <summary>Recording must come from the authorized execution owner after capture has joined.
    /// Null means not executed. Capture failure is explicit, never an empty substitute Recording.</summary>
    public async Task<GuaRunArtifactBundles> CompleteAsync(GuaRecording? recording = null, bool recordingCaptureFailed = false,
        Func<GuaRunArtifactBundles, bool>? admitPublication = null)
    {
        var bundles = await CaptureAsync(recording, recordingCaptureFailed).ConfigureAwait(false);
        try
        {
            if (admitPublication is not null && !admitPublication(bundles))
                return new(Refused(bundles.Trace), Refused(bundles.Recording));
            return new(Publish(bundles.Trace, "trace", true), Publish(bundles.Recording, "recording", false));
        }
        finally
        {
            // A successfully completed native checkpoint has closed capture and
            // finished all admitted output writes. This does not claim worker/thread
            // termination. Unjoined/failed native providers retain their isolated path.
            if (flushedSuccessfully && bundles.Trace.State is GuaArtifactState.Saved or GuaArtifactState.OmittedOnSuccess)
                RemoveStaging();
        }
        static GuaArtifactBundle Refused(GuaArtifactBundle bundle) => bundle.State == GuaArtifactState.Saved
            ? new(GuaArtifactState.SaveFailed) : bundle;
    }
    private GuaArtifactBundle Publish(GuaArtifactBundle bundle, string name, bool trace)
    {
        if (bundle.State != GuaArtifactState.Saved) return bundle;
        try
        {
            var source = Path.Combine(stagingDirectory, name);
            var destination = Path.Combine(runDirectory, name);
            CheckNoLinks(runDirectory); CheckNoLinks(source);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("ArtifactOutputExists");
            Directory.Move(source, destination);
            if (trace) Trace.Published(Path.Combine(destination, Trace.TraceId));
            return bundle;
        }
        catch { return new(GuaArtifactState.SaveFailed); }
    }
    private void RemoveStaging()
    {
        try
        {
            var path = Path.GetFullPath(stagingDirectory);
            if (Path.GetDirectoryName(path) != stagingParent || !Path.GetFileName(path).StartsWith(".gua-pending-", StringComparison.Ordinal))
                throw new IOException("ArtifactStagingPathInvalid");
            if (!Directory.Exists(path)) return;
            CheckNoLinks(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) CheckNoLinks(entry);
            Directory.Delete(path, recursive: true);
        }
        catch { /* Preserve an isolated pending directory rather than claim provider termination. */ }
    }
    private async Task<GuaRunArtifactBundles> CaptureAsync(GuaRecording? recording, bool recordingCaptureFailed)
    {
        if (completed || primary is null) throw new InvalidOperationException("ArtifactWriterStateInvalid");
        completed = true;
        var recordingBundle = SaveRecording(recording, recordingCaptureFailed);
        bool flushed;
        try { flushed = await Trace.Session.CompleteAsync(primary.Value).ConfigureAwait(false); }
        catch { return new(new(GuaArtifactState.SaveFailed), recordingBundle); }
        var status = Trace.Status;
        flushedSuccessfully = flushed;
        if (!flushed || status.Issues.Any(x => x is "write-failed" or "flush-timeout" or "flush-failed" or "flush-pending"))
            return new(new(GuaArtifactState.SaveFailed), recordingBundle);
        if (unsafeRecording || Trace.CaptureFailed || status.DetailStopped || status.DroppedEvents != 0 || status.Issues.Any(x =>
            x is "capture-failed" or "tree-failed" or "attachment-failed" or "native-capture-failed"))
            return new(new(GuaArtifactState.CaptureFailed), recordingBundle);
        if (primary == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure &&
            status.Issues.Count == 0 && !Directory.Exists(Trace.ArtifactPath))
            return new(new(GuaArtifactState.OmittedOnSuccess), recordingBundle);
        try
        {
            // Validate the actual Gua-owned manifest/event/blob association before issuing a receipt.
            var read = GuaTraceReader.Read(Trace.ArtifactPath, maxBundleBytes, maxJsonNodes, maxFileBytes);
            if (!read.Manifest.Finalized || read.Issues.Any(x => x is "reader-limit" or "invalid-record" or
                "incomplete-tail" or "sequence-incomplete" or "blob-hash-mismatch" or "blob-unavailable" or "invalid-blob-reference"))
                return new(new(GuaArtifactState.SaveFailed), recordingBundle);
            // Existing saved Dashboard consumes a Gua HTML entry. Retain the manifest,
            // events and every attachment in AdditionalFiles, without changing their bytes.
            var recordingFiles = recordingBundle.State == GuaArtifactState.Saved
                ? 1 + (recordingBundle.AdditionalFiles?.Count ?? 0) : 0;
            var manifestBundle = Inventory(Trace.ArtifactPath, Path.Combine(Trace.ArtifactPath, "manifest.json"), reserve: 1 + recordingFiles);
            var existingBytes = manifestBundle.Entry!.Bytes + manifestBundle.AdditionalFiles!.Sum(x => x.Bytes);
            var recordingBytes = (recordingBundle.Entry?.Bytes ?? 0) + (recordingBundle.AdditionalFiles?.Sum(x => x.Bytes) ?? 0);
            var remaining = maxBundleBytes - existingBytes - recordingBytes;
            if (remaining <= 0) return new(new(GuaArtifactState.SaveFailed), recordingBundle);
            // Gua owns HTML/CSP/escaping and bounds its output before returning bytes.
            // The exact reader snapshot is rendered once; no second read or renderer.
            var rendered = GuaTraceReport.RenderHtml(read, checked((int)Math.Min(maxFileBytes, remaining)));
            if (!rendered.Succeeded || rendered.Utf8Html is null)
                return new(new(GuaArtifactState.SaveFailed), recordingBundle);
            var reportPath = Path.Combine(Trace.ArtifactPath, "report.html");
            using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(rendered.Utf8Html); file.Flush(flushToDisk: true); }
            return new(Inventory(Trace.ArtifactPath, reportPath, reserve: recordingFiles), recordingBundle);
        }
        catch { return new(new(GuaArtifactState.SaveFailed), recordingBundle); }
    }

    private GuaArtifactBundle SaveRecording(GuaRecording? recording, bool captureFailed)
    {
        if (captureFailed) return new(GuaArtifactState.CaptureFailed);
        if (recording is null) return new(GuaArtifactState.NotExecuted);
        // Saved Recording contributes its two native attachments and recording.json
        // to the same Run file ceiling as manifest/events/report. Refuse an impossible
        // minimum before attachment or file creation; success omission writes neither.
        if (!(primary == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure) && maxFiles < 6)
            return new(GuaArtifactState.SaveFailed);
        try
        {
            if (recording.Steps.Count > maxFiles) return new(GuaArtifactState.CaptureFailed);
            recording = recording with { Steps = recording.Steps.ToArray() };
            // GuaRecordingFile has no literal-secret redactor. Reject unsafe fields before its
            // serializer buffers or hashes, preserving replay identity rather than rewriting it.
            foreach (var step in recording.Steps)
                foreach (var text in new[] { step.Target?.Id, step.Target?.Role, step.Target?.Name,
                    step.Target?.Scope, step.WaitCondition, step.Value, step.SecretKey })
                    if (text is not null && (text.Length > maxStringChars || !safeRecordingText(text)))
                    { unsafeRecording = true; return new(GuaArtifactState.CaptureFailed); }
            // Named-sensitive real inputs must be refused before admission encoding,
            // native attachments or Recording buffers. Fixed schema/action spelling
            // and Gua-generated encoding are outside this data policy.
            foreach (var step in recording.Steps)
            {
                if (sensitiveField("steps") || step.Target is not null && sensitiveField("target") ||
                    step.CoordinateFallback is not null && (sensitiveField("coordinateFallback") || sensitiveField("x") || sensitiveField("y")))
                { unsafeRecording = true; return new(GuaArtifactState.CaptureFailed); }
                foreach (var (name, value) in new (string, object?)[] {
                    ("id", step.Target?.Id), ("role", step.Target?.Role), ("name", step.Target?.Name), ("scope", step.Target?.Scope),
                    ("currentFocus", step.Target?.CurrentFocus == true ? true : null), ("value", step.Value), ("waitCondition", step.WaitCondition), ("secretKey", step.SecretKey),
                    ("relativeMilliseconds", step.RelativeMilliseconds), ("preRevision", step.PreRevision), ("postRevision", step.PostRevision),
                    ("requestId", step.RequestId), ("eventId", step.EventId), ("deltaX", step.DeltaX), ("deltaY", step.DeltaY),
                    ("scrollUnit", step.ScrollUnit), ("modifiers", step.Modifiers), ("x", step.CoordinateFallback?.X), ("y", step.CoordinateFallback?.Y) })
                    if (value is not null && (sensitiveField(name) || value is not string && !safeRecordingText(JsonSerializer.Serialize(value))))
                    { unsafeRecording = true; return new(GuaArtifactState.CaptureFailed); }
            }
            GuaRecordingFile.Validate(recording);
            // Count safe serialization before Gua's writer, including escape expansion.
            // Use Gua's public model/converters and pinned 1.1.1 file encoding options
            // only for admission. GuaRecordingFile remains the sole disk writer.
            // This encoding admits resource usage only. Public schema/enum/scalar
            // spelling is owned by Gua; actual text values were checked above.
            using var encoding = new BoundedEncoding(maxFileBytes);
            JsonSerializer.Serialize(encoding, recording, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            GuaJsonAdmission.Validate(encoding.Bytes.Span, maxJsonNodes, maxStringChars);
            if (!safeRecordingEncoding(encoding.Bytes))
            { unsafeRecording = true; return new(GuaArtifactState.CaptureFailed); }
            var association = Trace.BeginStep(GuaTraceStepKind.Mark, "Recording association");
            var attached = GuaRecordingTrace.Attach(Trace.Session, association, recording,
                profile == "player" ? Gua.Core.GuaObservationProfile.Player : Gua.Core.GuaObservationProfile.Debug);
            Trace.EndStep(association, attached ? GuaTraceOutcome.Passed : GuaTraceOutcome.Unknown);
            if (!attached) return new(GuaArtifactState.CaptureFailed);
            if (primary == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure)
                return new(GuaArtifactState.OmittedOnSuccess);
        }
        catch { return new(GuaArtifactState.CaptureFailed); }
        var directory = Path.Combine(stagingDirectory, "recording");
        var path = Path.Combine(directory, "recording.json");
        try
        {
            CheckNoLinks(stagingDirectory);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("RecordingOutputExists");
            Directory.CreateDirectory(directory);
            GuaRecordingFile.Save(path, recording);
            // Actual Gua reader, not a Playtest copy of the Recording schema.
            GuaRecordingFile.Load(path);
            return Inventory(directory, path);
        }
        catch { return new(GuaArtifactState.SaveFailed); }
    }

    private GuaArtifactBundle Inventory(string directory, string entry, int reserve = 0)
    {
        var files = new List<GuaArtifactFile>(); long bytes = 0;
        Visit(directory);
        var entryName = Path.GetRelativePath(stagingDirectory, entry).Replace('\\', '/');
        var main = files.Single(x => x.Name == entryName);
        return new(GuaArtifactState.Saved, main, files.Where(x => x != main).ToArray());
        void Visit(string path)
        {
            CheckNoLinks(path);
            foreach (var item in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                CheckNoLinks(item);
                if (Directory.Exists(item)) { Visit(item); continue; }
                if (files.Count == maxFiles - reserve) throw new IOException("ArtifactFileLimit");
                using var stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length <= 0 || stream.Length > maxFileBytes || (bytes += stream.Length) > maxBundleBytes)
                    throw new IOException("ArtifactByteLimit");
                files.Add(new(Path.GetRelativePath(stagingDirectory, item).Replace('\\', '/'), stream.Length,
                    Convert.ToHexStringLower(SHA256.HashData(stream))));
            }
        }
    }

    private static void CheckNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("ArtifactLink");
    }
    public ValueTask DisposeAsync() => Trace.Session.DisposeAsync();

    private sealed class BoundedEncoding(int limit) : Stream
    {
        private readonly MemoryStream memory = new();
        internal ReadOnlyMemory<byte> Bytes => memory.GetBuffer().AsMemory(0, checked((int)memory.Length));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => memory.Length;
        public override long Position { get => memory.Length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int length) => Write(buffer.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (buffer.Length > limit - memory.Length) throw new IOException("RecordingByteLimit"); memory.Write(buffer); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) memory.Dispose(); base.Dispose(disposing); }
    }
}
