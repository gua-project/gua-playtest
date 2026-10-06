using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing;

namespace Gua.Playtest.GuaIntegration;

public enum ReadAvailability { Available, Unavailable, Truncated, Stale, Gap }
public sealed record ObservationIdentity(string SourceId, ulong SessionEpoch, string Profile,
    string Source, string RuntimeId, ulong Revision, ulong FrameSequence,
    ulong? OwnerId = null, ulong? RegistrationId = null, string? ValueTypeIdentity = null);
public sealed record BridgeRead(ReadAvailability Availability, string Reason,
    ObservationIdentity? Identity = null, JsonElement? Value = null, JsonElement? EnumCatalog = null);
public sealed record BridgeChange(JsonElement Event, JsonElement Catalogs);
public sealed record BridgeReadCollection(DateTimeOffset ObservedAt, ReadAvailability Availability,
    IReadOnlyList<BridgeRead> Reads, IReadOnlyList<BridgeChange>? Changes = null);

/// <summary>A connection and cursor owned by one Run. Construct a separate instance against
/// the host's Player/PublicAgent bridge for Planner reads. A caller-supplied profile cannot
/// elevate a bridge. Every read resolves the original Gua selector again; no Runtime ID cache.</summary>
public sealed class BridgeObservations : IDisposable
{
    private readonly GuaWebSocketContext context;
    private readonly GuaObservationProfile profile;
    private readonly int maxNodes, maxBytes;
    private GuaRemoteObserveSubscription? subscription;
    private string? cursorSourceId;
    private ulong cursorEpoch;
    private bool disposed;
    private readonly object gate = new();

    public BridgeObservations(string endpoint, GuaObservationProfile profile, TimeSpan requestTimeout,
        int maxNodes, int maxBytes)
    {
        if (profile is not (GuaObservationProfile.Debug or GuaObservationProfile.Player) ||
            requestTimeout <= TimeSpan.Zero || maxNodes <= 0 || maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        this.profile = profile; this.maxNodes = maxNodes; this.maxBytes = maxBytes;
        context = new GuaWebSocketContext(endpoint, requestTimeout);
    }

    /// <summary>Explicitly establishes a new snapshot/cursor boundary. Lost history is never
    /// restored by this call; temporal consumers must reset their continuity state.</summary>
    public void Resubscribe()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            subscription?.Dispose(); subscription = null;
            context.GetVersion().EnsureCompatible("2", 1, ["observe_v1"]);
            var next = context.SubscribeObservations();
            try
            {
                var document = ParseTransport(next.SnapshotJson).GetProperty("document");
                cursorSourceId = document.GetProperty("sourceId").GetString();
                cursorEpoch = document.GetProperty("sessionEpoch").GetUInt64();
                subscription = next;
            }
            catch { next.Dispose(); throw; }
        }
    }

    /// <summary>Accepts a schema-validated common.read. Failure never carries arbitrary remote
    /// errors, selector candidates or values from another profile.</summary>
    public BridgeReadCollection Read(JsonObject read) => ReadBatch([read])[0];

    /// <summary>Polls once for all active reads. Temporal consumers use this batch API so one
    /// read cannot consume another read's intervening notifications. Standard fields have
    /// snapshot-only evidence; Observe notifications do not imply continuous standard state.</summary>
    public IReadOnlyList<BridgeReadCollection> ReadBatch(IReadOnlyList<JsonObject> reads)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (reads.Count == 0 || reads.Count > maxNodes) throw new ArgumentOutOfRangeException(nameof(reads));
            try
            {
                if (subscription is null) Resubscribe();
                var changes = ParseTransport(subscription!.PollJson());
                var budget = new ObservationBudget(maxNodes, maxBytes);
                var results = new List<BridgeReadCollection>();
                foreach (var read in reads)
                {
                    budget.TakeNodes(1);
                    var result = ReadCore(read, changes, budget);
                    budget.TakeBytes(JsonSerializer.Serialize(result));
                    results.Add(result);
                }
                return results.AsReadOnly();
            }
            catch (ObservationLimitException)
            { return Array.AsReadOnly(reads.Select(_ => Failure(ReadAvailability.Truncated, "observation-limit")).ToArray()); }
            catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException or OverflowException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            { return Array.AsReadOnly(reads.Select(_ => Failure(ReadAvailability.Unavailable, "observation-unconfirmed")).ToArray()); }
        }
    }

    private BridgeReadCollection ReadCore(JsonObject read, JsonElement changeTransport, ObservationBudget budget)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (subscription is null) Resubscribe();
                var changes = changeTransport.GetProperty("document");
                if (changes.GetProperty("status").GetString() is "gap") return Failure(ReadAvailability.Gap, "observation-gap");
                if (changes.GetProperty("status").GetString() is "stale_session") return Failure(ReadAvailability.Stale, "stale-session");
                if (changes.GetProperty("sourceId").GetString() != cursorSourceId ||
                    changes.GetProperty("sessionEpoch").GetUInt64() != cursorEpoch)
                    return Failure(ReadAvailability.Stale, "stale-source");
                var before = context.GetContextStatus();
                var transport = ParseTransport(context.GetObserveSnapshotJson());
                var snapshot = transport.GetProperty("document");
                if (snapshot.GetProperty("sourceId").GetString() != cursorSourceId ||
                    snapshot.GetProperty("sessionEpoch").GetUInt64() != cursorEpoch)
                    return Failure(ReadAvailability.Stale, "stale-source");
                if (snapshot.GetProperty("sessionEpoch").GetUInt64() != before.SessionEpoch)
                    return Failure(ReadAvailability.Stale, "stale-session");
                if (snapshot.GetProperty("revision").GetUInt64() != changes.GetProperty("revision").GetUInt64())
                    return Failure(ReadAvailability.Stale, "changed-since-poll");
                string source = read["target"]!["source"]!.GetValue<string>();
                string region = read["region"]!.GetValue<string>();
                var ids = new List<string>();
                JsonElement tree = default;
                if (source == "ui")
                {
                    var result = context.Query(BridgeSelectors.Ui((JsonObject)read["target"]!["selector"]!));
                    if (!result.Valid) return Failure(ReadAvailability.Unavailable, "target-unavailable");
                    ids.AddRange(result.Matches.Select(m => m.Id));
                    tree = ParseTree(context.GetUiTreeJson(), "ui-tree.schema.json");
                }
                else if (source == "object")
                {
                    var result = context.QueryWorldObjects(BridgeSelectors.World((JsonObject)read["target"]!["selector"]!));
                    if (!result.Valid) return Failure(ReadAvailability.Unavailable, "target-unavailable");
                    if (result.Spatial?.Truncated == true) return Failure(ReadAvailability.Truncated, "query-truncated");
                    ids.AddRange(result.Matches.Select(m => m.Id));
                    tree = ParseTree(context.GetWorldObjectTreeJson(), "world-object-tree.schema.json");
                }
                else if (source == "world") ids.Add("");
                else return Failure(ReadAvailability.Unavailable, "invalid-read");
                if (ids.Count > maxNodes) return Failure(ReadAvailability.Truncated, "node-limit");
                budget.TakeNodes(Math.Max(0, ids.Count - 1));
                var after = context.GetContextStatus();
                if (before.SessionEpoch != after.SessionEpoch || before.Revision != after.Revision ||
                    before.WorldRevision != after.WorldRevision ||
                    snapshot.GetProperty("uiRevision").GetUInt64() != after.Revision ||
                    snapshot.GetProperty("worldRevision").GetUInt64() != after.WorldRevision)
                    return Failure(ReadAvailability.Stale, "changed-during-read");
                if (source != "world" && tree.TryGetProperty("sessionEpoch", out var treeEpoch) && treeEpoch.GetUInt64() != after.SessionEpoch)
                    return Failure(ReadAvailability.Stale, "stale-session");
                if (source != "world" && tree.GetProperty("revision").GetUInt64() != (source == "ui" ? after.Revision : after.WorldRevision))
                    return Failure(ReadAvailability.Stale, "stale-tree");
                var entriesByKey = region is "observe" or "property" ? snapshot.GetProperty("entries").EnumerateArray()
                    .Select((e, i) => (e, i)).ToLookup(pair => (pair.e.GetProperty("source").GetString(),
                        pair.e.GetProperty("runtimeId").GetString(), pair.e.GetProperty("name").GetString())) : null;
                var nodesById = region == "standard" && source != "world" ? tree.GetProperty(source == "ui" ? "nodes" : "objects")
                    .EnumerateArray().ToLookup(n => n.GetProperty("id").GetString()!, StringComparer.Ordinal) : null;
                var reads = new List<BridgeRead>();
                foreach (string id in ids)
                {
                    var identity = new ObservationIdentity(snapshot.GetProperty("sourceId").GetString()!, after.SessionEpoch,
                        ProfileName, source, id, source == "ui" ? after.Revision : after.WorldRevision,
                        source == "ui" ? after.FrameSequence : after.WorldFrameSequence);
                    if (region is "observe" or "property")
                    {
                        string name = read["name"]!.GetValue<string>();
                        var entries = entriesByKey![(source, id, name)].ToArray();
                        if (entries.Length != 1) { reads.Add(new(ReadAvailability.Unavailable, "read-unavailable", identity)); continue; }
                        var entry = entries[0].e;
                        identity = identity with { OwnerId = entry.GetProperty("ownerId").GetUInt64(), RegistrationId = entry.GetProperty("registrationId").GetUInt64(),
                            Revision = snapshot.GetProperty("revision").GetUInt64(),
                            FrameSequence = snapshot.GetProperty(source == "ui" ? "uiFrame" : "worldFrame").GetUInt64() };
                        if (entry.GetProperty("status").GetString() != "available")
                        { reads.Add(new(ReadAvailability.Unavailable, "gua-observe-error-" + entry.GetProperty("error").GetInt32(), identity)); continue; }
                        var value = entry.GetProperty("value").Clone();
                        identity = identity with { ValueTypeIdentity = read["valueType"]!.ToJsonString() };
                        JsonElement? catalog = transport.GetProperty("catalogs")[entries[0].i].TryGetProperty("value", out var c) ? c.Clone() : null;
                        if (!CatalogMatches(value, catalog)) return Failure(ReadAvailability.Unavailable, "enum-catalog-unavailable");
                        reads.Add(TypeMatches(value, (JsonObject)read["valueType"]!)
                            ? new(ReadAvailability.Available, "available", identity, value, catalog)
                            : new(ReadAvailability.Unavailable, "value-type-changed", identity));
                    }
                    else if (region == "standard" && source != "world")
                    {
                        var node = nodesById![id].ToArray();
                        if (node.Length != 1) { reads.Add(new(ReadAvailability.Stale, "target-changed", identity)); continue; }
                        JsonElement field = node[0];
                        bool found = true;
                        foreach (string part in read["field"]!.GetValue<string>().Split('.'))
                            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty(part, out field)) { found = false; break; }
                        var value = found ? Scalar(field, (JsonObject)read["valueType"]!) : null;
                        identity = identity with { Revision = tree.GetProperty("revision").GetUInt64(),
                            FrameSequence = tree.GetProperty("frameSequence").GetUInt64(), ValueTypeIdentity = read["valueType"]!.ToJsonString() };
                        reads.Add(value.HasValue ? new(ReadAvailability.Available, "available", identity, value)
                            : new(ReadAvailability.Unavailable, "read-unavailable", identity));
                    }
                    else reads.Add(new(ReadAvailability.Unavailable, "invalid-read", identity));
                }
                // Hidden and nonexistent targets deliberately share this shape.
                if (reads.Count == 0) return Failure(ReadAvailability.Unavailable, "target-unavailable");
                var relevantChanges = new List<BridgeChange>();
                if (region is "observe" or "property")
                {
                    string name = read["name"]!.GetValue<string>();
                    var matchedIds = ids.ToHashSet(StringComparer.Ordinal);
                    int index = 0;
                    foreach (var change in changes.GetProperty("events").EnumerateArray())
                    {
                        if (change.GetProperty("source").GetString() == source &&
                            matchedIds.Contains(change.GetProperty("runtimeId").GetString()!) &&
                            change.GetProperty("name").GetString() == name)
                        {
                            // A recovered final snapshot cannot establish the types of prior values.
                            foreach (string side in new[] { "before", "after" })
                                if (change.TryGetProperty(side + "Status", out var status) && status.GetString() == "available" &&
                                    !TypeMatches(change.GetProperty(side), (JsonObject)read["valueType"]!))
                                    return Failure(ReadAvailability.Unavailable, "value-type-changed");
                            foreach (string side in new[] { "before", "after" })
                                if (change.TryGetProperty(side + "Status", out var status) && status.GetString() == "available" &&
                                    !CatalogMatches(change.GetProperty(side), changeTransport.GetProperty("catalogs")[index].TryGetProperty(side, out var paired) ? paired : null))
                                    return Failure(ReadAvailability.Unavailable, "enum-catalog-unavailable");
                            budget.TakeNodes(1);
                            relevantChanges.Add(new(change.Clone(), changeTransport.GetProperty("catalogs")[index].Clone()));
                        }
                        index++;
                    }
                }
                return new(DateTimeOffset.UtcNow, reads.All(r => r.Availability == ReadAvailability.Available)
                    ? ReadAvailability.Available : ReadAvailability.Unavailable, reads.AsReadOnly(), relevantChanges.AsReadOnly());
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException or OverflowException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            { return Failure(ReadAvailability.Unavailable, "observation-unconfirmed"); }
        }
    }

    private string ProfileName => profile == GuaObservationProfile.Player ? "player" : "debug";
    private JsonElement ParseTransport(string json)
    {
        var root = ParseBounded(json);
        if (!GuaDistribution.ValidateJson("observe-transport-v1.schema.json", json) ||
            root.GetProperty("document").GetProperty("profile").GetString() != ProfileName)
            throw new InvalidOperationException("Bridge profile or schema mismatch.");
        if (root.GetProperty("catalogs").GetArrayLength() > maxNodes) throw new ObservationLimitException();
        if (root.GetProperty("document").TryGetProperty("entries", out var entries) &&
            entries.GetArrayLength() != root.GetProperty("catalogs").GetArrayLength())
            throw new InvalidOperationException("Unpaired Observe catalogs.");
        if (root.GetProperty("document").TryGetProperty("events", out var events) &&
            events.GetArrayLength() != root.GetProperty("catalogs").GetArrayLength())
            throw new InvalidOperationException("Unpaired Observe change catalogs.");
        if (root.GetProperty("document").TryGetProperty("events", out events))
        {
            var document = root.GetProperty("document");
            foreach (var change in events.EnumerateArray())
                if (change.GetProperty("sourceId").GetString() != document.GetProperty("sourceId").GetString() ||
                    change.GetProperty("sessionEpoch").GetUInt64() != document.GetProperty("sessionEpoch").GetUInt64() ||
                    change.GetProperty("profile").GetString() != ProfileName)
                    throw new InvalidOperationException("Observe event identity mismatch.");
        }
        return root;
    }
    private JsonElement ParseTree(string json, string schema)
    {
        var tree = ParseBounded(json);
        if (!GuaDistribution.ValidateJson(schema, json)) throw new InvalidOperationException("Invalid published tree.");
        return tree;
    }
    private JsonElement ParseBounded(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > maxBytes) throw new ObservationLimitException();
        using var doc = JsonDocument.Parse(json);
        foreach (string name in new[] { "nodes", "objects" })
            if (doc.RootElement.TryGetProperty(name, out var items) && items.GetArrayLength() > maxNodes) throw new ObservationLimitException();
        return doc.RootElement.Clone();
    }
    private static bool TypeMatches(JsonElement value, JsonObject type) =>
        new[] { "type", "elementType", "enumType" }.All(k => type[k] is null ? !value.TryGetProperty(k, out _) :
            value.TryGetProperty(k, out var actual) && actual.GetString() == type[k]!.GetValue<string>());
    private static bool CatalogMatches(JsonElement value, JsonElement? catalog)
    {
        if (!value.TryGetProperty("enumType", out var enumType)) return true;
        if (catalog is null) return false;
        var definitions = catalog.Value.GetProperty("enums");
        if (definitions.GetArrayLength() != 1 || definitions[0].GetProperty("enumType").GetString() != enumType.GetString()) return false;
        var members = definitions[0].GetProperty("members").EnumerateArray().Select(m => m.GetString()!).ToHashSet(StringComparer.Ordinal);
        var payload = value.GetProperty("value");
        return value.GetProperty("type").GetString() == "enum" ? members.Contains(payload.GetString()!) :
            payload.EnumerateArray().All(m => members.Contains(m.GetString()!));
    }
    private static JsonElement? Scalar(JsonElement field, JsonObject type)
    {
        string kind = type["type"]!.GetValue<string>();
        if (kind == "list" && type["elementType"]?.GetValue<string>() == "string" && field.ValueKind == JsonValueKind.Array)
        {
            var list = new JsonObject { ["type"] = "list", ["elementType"] = "string",
                ["value"] = JsonNode.Parse(field.GetRawText()) };
            return GuaDistribution.ValidateJson("value-v1.schema.json", list.ToJsonString()) ? JsonSerializer.SerializeToElement(list) : null;
        }
        if (kind == "bool" && field.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            kind == "string" && field.ValueKind != JsonValueKind.String ||
            kind is "integer" or "number" && field.ValueKind != JsonValueKind.Number ||
            kind is not ("bool" or "string" or "integer" or "number")) return null;
        var value = new JsonObject { ["type"] = kind, ["value"] = JsonNode.Parse(field.GetRawText()) };
        return GuaDistribution.ValidateJson("value-v1.schema.json", value.ToJsonString()) ? JsonSerializer.SerializeToElement(value) : null;
    }
    private static BridgeReadCollection Failure(ReadAvailability availability, string reason) =>
        new(DateTimeOffset.UtcNow, availability, Array.AsReadOnly(new[] { new BridgeRead(availability, reason) }));
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            try { subscription?.Dispose(); } finally { subscription = null; context.Dispose(); }
        }
    }
    private sealed class ObservationLimitException : Exception;
    private sealed class ObservationBudget(int nodes, int bytes)
    {
        private int remainingNodes = nodes, remainingBytes = bytes;
        public void TakeNodes(int count)
        { if (count > remainingNodes) throw new ObservationLimitException(); remainingNodes -= count; }
        public void TakeBytes(string json)
        { int count = Encoding.UTF8.GetByteCount(json); if (count > remainingBytes) throw new ObservationLimitException(); remainingBytes -= count; }
    }
}
