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
                foreach (var name in new[] { "codexHome", "platformFamily", "platformOs" })
                    if (!initialized.TryGetProperty(name, out var required) || required.ValueKind != JsonValueKind.String)
                        throw new ProtocolException();
                await WriteAsync(new JsonObject { ["method"] = "initialized" }, token).ConfigureAwait(false);
                var started = await RequestAsync("thread/start", new JsonObject
                {
                    ["ephemeral"] = true, ["approvalPolicy"] = "never", ["sandbox"] = "read-only"
                }, token).ConfigureAwait(false);
                var createdThread = started.GetProperty("thread");
                if (!createdThread.TryGetProperty("ephemeral", out var ephemeral) || ephemeral.ValueKind != JsonValueKind.True)
                    throw new BackendException();
                if (!started.TryGetProperty("approvalPolicy", out var approval) || approval.ValueKind != JsonValueKind.String ||
                    approval.GetString() != "never" || !started.TryGetProperty("sandbox", out var sandbox) ||
                    sandbox.ValueKind != JsonValueKind.Object || !sandbox.TryGetProperty("type", out var sandboxType) ||
                    sandboxType.ValueKind != JsonValueKind.String || sandboxType.GetString() != "readOnly" ||
                    (sandbox.TryGetProperty("networkAccess", out var network) && network.ValueKind != JsonValueKind.False))
                    throw new BackendException();
                if (!CodexItemSchema.ValidThreadStart(started)) throw new ProtocolException();
                ValidateThread(createdThread);
                threadId = Identifier(createdThread.GetProperty("id"));
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
            var startedTurn = result.GetProperty("turn");
            ValidateTurn(startedTurn);
            if (startedTurn.GetProperty("status").GetString() != "inProgress") throw new ProtocolException();
            var turnId = Identifier(startedTurn.GetProperty("id"));
            CodexUsage? usage = null;
            JsonElement? terminalError = null;
            var completedItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var completedItemBytes = 0;
            while (true)
            {
                var message = pendingTurnEvents.Count > 0 ? pendingTurnEvents.Dequeue() : await ReadAsync(token).ConfigureAwait(false);
                // Server requests are capabilities we never grant (including approvals and tools).
                if (message.TryGetProperty("id", out _)) throw new ProtocolException();
                var method = message.GetProperty("method").GetString();
                RejectInheritedHook(method);
                if (string.IsNullOrWhiteSpace(method)) throw new ProtocolException();
                var parameters = message.GetProperty("params");
                if (CodexItemSchema.IsStartupNotice(method)) { ValidateStartupNotice(method!, parameters, threadId); continue; }
                if (method == "warning") { ValidateWarning(parameters, threadId); continue; }
                if (parameters.TryGetProperty("threadId", out var thread) && thread.GetString() != threadId)
                    throw new ProtocolException();
                if (method is not null && (method.StartsWith("item/", StringComparison.Ordinal) ||
                    method.StartsWith("turn/", StringComparison.Ordinal)))
                    ValidateTurnNotice(message, threadId, turnId);
                if (method == "turn/started") ValidateTurnStartedNotice(message, threadId, turnId);
                else if (method == "thread/started")
                {
                    ValidateThread(parameters.GetProperty("thread"));
                    if (Identifier(parameters.GetProperty("thread").GetProperty("id")) != threadId) throw new ProtocolException();
                }
                else if (method == "thread/status/changed") ValidateThreadStatusNotice(parameters, threadId);
                else if (method == "thread/tokenUsage/updated")
                {
                    if (Identifier(parameters.GetProperty("threadId")) != threadId) throw new ProtocolException();
                    if (parameters.GetProperty("turnId").GetString() != turnId) throw new ProtocolException();
                    // `last` is one model response; `total` accumulates the thread.
                    // Duplicate or regressive snapshots must not erase already observed usage.
                    var tokenUsage = parameters.GetProperty("tokenUsage");
                    ValidateUsage(tokenUsage.GetProperty("last"));
                    var total = tokenUsage.GetProperty("total"); ValidateUsage(total);
                    var currentInput = Nonnegative(total, "inputTokens");
                    var currentOutput = Nonnegative(total, "outputTokens");
                    var currentTotal = Nonnegative(total, "totalTokens");
                    // Context-full snapshots replace counters with zero breakdown and a synthetic
                    // context-window total. A mixed regression/increase also loses epoch identity.
                    // Neither can provide measured decision usage, including subsequent turns.
                    if ((currentTotal > 0 && currentInput == 0 && currentOutput == 0) ||
                        ((currentInput < usageHighWater.InputTokens || currentOutput < usageHighWater.OutputTokens) &&
                         (currentTotal > usageHighWater.TotalTokens || currentInput > usageHighWater.InputTokens ||
                          currentOutput > usageHighWater.OutputTokens)) ||
                        (currentTotal < usageHighWater.TotalTokens && (currentInput > usageHighWater.InputTokens ||
                         currentOutput > usageHighWater.OutputTokens))) usageBaselineKnown = false;
                    usageHighWater = new(Math.Max(usageHighWater.InputTokens, Nonnegative(total, "inputTokens")),
                        Math.Max(usageHighWater.OutputTokens, Nonnegative(total, "outputTokens")),
                        Math.Max(usageHighWater.TotalTokens, Nonnegative(total, "totalTokens")));
                    usage = usageBaselineKnown ? new(usageHighWater.InputTokens - usageBaseline.InputTokens,
                        usageHighWater.OutputTokens - usageBaseline.OutputTokens,
                        usageHighWater.TotalTokens - usageBaseline.TotalTokens) : null;
                }
                else if (CodexItemSchema.IsTelemetry(method)) ValidateTelemetry(message, threadId, turnId);
                else if (method == "model/rerouted") ValidateModelRerouted(message, threadId, turnId);
                else if (method == "error")
                {
                    ValidateErrorNotice(message, threadId, turnId);
                    // A retry can recover; non-retryable failure cannot be erased by later completion.
                    if (parameters.GetProperty("willRetry").ValueKind == JsonValueKind.False)
                        terminalError = parameters.GetProperty("error").Clone();
                }
                else if (method == "item/completed")
                {
                    ValidateItemCompleted(message, threadId, turnId);
                    var item = parameters.GetProperty("item");
                    if (!CodexItemSchema.Valid(item)) throw new ProtocolException();
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
                    ValidateTurn(turn);
                    var turnItems = turn.GetProperty("items");
                    token.ThrowIfCancellationRequested();
                    // A later cumulative snapshot cannot separate an unobserved earlier turn.
                    if (usage is null) usageBaselineKnown = false;
                    var status = turn.GetProperty("status").GetString();
                    if (terminalError is { } observedError && (status != "failed" ||
                        !turn.TryGetProperty("error", out var repeatedError) ||
                        !JsonElement.DeepEquals(observedError, repeatedError))) throw new ProtocolException();
                    if (status == "interrupted")
                    { closed = true; return new(CodexReplyStatus.ConnectionFailure, Usage: usage); }
                    if (status == "failed")
                    {
                        closed = true;
                        if (!turn.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
                            return new(CodexReplyStatus.ConnectionFailure, Usage: usage);
                        if (!CodexItemSchema.ValidError(error)) throw new ProtocolException();
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
                RejectInheritedHook(notification);
                if (CodexItemSchema.IsStartupNotice(notification))
                { ValidateStartupNotice(notification!, message.GetProperty("params"), threadId ?? announcedThread); continue; }
                if (notification == "warning")
                { ValidateWarning(message.GetProperty("params"), threadId ?? announcedThread); continue; }
                if (notification is "remoteControl/status/changed" or "account/rateLimits/updated") continue;
                if (notification is "thread/started" or "turn/started" or "thread/status/changed")
                {
                    var notice = message.GetProperty("params");
                    if (notification == "thread/started") ValidateThread(notice.GetProperty("thread"));
                    var noticeThread = Identifier(notification == "thread/started"
                        ? notice.GetProperty("thread").GetProperty("id") : notice.GetProperty("threadId"));
                    if ((threadId is not null && noticeThread != threadId) ||
                        (threadId is null && method != "thread/start") ||
                        (announcedThread is not null && announcedThread != noticeThread)) throw new ProtocolException();
                    announcedThread = noticeThread;
                    if (notification == "thread/status/changed") ValidateThreadStatusNotice(notice, noticeThread);
                    if (notification == "turn/started")
                    {
                        if (method != "turn/start") throw new ProtocolException();
                        ValidateTurnStartedNotice(message, threadId!, Identifier(notice.GetProperty("turn").GetProperty("id")));
                        var noticeTurn = Identifier(notice.GetProperty("turn").GetProperty("id"));
                        if (announcedTurn is not null && announcedTurn != noticeTurn) throw new ProtocolException();
                        announcedTurn = noticeTurn;
                    }
                    continue;
                }
                if (method == "turn/start" && announcedTurn is not null && notification is not null &&
                    (notification.StartsWith("item/", StringComparison.Ordinal) ||
                     notification.StartsWith("turn/", StringComparison.Ordinal) || notification is "thread/tokenUsage/updated" or "error" or "model/rerouted" || CodexItemSchema.IsTelemetry(notification)))
                {
                    ValidateTurnNotice(message, threadId!, announcedTurn);
                    if (CodexItemSchema.IsTelemetry(notification)) ValidateTelemetry(message, threadId!, announcedTurn);
                    if (notification == "model/rerouted") ValidateModelRerouted(message, threadId!, announcedTurn);
                    if (notification == "error") ValidateErrorNotice(message, threadId!, announcedTurn);
                    if (notification == "item/completed") ValidateItemCompleted(message, threadId!, announcedTurn);
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
        var turn = message.GetProperty("method").GetString() is "turn/completed" or "turn/started"
            ? parameters.GetProperty("turn").GetProperty("id") : parameters.GetProperty("turnId");
        if (Identifier(turn) != expectedTurn) throw new ProtocolException();
    }
    private static void ValidateTurn(JsonElement turn)
    {
        _ = Identifier(turn.GetProperty("id"));
        if (turn.GetProperty("status").GetString() is not ("inProgress" or "completed" or "failed" or "interrupted"))
            throw new ProtocolException();
        var items = turn.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array) throw new ProtocolException();
        foreach (var item in items.EnumerateArray())
            if (!CodexItemSchema.Valid(item)) throw new ProtocolException();
        if (turn.TryGetProperty("itemsView", out var view) &&
            (view.ValueKind != JsonValueKind.String || view.GetString() is not ("full" or "summary" or "notLoaded")))
            throw new ProtocolException();
        foreach (var name in new[] { "startedAt", "completedAt", "durationMs" })
            if (turn.TryGetProperty(name, out var timestamp) && timestamp.ValueKind != JsonValueKind.Null &&
                (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out _))) throw new ProtocolException();
        if (turn.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
            (turn.GetProperty("status").GetString() != "failed" || !CodexItemSchema.ValidError(error)))
            throw new ProtocolException();
    }
    private static void ValidateTurnStartedNotice(JsonElement message, string expectedThread, string expectedTurn)
    {
        ValidateTurnNotice(message, expectedThread, expectedTurn);
        var turn = message.GetProperty("params").GetProperty("turn");
        ValidateTurn(turn);
        if (turn.GetProperty("status").GetString() != "inProgress") throw new ProtocolException();
    }
    private static void ValidateThreadStatusNotice(JsonElement parameters, string expectedThread)
    {
        if (Identifier(parameters.GetProperty("threadId")) != expectedThread) throw new ProtocolException();
        var status = parameters.GetProperty("status");
        var kind = status.GetProperty("type").GetString();
        if (kind == "active")
        {
            foreach (var flag in status.GetProperty("activeFlags").EnumerateArray())
                if (flag.GetString() is not ("waitingOnApproval" or "waitingOnUserInput")) throw new ProtocolException();
        }
        else if (kind is not ("notLoaded" or "idle" or "systemError")) throw new ProtocolException();
    }
    private static void ValidateThread(JsonElement thread)
    {
        if (!CodexItemSchema.ValidThread(thread)) throw new ProtocolException();
        _ = Identifier(thread.GetProperty("id"));
        if (thread.GetProperty("ephemeral").ValueKind != JsonValueKind.True || thread.GetProperty("turns").GetArrayLength() != 0)
            throw new ProtocolException();
        if (thread.GetProperty("status").GetProperty("type").GetString() != "idle" ||
            thread.GetProperty("cliVersion").GetString() != "0.150.1") throw new BackendException();
        foreach (var name in new[] { "createdAt", "updatedAt" })
            if (!thread.GetProperty(name).TryGetInt64(out _)) throw new ProtocolException();
    }
    private static void ValidateItemCompleted(JsonElement message, string expectedThread, string expectedTurn)
    {
        ValidateTurnNotice(message, expectedThread, expectedTurn);
        var parameters = message.GetProperty("params");
        if (!CodexItemSchema.ValidItemCompleted(parameters) || !parameters.GetProperty("completedAtMs").TryGetInt64(out _))
            throw new ProtocolException();
    }
    private static void ValidateTelemetry(JsonElement message, string expectedThread, string expectedTurn)
    {
        ValidateTurnNotice(message, expectedThread, expectedTurn);
        var method = message.GetProperty("method").GetString()!;
        var parameters = message.GetProperty("params");
        if (!CodexItemSchema.ValidTelemetry(method, parameters)) throw new ProtocolException();
        if (method == "rawResponse/completed" && parameters.TryGetProperty("usage", out var usage) &&
            usage.ValueKind != JsonValueKind.Null) ValidateUsage(usage);
    }
    private static void ValidateModelRerouted(JsonElement message, string expectedThread, string expectedTurn)
    {
        ValidateTurnNotice(message, expectedThread, expectedTurn);
        var parameters = message.GetProperty("params");
        if (parameters.GetProperty("fromModel").ValueKind != JsonValueKind.String ||
            parameters.GetProperty("toModel").ValueKind != JsonValueKind.String ||
            parameters.GetProperty("reason").GetString() != "highRiskCyberActivity") throw new ProtocolException();
    }
    private static void RejectInheritedHook(string? method)
    {
        // Notice refusal cannot prevent upstream execution. No production host isolation
        // or inherited hook capability has been accepted under OPEN-09.
        if (method?.StartsWith("hook/", StringComparison.Ordinal) == true) throw new BackendException();
    }
    private static void ValidateStartupNotice(string method, JsonElement parameters, string? expectedThread)
    {
        if (!CodexItemSchema.ValidStartupNotice(method, parameters)) throw new ProtocolException();
        if (method == "guardianWarning" && Identifier(parameters.GetProperty("threadId")) != expectedThread)
            throw new ProtocolException();
    }
    private static void ValidateWarning(JsonElement parameters, string? expectedThread)
    {
        if (parameters.GetProperty("message").ValueKind != JsonValueKind.String) throw new ProtocolException();
        if (parameters.TryGetProperty("threadId", out var thread) && thread.ValueKind != JsonValueKind.Null &&
            Identifier(thread) != expectedThread) throw new ProtocolException();
    }
    private static void ValidateErrorNotice(JsonElement message, string expectedThread, string expectedTurn)
    {
        ValidateTurnNotice(message, expectedThread, expectedTurn);
        var parameters = message.GetProperty("params");
        if (parameters.GetProperty("willRetry").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !CodexItemSchema.ValidError(parameters.GetProperty("error"))) throw new ProtocolException();
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
