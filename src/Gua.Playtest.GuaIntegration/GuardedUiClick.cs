using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

public sealed record GuardedUiReceipt(GuaRemoteDispatchState State, bool DispatchBegan, ulong? RequestId,
    bool? Succeeded, string Reason, string SourceId, ulong SessionEpoch, string Profile, ulong Revision, string? NodeId = null,
    int? GuaErrorCode = null, string? DispatchError = null);

/// <summary>One explicit UI click, using the published 1.1.2 remote atomic guard.
/// Borrows the observation context and owns a separate guarded connection. Never retries an attempt.</summary>
public sealed class GuardedUiClick : IDisposable
{
    private readonly GuaWebSocketContext observation;
    private readonly GuaRemoteGuardedSession dispatch;
    private readonly GuaObservationProfile profile;
    private readonly object gate = new();
    private GuaRemoteDispatchAttempt? attempt;
    private GuardedUiReceipt? receipt;
    private bool disposed;
    internal GuardedUiClick(GuaWebSocketContext observation, GuaObservationProfile profile, string sourceId)
    {
        this.observation = observation; this.profile = profile;
        // Public factory verifies guarded_dispatch_v1 and source identity on both connections.
        dispatch = observation.CreateGuardedDispatchSession(sourceId);
    }
    public string SourceId => dispatch.SourceId;
    public GuardedUiReceipt? Receipt { get { lock (gate) return receipt; } }

    public GuardedUiReceipt Click(string targetId, Action beforeDispatch, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (targetId.Length > 128 || targetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw new ArgumentException("Explicit UI ID required", nameof(targetId));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (receipt is not null) return receipt;
            receipt = new(GuaRemoteDispatchState.Rejected, false, null, null, "preflight-rejected", SourceId, 0, profile.ToString(), 0);
            token.ThrowIfCancellationRequested();
            var status = observation.GetContextStatus();
            using var snapshot = JsonDocument.Parse(observation.GetObserveSnapshotJson());
            var document = snapshot.RootElement.GetProperty("document");
            if (document.GetProperty("sourceId").GetString() != SourceId ||
                document.GetProperty("sessionEpoch").GetUInt64() != status.SessionEpoch ||
                !string.Equals(document.GetProperty("profile").GetString(), profile.ToString(), StringComparison.OrdinalIgnoreCase))
                return receipt = receipt with { Reason = "identity-mismatch" };
            var selector = new JsonObject { ["id"] = new JsonObject { ["value"] = targetId } };
            var matches = observation.Query(BridgeSelectors.Ui(selector));
            if (!matches.Valid || matches.Matches.Count != 1) return receipt = receipt with { Reason = "target-unavailable" };
            using var tree = JsonDocument.Parse(observation.GetUiTreeJson());
            if (!GuaDistribution.ValidateJson("ui-tree.schema.json", tree.RootElement.GetRawText()) ||
                !BridgeSelectors.UiMatchesTree(selector, tree.RootElement, matches.Matches.Select(x => x.Id)))
                return receipt = receipt with { Reason = "target-unconfirmed" };
            var after = observation.GetContextStatus();
            var revision = tree.RootElement.GetProperty("revision").GetUInt64();
            if (revision != status.Revision || after.Revision != revision || status.SessionEpoch != after.SessionEpoch ||
                tree.RootElement.TryGetProperty("sessionEpoch", out var epoch) && epoch.GetUInt64() != status.SessionEpoch)
                return receipt = receipt with { Reason = "stale-target" };
            var guard = new GuaDispatchGuard(status.SessionEpoch, profile, revision, SourceId);
            receipt = receipt with { SessionEpoch = guard.SessionEpoch, Revision = revision, NodeId = matches.Matches[0].Id };
            attempt = dispatch.SendUi(guard, new GuaActionRequest(GuaActionType.Click, NodeId: matches.Matches[0].Id), () =>
            {
                token.ThrowIfCancellationRequested();
                beforeDispatch();
                receipt = receipt with { DispatchBegan = true };
            });
            return receipt = Snapshot();
        }
    }
    public GuardedUiReceipt Poll()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attempt is not null && attempt.State == GuaRemoteDispatchState.Enqueued) dispatch.Poll(attempt);
            return receipt = Snapshot();
        }
    }
    internal T ReadForRecording<T>(Func<IGuaContext, T> read)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return read(observation);
        }
    }
    internal bool TryRecordingEvent(ulong requestId, out GuaActionEvent result)
    {
        lock (gate)
        {
            // A real correlated completion remains immutable after owned disconnect.
            // Only the execution owner polls the transport. Artifact continuations
            // consume retained evidence without adding I/O or stealing its window.
            if (attempt?.Completion is null)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            if (attempt?.State == GuaRemoteDispatchState.Uncertain)
                throw new InvalidOperationException("RecordingCompletionUnconfirmed");
            if (attempt?.Completion is not { } completion) { result = default; return false; }
            result = JsonSerializer.Deserialize<GuaActionEvent>(completion.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var expected = receipt ?? throw new InvalidOperationException("RecordingReceiptMissing");
            if (result.RequestId != requestId || result.SessionEpoch != expected.SessionEpoch || result.NodeId != expected.NodeId ||
                result.Action != GuaActionType.Click || !result.Revision.HasValue)
                throw new InvalidOperationException("RecordingCompletionMismatch");
            return true;
        }
    }
    private GuardedUiReceipt Snapshot()
    {
        if (attempt is null) return receipt ?? throw new InvalidOperationException("NoClickAttempt");
        return receipt! with { State = attempt.State, RequestId = attempt.RequestId,
            Succeeded = attempt.Completion is { } completed ? completed.GetProperty("succeeded").GetBoolean() : null,
            GuaErrorCode = attempt.Completion is { } completion ? completion.GetProperty("error").GetInt32() : null,
            DispatchError = SafeCode(attempt.Error),
            Reason = attempt.State switch { GuaRemoteDispatchState.Enqueued => "enqueued",
                GuaRemoteDispatchState.Completed => "host-completed", GuaRemoteDispatchState.Uncertain => "unconfirmed", _ => "host-rejected" } };
    }
    private static string? SafeCode(string? value) => value is null ? null : value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.') ? value : "invalid-error-code";
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; dispatch.Dispose(); } }
}
