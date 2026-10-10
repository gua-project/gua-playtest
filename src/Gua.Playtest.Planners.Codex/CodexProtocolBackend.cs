using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Contracts;

namespace Gua.Playtest.Planners.Codex;

public enum CodexReplyStatus { Completed, UsageLimit, ConnectionFailure, OutputInvalid }
public sealed record CodexUsage(long InputTokens, long OutputTokens, long TotalTokens);
public sealed record CodexReply(CodexReplyStatus Status, byte[]? CompletedJson = null, CodexUsage? Usage = null);
public interface ICodexDecisionBackend
{
    ValueTask<CodexReply> DecideAsync(PlannerInputDocument input, CancellationToken cancellationToken);
}

/// <summary>One owned protocol connection. Reads return a complete UTF-8 JSONL frame without LF,
/// or null on EOF. Implementations must enforce the byte limit before allocating the frame,
/// honor cancellation and return promptly. This port does not authorize process launch or inference.</summary>
public interface ICodexProtocolTransport
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> jsonLine, CancellationToken cancellationToken);
    ValueTask<byte[]?> ReadAsync(int maximumBytes, CancellationToken cancellationToken);
}

public sealed record CodexProtocolLimits(int MaximumFrameBytes, int MaximumMessages, TimeSpan Timeout);

/// <summary>Version-specific 0.150.1 protocol conversion, with no process launcher, credentials,
/// filesystem or game capabilities. Production composition remains unavailable pending OPEN-09.
/// One instance belongs to one Run; faults/cancellation permanently close it.</summary>
public sealed class CodexProtocolBackend : ICodexDecisionBackend
{
    private readonly ICodexProtocolTransport transport;
    private readonly CodexProtocolLimits limits;
    private readonly Func<string, string> redact;
    private readonly JsonObject outputSchema;
    private readonly string runId;
    private readonly HashSet<string> requests = new(StringComparer.Ordinal);
    private string? threadId;
    private long nextId;
    private int active;
    private bool closed;
    private int messages;
    private CodexUsage usageHighWater = new(0, 0, 0);
    private bool usageBaselineKnown = true;
    private readonly Queue<JsonElement> pendingTurnEvents = new();
    private int pendingBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public CodexProtocolBackend(string runId, ICodexProtocolTransport transport, JsonObject outputSchema,
        CodexProtocolLimits limits, Func<string, string> redact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(outputSchema);
        ArgumentNullException.ThrowIfNull(redact);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumFrameBytes is < 1 or > 1048576 || limits.MaximumMessages is < 1 or > 10000 ||
            limits.Timeout <= TimeSpan.Zero || limits.Timeout > TimeSpan.FromDays(1))
            throw new ArgumentException("CodexProtocolLimitsInvalid");
        this.runId = runId; this.transport = transport; this.redact = redact;
        this.outputSchema = (JsonObject)outputSchema.DeepClone(); this.limits = limits;
    }

    public async ValueTask<CodexReply> DecideAsync(PlannerInputDocument input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        // A second caller never reads or writes the current exchange.
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return new(CodexReplyStatus.ConnectionFailure);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.Timeout);
        var token = timeout.Token;
        try
        {
            if (closed || input.RunId != runId || requests.Count >= 10000 || !requests.Add(input.DecisionRequestId))
                return new(CodexReplyStatus.OutputInvalid);
            messages = 0;
            pendingTurnEvents.Clear(); pendingBytes = 0;
            var projected = JsonNode.Parse(ContractJson.Serialize(input)) ?? throw new ProtocolException();
            RedactInput(projected);
            var publicInput = projected.ToJsonString();
            // Observation/game text is data in a user message, never developer/system instructions.
            if (threadId is null)
            {
                var initialized = await RequestAsync("initialize", new JsonObject
                {
                    ["clientInfo"] = new JsonObject { ["name"] = "gua-playtest", ["version"] = "0.1.0" },
                    ["capabilities"] = new JsonObject { ["experimentalApi"] = false }
                }, token).ConfigureAwait(false);
                if (!initialized.TryGetProperty("userAgent", out var agent) || agent.ValueKind != JsonValueKind.String ||
                    !SupportedUserAgent(agent.GetString()!)) throw new BackendException();
                await WriteAsync(new JsonObject { ["method"] = "initialized" }, token).ConfigureAwait(false);
                var started = await RequestAsync("thread/start", new JsonObject
                {
                    ["ephemeral"] = true, ["approvalPolicy"] = "never", ["sandbox"] = "read-only"
                }, token).ConfigureAwait(false);
                threadId = Identifier(started.GetProperty("thread").GetProperty("id"));
            }
            var usageBaseline = usageHighWater;
            var result = await RequestAsync("turn/start", new JsonObject
            {
                ["threadId"] = threadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = publicInput }),
                ["approvalPolicy"] = "never",
                ["sandboxPolicy"] = new JsonObject { ["type"] = "readOnly", ["networkAccess"] = false },
                ["outputSchema"] = outputSchema.DeepClone()
            }, token).ConfigureAwait(false);
            var turnId = Identifier(result.GetProperty("turn").GetProperty("id"));
            CodexUsage? usage = null;
            var completedItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var completedItemBytes = 0;
            while (true)
            {
                var message = pendingTurnEvents.Count > 0 ? pendingTurnEvents.Dequeue() : await ReadAsync(token).ConfigureAwait(false);
                // Server requests are capabilities we never grant (including approvals and tools).
                if (message.TryGetProperty("id", out _)) throw new ProtocolException();
                var method = message.GetProperty("method").GetString();
                var parameters = message.GetProperty("params");
                if (parameters.TryGetProperty("threadId", out var thread) && thread.GetString() != threadId)
                    throw new ProtocolException();
                if (method == "thread/tokenUsage/updated")
                {
                    if (Identifier(parameters.GetProperty("threadId")) != threadId) throw new ProtocolException();
                    if (parameters.GetProperty("turnId").GetString() != turnId) throw new ProtocolException();
                    // `last` is one model response; `total` accumulates the thread.
                    // Duplicate or regressive snapshots must not erase already observed usage.
                    var tokenUsage = parameters.GetProperty("tokenUsage");
                    ValidateUsage(tokenUsage.GetProperty("last"));
                    var total = tokenUsage.GetProperty("total"); ValidateUsage(total);
                    usageHighWater = new(Math.Max(usageHighWater.InputTokens, Nonnegative(total, "inputTokens")),
                        Math.Max(usageHighWater.OutputTokens, Nonnegative(total, "outputTokens")),
                        Math.Max(usageHighWater.TotalTokens, Nonnegative(total, "totalTokens")));
                    usage = usageBaselineKnown ? new(usageHighWater.InputTokens - usageBaseline.InputTokens,
                        usageHighWater.OutputTokens - usageBaseline.OutputTokens,
                        usageHighWater.TotalTokens - usageBaseline.TotalTokens) : null;
                }
                else if (method == "item/completed")
                {
                    ValidateTurnNotice(message, threadId, turnId);
                    var item = parameters.GetProperty("item");
                    if (IsFinalAgent(item))
                    {
                        var itemId = Identifier(item.GetProperty("id"));
                        CheckRedaction(item);
                        if (completedItems.TryGetValue(itemId, out var previous))
                        { if (!JsonElement.DeepEquals(previous, item)) throw new ProtocolException(); }
                        else
                        {
                            completedItemBytes = checked(completedItemBytes + Utf8.GetByteCount(item.GetRawText()));
                            if (completedItemBytes > limits.MaximumFrameBytes) throw new ProtocolException();
                            completedItems.Add(itemId, item.Clone());
                        }
                    }
                }
                else if (method == "turn/completed")
                {
                    if (Identifier(parameters.GetProperty("threadId")) != threadId) throw new ProtocolException();
                    var turn = parameters.GetProperty("turn");
                    if (Identifier(turn.GetProperty("id")) != turnId) throw new ProtocolException();
                    var turnItems = turn.GetProperty("items");
                    if (turnItems.ValueKind != JsonValueKind.Array) throw new ProtocolException();
                    token.ThrowIfCancellationRequested();
                    // A later cumulative snapshot cannot separate an unobserved earlier turn.
                    if (usage is null) usageBaselineKnown = false;
                    var status = turn.GetProperty("status").GetString();
                    if (status == "interrupted")
                    { closed = true; return new(CodexReplyStatus.ConnectionFailure, Usage: usage); }
                    if (status == "failed")
                    {
                        closed = true;
                        if (!turn.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
                            return new(CodexReplyStatus.ConnectionFailure, Usage: usage);
                        if (error.GetProperty("message").ValueKind != JsonValueKind.String) throw new ProtocolException();
                        return new(error.TryGetProperty("codexErrorInfo", out var info) &&
                            info.ValueKind == JsonValueKind.String && info.GetString() is "usageLimitExceeded" or "sessionBudgetExceeded"
                            ? CodexReplyStatus.UsageLimit : CodexReplyStatus.ConnectionFailure, Usage: usage);
                    }
                    if (status != "completed") throw new ProtocolException();
                    var itemsView = turn.TryGetProperty("itemsView", out var view) ? view.GetString() : "full";
                    if (itemsView is not ("full" or "summary" or "notLoaded")) throw new ProtocolException();
                    var items = turnItems.EnumerateArray().Where(IsFinalAgent).ToArray();
                    if (items.Length != 1) throw new ProtocolException();
                    if (itemsView != "full")
                    {
                        if (itemsView != "summary") throw new ProtocolException();
                        var itemId = Identifier(items[0].GetProperty("id"));
                        // Ephemeral 0.150.1 threads cannot read persisted turns. Hydrate
                        // the output from its canonical live completion, never display text.
                        if (!completedItems.TryGetValue(itemId, out var canonical)) throw new BackendException();
                        if (!JsonElement.DeepEquals(items[0], canonical)) throw new ProtocolException();
                        items[0] = canonical;
                    }
                    var text = items[0].GetProperty("text").GetString() ?? throw new ProtocolException();
                    if (redact(text) != text) throw new ProtocolException();
                    var bytes = Utf8.GetBytes(text);
                    if (bytes.Length == 0 || bytes.Length > limits.MaximumFrameBytes) throw new ProtocolException();
                    var decision = Parse(bytes);
                    CheckRedaction(decision);
                    if (decision.GetProperty("runId").GetString() != input.RunId ||
                        decision.GetProperty("decisionRequestId").GetString() != input.DecisionRequestId ||
                        decision.GetProperty("basedOnObservationId").GetString() != input.BasedOnObservationId)
                        throw new ProtocolException();
                    token.ThrowIfCancellationRequested();
                    // Runner's pinned exchange validator, not this converter, grants adoption.
                    return new(CodexReplyStatus.Completed, bytes, usage);
                }
                // Deltas, reasoning and intermediate item notifications never form a Decision.
            }
        }
        catch (OperationCanceledException)
        {
            closed = true;
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return new(CodexReplyStatus.ConnectionFailure);
        }
        catch (Exception exception) when (exception is ProtocolException or JsonException or
            InvalidOperationException or KeyNotFoundException or DecoderFallbackException or FormatException or OverflowException)
        { closed = true; return new(CodexReplyStatus.OutputInvalid); }
        catch { closed = true; return new(CodexReplyStatus.ConnectionFailure); }
        finally { pendingTurnEvents.Clear(); Volatile.Write(ref active, 0); }
    }

    private async ValueTask<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken token)
    {
        var id = checked(++nextId);
        string? announcedThread = null;
        string? announcedTurn = null;
        await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, token).ConfigureAwait(false);
        while (true)
        {
            var message = await ReadAsync(token).ConfigureAwait(false);
            if (!message.TryGetProperty("id", out var received))
            {
                if (message.GetProperty("params").ValueKind != JsonValueKind.Object) throw new ProtocolException();
                // 0.150.1 sends benign configuration/remote-control status notices
                // after initialize. They grant no capabilities and still consume bounds.
                var notification = message.GetProperty("method").GetString();
                if (notification is "configWarning" or "remoteControl/status/changed") continue;
                if (notification is "thread/started" or "turn/started" or "thread/status/changed")
                {
                    var notice = message.GetProperty("params");
                    var noticeThread = Identifier(notification == "thread/started"
                        ? notice.GetProperty("thread").GetProperty("id") : notice.GetProperty("threadId"));
                    if ((threadId is not null && noticeThread != threadId) ||
                        (threadId is null && method != "thread/start") ||
                        (announcedThread is not null && announcedThread != noticeThread)) throw new ProtocolException();
                    announcedThread = noticeThread;
                    if (notification == "thread/status/changed")
                    {
                        var status = notice.GetProperty("status");
                        var kind = status.GetProperty("type").GetString();
                        if (kind == "active")
                        {
                            foreach (var flag in status.GetProperty("activeFlags").EnumerateArray())
                                if (flag.GetString() is not ("waitingOnApproval" or "waitingOnUserInput")) throw new ProtocolException();
                        }
                        else if (kind is not ("notLoaded" or "idle" or "systemError")) throw new ProtocolException();
                    }
                    if (notification == "turn/started")
                    {
                        if (method != "turn/start") throw new ProtocolException();
                        var noticeTurn = Identifier(notice.GetProperty("turn").GetProperty("id"));
                        if (announcedTurn is not null && announcedTurn != noticeTurn) throw new ProtocolException();
                        announcedTurn = noticeTurn;
                    }
                    continue;
                }
                if (method == "turn/start" && announcedTurn is not null && notification is not null &&
                    (notification.StartsWith("item/", StringComparison.Ordinal) ||
                     notification.StartsWith("turn/", StringComparison.Ordinal) || notification == "thread/tokenUsage/updated"))
                {
                    ValidateTurnNotice(message, threadId!, announcedTurn);
                    pendingBytes = checked(pendingBytes + Utf8.GetByteCount(message.GetRawText()));
                    if (pendingBytes > limits.MaximumFrameBytes) throw new ProtocolException();
                    pendingTurnEvents.Enqueue(message); continue;
                }
                throw new ProtocolException();
            }
            if (!received.TryGetInt64(out var number) || number != id || message.TryGetProperty("method", out _)) throw new ProtocolException();
            if (message.TryGetProperty("error", out var error))
            {
                if (message.TryGetProperty("result", out _) || !error.GetProperty("code").TryGetInt64(out _) ||
                    error.GetProperty("message").ValueKind != JsonValueKind.String) throw new ProtocolException();
                throw new BackendException();
            }
            var result = message.GetProperty("result");
            if (method == "thread/start" && announcedThread is not null &&
                Identifier(result.GetProperty("thread").GetProperty("id")) != announcedThread) throw new ProtocolException();
            if (method == "turn/start" && announcedTurn is not null &&
                Identifier(result.GetProperty("turn").GetProperty("id")) != announcedTurn) throw new ProtocolException();
            return result;
        }
    }
    private async ValueTask WriteAsync(JsonObject message, CancellationToken token)
    {
        var bytes = Utf8.GetBytes(message.ToJsonString() + "\n");
        if (bytes.Length > limits.MaximumFrameBytes) throw new ProtocolException();
        CheckRedaction(Parse(bytes));
        await BoundedAsync(transport.WriteAsync(bytes, token).AsTask(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    private async ValueTask<JsonElement> ReadAsync(CancellationToken token)
    {
        if (++messages > limits.MaximumMessages) throw new ProtocolException();
        var pending = transport.ReadAsync(limits.MaximumFrameBytes, token).AsTask();
        await BoundedAsync(pending, token).ConfigureAwait(false);
        var bytes = await pending.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (bytes is null) throw new IOException();
        if (bytes.Length == 0 || bytes.Length > limits.MaximumFrameBytes || bytes.Contains((byte)'\n')) throw new ProtocolException();
        return Parse(bytes);
    }
    private static JsonElement Parse(byte[] bytes)
    {
        // Reject invalid UTF-8 and duplicate keys rather than choosing a permissive last value.
        _ = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        CheckKeys(document.RootElement);
        return document.RootElement.Clone();
    }
    private static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new ProtocolException(); CheckKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckKeys(item);
    }
    private static string Identifier(JsonElement value)
    {
        var text = value.GetString();
        return !string.IsNullOrEmpty(text) && text.Length <= 128 ? text : throw new ProtocolException();
    }
    private static bool SupportedUserAgent(string userAgent)
    {
        // 0.150.1 emits originator/version followed by OS/client details; the
        // client-controlled suffix is not evidence of the server build version.
        var product = userAgent.Split(' ', 2)[0];
        var slash = product.IndexOf('/');
        return slash > 0 && product[(slash + 1)..] == "0.150.1";
    }
    private static long Nonnegative(JsonElement value, string name)
    { var count = value.GetProperty(name).GetInt64(); return count >= 0 ? count : throw new ProtocolException(); }
    private static void ValidateUsage(JsonElement value)
    {
        foreach (var name in new[] { "inputTokens", "cachedInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens" })
            _ = Nonnegative(value, name);
        if (value.TryGetProperty("cacheWriteInputTokens", out _)) _ = Nonnegative(value, "cacheWriteInputTokens");
    }
    private static bool IsFinalAgent(JsonElement item) => item.GetProperty("type").GetString() == "agentMessage" &&
        (!item.TryGetProperty("delivery", out var delivery) || delivery.ValueKind == JsonValueKind.Null) &&
        (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer");
    private static void ValidateTurnNotice(JsonElement message, string expectedThread, string expectedTurn)
    {
        var parameters = message.GetProperty("params");
        if (Identifier(parameters.GetProperty("threadId")) != expectedThread) throw new ProtocolException();
        var turn = message.GetProperty("method").GetString() == "turn/completed"
            ? parameters.GetProperty("turn").GetProperty("id") : parameters.GetProperty("turnId");
        if (Identifier(turn) != expectedTurn) throw new ProtocolException();
    }
    private void RedactInput(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var property in obj.ToArray())
            {
                if (redact(property.Key) != property.Key) throw new ProtocolException();
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[property.Key] = redact(text);
                else if (property.Value is { } child) RedactInput(child);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = redact(text);
                else if (array[index] is { } child) RedactInput(child);
            }
    }
    private void CheckRedaction(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && redact(element.GetString()!) != element.GetString()) throw new ProtocolException();
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            { if (redact(property.Name) != property.Name) throw new ProtocolException(); CheckRedaction(property.Value); }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckRedaction(item);
    }
    private static async Task BoundedAsync(Task task, CancellationToken token)
    {
        try { await task.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // The abandoned session can never read/write again; observe any noncooperative late fault.
            _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    private sealed class ProtocolException : Exception;
    private sealed class BackendException : Exception;
}
