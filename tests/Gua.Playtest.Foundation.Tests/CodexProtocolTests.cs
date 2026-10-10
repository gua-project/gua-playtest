using System.Text;
using System.Text.Json.Nodes;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Gua.Playtest.Planners.Codex;
using Gua.Playtest.Runner.Planning;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public sealed class CodexProtocolTests
{
    private sealed class Transport : ICodexProtocolTransport
    {
        public readonly Queue<byte[]?> Frames = new();
        public readonly List<string> Writes = [];
        public Func<CancellationToken, ValueTask<byte[]?>>? Read;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> line, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes.Add(Encoding.UTF8.GetString(line.Span)); return ValueTask.CompletedTask; }
        public ValueTask<byte[]?> ReadAsync(int maximumBytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Frames.Count > 0 ? new(Frames.Dequeue()) : Read?.Invoke(token) ?? new((byte[]?)null); }
        public void Add(string frame) => Frames.Enqueue(Encoding.UTF8.GetBytes(frame));
    }
    private static PlannerInputDocument Input(string request = "request-1") => new("run-1", request, "observation-1",
        "public objective", new(5000, 100, 10, 1000, 1000, 1000, 1000, 1000, 0, 100,
            100000, 100, 1024, 12, 4, 2, 100000, 1024, 1024, 100000), new(), new(), new(), []);
    private static string Decision(string request = "request-1") =>
        "{\"kind\":\"plannerDecision\",\"schemaVersion\":1,\"runId\":\"run-1\",\"decisionRequestId\":\"" + request +
        "\",\"basedOnObservationId\":\"observation-1\",\"decision\":{\"kind\":\"finish\",\"report\":\"goalClaimed\"}}";
    private static string Completed(string text, string turn = "turn-1", string thread = "thread-1") => new JsonObject
    {
        ["method"] = "turn/completed", ["params"] = new JsonObject { ["threadId"] = thread,
            ["turn"] = new JsonObject { ["id"] = turn, ["status"] = "completed",
                ["items"] = new JsonArray(new JsonObject { ["id"] = "message-1", ["type"] = "agentMessage", ["text"] = text }) } }
    }.ToJsonString();
    [Theory]
    [InlineData("approvalPolicy", "missing")]
    [InlineData("approvalPolicy", "null")]
    [InlineData("approvalPolicy", "\"on-request\"")]
    [InlineData("approvalPolicy", "{}")]
    [InlineData("sandbox", "missing")]
    [InlineData("sandbox", "null")]
    [InlineData("sandbox", "{\"type\":\"workspaceWrite\"}")]
    [InlineData("sandbox", "{\"type\":\"dangerFullAccess\"}")]
    [InlineData("sandbox", "{\"type\":\"readOnly\",\"networkAccess\":true}")]
    [InlineData("sandbox", "{\"type\":\"readOnly\",\"networkAccess\":null}")]
    [InlineData("sandbox", "{\"type\":\"readOnly\",\"networkAccess\":\"false\"}")]
    public async Task EffectiveThreadRestrictionsMustMatchBeforeSendingPlannerInput(string field, string value)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]);
        var response = JsonNode.Parse(Encoding.UTF8.GetString(frames[1]!))!;
        var result = response["result"]!.AsObject();
        if (value == "missing") result.Remove(field); else result[field] = JsonNode.Parse(value);
        transport.Add(response.ToJsonString()); transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain(transport.Writes, x => x.Contains("turn/start") || x.Contains("public objective"));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Fact]
    public async Task ReadOnlySandboxOmittedNetworkUsesSchemaDefaultFalse()
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); var response = JsonNode.Parse(Encoding.UTF8.GetString(frames[1]!))!;
        response["result"]!["sandbox"]!.AsObject().Remove("networkAccess"); transport.Add(response.ToJsonString());
        transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.Completed, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("items", "missing")]
    [InlineData("items", "null")]
    [InlineData("items", "{}")]
    [InlineData("items", "[null]")]
    [InlineData("items", "[{\"id\":\"x\",\"type\":\"agentMessage\"}]")]
    [InlineData("status", "missing")]
    [InlineData("status", "null")]
    [InlineData("status", "\"completed\"")]
    [InlineData("status", "\"failed\"")]
    [InlineData("status", "\"interrupted\"")]
    [InlineData("status", "\"unknown\"")]
    [InlineData("itemsView", "\"unknown\"")]
    [InlineData("durationMs", "\"wrong\"")]
    [InlineData("error", "{\"message\":42}")]
    public async Task InvalidStartTurnCannotBeRescuedByLaterCompletion(string field, string value)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        var response = JsonNode.Parse(Encoding.UTF8.GetString(frames[2]!))!; var turn = response["result"]!["turn"]!.AsObject();
        if (value == "missing") turn.Remove(field); else turn[field] = JsonNode.Parse(value);
        transport.Add(response.ToJsonString()); transport.Add(Completed(Decision()));
        var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningBeforeOrAfterResponseIsBoundedNonfatalTelemetry(bool beforeResponse)
    {
        foreach (var target in new[] { "missing", "null", "\"thread-1\"" })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var warning = new JsonObject { ["method"] = "warning", ["params"] = new JsonObject { ["message"] = "PRIVATE_WARNING" } };
            if (target != "missing") warning["params"]!["threadId"] = JsonNode.Parse(target);
            transport.Add(warning.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]);
            transport.Add(Completed(Decision()));
            var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.Completed, reply.Status); Assert.DoesNotContain("PRIVATE_WARNING", reply.ToString());
            Assert.Single(transport.Writes, x => x.Contains("turn/start"));
        }
    }

    [Theory]
    [InlineData("threadId", "\"wrong\"")]
    [InlineData("threadId", "42")]
    [InlineData("message", "missing")]
    [InlineData("message", "null")]
    public async Task InvalidWarningsCloseBeforeAndAfterResponse(string field, string value)
    {
        foreach (var beforeResponse in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var parameters = new JsonObject { ["message"] = "PRIVATE_WARNING", ["threadId"] = "thread-1" };
            if (value == "missing") parameters.Remove(field); else parameters[field] = JsonNode.Parse(value);
            transport.Add(new JsonObject { ["method"] = "warning", ["params"] = parameters }.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]);
            transport.Add(Completed(Decision())); var backend = Backend(transport);
            var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "interrupted")]
    [InlineData(true, "interrupted")]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    public async Task NonRetryableErrorRequiresFailedTerminalStatus(bool beforeResponse, string status)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        var error = JsonNode.Parse(RetryNotice())!; error["params"]!["willRetry"] = false;
        transport.Add(error.ToJsonString());
        // Later recoverable telemetry must not erase already terminal failure evidence.
        transport.Add(RetryNotice());
        if (beforeResponse) transport.Frames.Enqueue(frames[2]);
        var completion = JsonNode.Parse(Completed(Decision()))!; completion["params"]!["turn"]!["status"] = status;
        if (status == "failed") completion["params"]!["turn"]!["error"] = error["params"]!["error"]!.DeepClone();
        transport.Add(completion.ToJsonString()); var backend = Backend(transport);
        var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(status == "failed" ? CodexReplyStatus.ConnectionFailure : CodexReplyStatus.OutputInvalid, reply.Status);
        Assert.Null(reply.CompletedJson); Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningFloodConsumesMessageLimitBeforeAndAfterResponse(bool beforeResponse)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        for (var i = 0; i < 20; i++) transport.Add("{\"method\":\"warning\",\"params\":{\"message\":\"notice\",\"threadId\":\"thread-1\"}}");
        if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        var backend = new CodexProtocolBackend("run-1", transport, new JsonObject(), new(100000, 10, TimeSpan.FromSeconds(5)), x => x);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input(), default)).Status);
    }
    [Theory]
    [InlineData(false, "different")]
    [InlineData(true, "different")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "latest")]
    [InlineData(true, "latest")]
    public async Task TerminalErrorPayloadMustMatchLatestNonRetryableNotice(bool beforeResponse, string variant)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        var notice = JsonNode.Parse(RetryNotice())!; notice["params"]!["willRetry"] = false; transport.Add(notice.ToJsonString());
        var second = JsonNode.Parse(RetryNotice())!; second["params"]!["willRetry"] = false;
        second["params"]!["error"] = new JsonObject { ["codexErrorInfo"] = "usageLimitExceeded", ["message"] = "PRIVATE_ERROR" };
        if (variant == "latest") transport.Add(second.ToJsonString());
        transport.Add(RetryNotice());
        if (beforeResponse) transport.Frames.Enqueue(frames[2]);
        var completion = JsonNode.Parse(Completed(Decision()))!; var turn = completion["params"]!["turn"]!;
        turn["status"] = "failed";
        if (variant == "different" || variant == "latest")
            turn["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = "usageLimitExceeded" };
        else if (variant == "null") turn["error"] = null;
        transport.Add(completion.ToJsonString()); var backend = Backend(transport);
        var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(variant == "latest" ? CodexReplyStatus.UsageLimit : CodexReplyStatus.OutputInvalid, reply.Status);
        Assert.Null(reply.CompletedJson); Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    private static string RerouteNotice() => "{\"method\":\"model/rerouted\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"fromModel\":\"a\",\"toModel\":\"b\",\"reason\":\"highRiskCyberActivity\"}}";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelRerouteBeforeOrAfterResponseRequiresValidCorrelationAndShape(bool beforeResponse)
    {
        foreach (var field in new[] { "valid", "threadId", "turnId", "fromModel", "toModel", "reason", "wrongThread", "wrongTurn", "wrongReason", "nullModel" })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var notice = JsonNode.Parse(RerouteNotice())!;
            if (field == "wrongThread") notice["params"]!["threadId"] = "wrong";
            else if (field == "wrongTurn") notice["params"]!["turnId"] = "wrong";
            else if (field == "wrongReason") notice["params"]!["reason"] = "unknown";
            else if (field == "nullModel") notice["params"]!["toModel"] = null;
            else if (field != "valid") notice["params"]!.AsObject().Remove(field);
            transport.Add(notice.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(field == "valid" ? CodexReplyStatus.Completed : CodexReplyStatus.OutputInvalid, reply.Status);
        }
    }
    [Fact]
    public async Task RerouteTelemetryConsumesPreResponseAggregateBound()
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        for (var i = 0; i < 80; i++) transport.Add(RerouteNotice());
        transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticContextResetLeavesCurrentAndSubsequentUsageUnavailable(bool beforeResponse)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        transport.Add(Usage("turn-1", 7, 5, 12));
        var reset = JsonNode.Parse(Usage("turn-1", 0, 0, 100000))!;
        reset["params"]!["tokenUsage"]!["last"] = JsonNode.Parse("{\"inputTokens\":0,\"cachedInputTokens\":0,\"outputTokens\":0,\"reasoningOutputTokens\":0,\"totalTokens\":99988}");
        reset["params"]!["tokenUsage"]!["modelContextWindow"] = 100000;
        transport.Add(reset.ToJsonString());
        transport.Add(Usage("turn-1", 3, 2, 100005));
        if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        var backend = Backend(transport); var first = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, first.Status); Assert.Null(first.Usage);
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\",\"status\":\"inProgress\",\"items\":[]}}}");
        transport.Add(Usage("turn-2", 10, 8, 100018)); transport.Add(Completed(Decision("request-2"), "turn-2"));
        var second = await backend.DecideAsync(Input("request-2"), default);
        Assert.Equal(CodexReplyStatus.Completed, second.Status); Assert.Null(second.Usage);
    }
    private static string PassiveTelemetry(string method) => new JsonObject
    {
        ["method"] = method, ["params"] = method switch
        {
            "rawResponse/completed" => new JsonObject { ["responseId"] = "response-1" },
            "rawResponseItem/completed" => new JsonObject { ["item"] = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray() } },
            "model/verification" => new JsonObject { ["verifications"] = new JsonArray("trustedAccessForCyber") },
            _ => new JsonObject { ["model"] = "a", ["reasons"] = new JsonArray(), ["useCases"] = new JsonArray(), ["showBufferingUi"] = false }
        }
    }.ToJsonString();
    [Theory]
    [InlineData("rawResponse/completed")]
    [InlineData("rawResponseItem/completed")]
    [InlineData("model/verification")]
    [InlineData("model/safetyBuffering/updated")]
    public async Task PassiveTelemetryIsValidatedBeforeAndAfterResponse(string method)
    {
        foreach (var beforeResponse in new[] { true, false })
        foreach (var variant in new[] { "valid", "wrongThread", "wrongTurn", "missing", "null" })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var notice = JsonNode.Parse(PassiveTelemetry(method))!; var parameters = notice["params"]!.AsObject();
            parameters["threadId"] = variant == "wrongThread" ? "wrong" : "thread-1";
            parameters["turnId"] = variant == "wrongTurn" ? "wrong" : "turn-1";
            var required = method switch { "rawResponse/completed" => "responseId", "rawResponseItem/completed" => "item", "model/verification" => "verifications", _ => "showBufferingUi" };
            if (variant == "missing") parameters.Remove(required); else if (variant == "null") parameters[required] = null;
            transport.Add(notice.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(variant == "valid" ? CodexReplyStatus.Completed : CodexReplyStatus.OutputInvalid, reply.Status);
            if (variant == "valid") Assert.Null(reply.Usage); else Assert.Null(reply.CompletedJson);
        }
    }
    [Theory]
    [InlineData("rawResponse/completed")]
    [InlineData("rawResponseItem/completed")]
    [InlineData("model/verification")]
    [InlineData("model/safetyBuffering/updated")]
    public async Task PassiveTelemetryCannotHydrateProposalOrExceedBuffer(string method)
    {
        foreach (var flood in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
            var notice = JsonNode.Parse(PassiveTelemetry(method))!; notice["params"]!["threadId"] = "thread-1"; notice["params"]!["turnId"] = "turn-1";
            if (flood && method == "rawResponse/completed") notice["params"]!["responseId"] = new string('r', 500);
            for (var i = 0; i < (flood ? 90 : 1); i++) transport.Add(notice.ToJsonString());
            transport.Frames.Enqueue(frames[2]); transport.Add(Summary(Decision()));
            var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(flood ? CodexReplyStatus.OutputInvalid : CodexReplyStatus.ConnectionFailure, reply.Status);
            Assert.Null(reply.CompletedJson);
        }
    }
    [Theory]
    [InlineData(6, 6, 12)]
    [InlineData(8, 4, 12)]
    [InlineData(8, 5, 11)]
    [InlineData(6, 6, 11)]
    public async Task OpposingUsageComponentsInvalidateCurrentAndLaterEpoch(long input, long output, long total)
    {
        var transport = Started(); transport.Add(Usage("turn-1", 7, 5, 12));
        transport.Add(Usage("turn-1", input, output, total)); transport.Add(Completed(Decision()));
        var backend = Backend(transport); var first = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, first.Status); Assert.Null(first.Usage);
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\",\"status\":\"inProgress\",\"items\":[]}}}");
        transport.Add(Usage("turn-2", 10, 8, 18)); transport.Add(Completed(Decision("request-2"), "turn-2"));
        var second = await backend.DecideAsync(Input("request-2"), default);
        Assert.Equal(CodexReplyStatus.Completed, second.Status); Assert.Null(second.Usage);
    }
    private static string StartupNotice(string method) => new JsonObject
    {
        ["method"] = method, ["params"] = method == "guardianWarning"
            ? new JsonObject { ["message"] = "PRIVATE_NOTICE", ["threadId"] = "thread-1" }
            : new JsonObject { ["summary"] = "PRIVATE_NOTICE", ["details"] = null }
    }.ToJsonString();
    [Theory]
    [InlineData("deprecationNotice")]
    [InlineData("guardianWarning")]
    [InlineData("configWarning")]
    public async Task NonfatalStartupNoticesAreValidatedAcrossThreadAndTurnResponses(string method)
    {
        foreach (var phase in new[] { "threadBefore", "threadAfter", "turnBefore", "turnAfter" })
        foreach (var invalid in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            var notice = JsonNode.Parse(StartupNotice(method))!;
            if (invalid) notice["params"]!.AsObject().Remove(method == "guardianWarning" ? "message" : "summary");
            transport.Frames.Enqueue(frames[0]);
            if (phase == "threadBefore")
            {
                transport.Add(new JsonObject { ["method"] = "thread/started", ["params"] = new JsonObject { ["thread"] = ThreadStartResult()["thread"]!.DeepClone() } }.ToJsonString());
                transport.Add(notice.ToJsonString());
            }
            transport.Frames.Enqueue(frames[1]);
            if (phase == "threadAfter" || phase == "turnBefore") transport.Add(notice.ToJsonString());
            transport.Frames.Enqueue(frames[2]);
            if (phase == "turnAfter") transport.Add(notice.ToJsonString());
            transport.Add(Completed(Decision())); var backend = Backend(transport);
            var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(invalid ? CodexReplyStatus.OutputInvalid : CodexReplyStatus.Completed, reply.Status);
            Assert.DoesNotContain("PRIVATE_NOTICE", reply.ToString());
            if (invalid) Assert.Null(reply.CompletedJson);
        }
    }
    [Theory]
    [InlineData("deprecationNotice")]
    [InlineData("guardianWarning")]
    [InlineData("configWarning")]
    public async Task NonfatalStartupNoticeFloodConsumesExistingMessageBound(string method)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]);
        transport.Add(new JsonObject { ["method"] = "thread/started", ["params"] = new JsonObject { ["thread"] = ThreadStartResult()["thread"]!.DeepClone() } }.ToJsonString());
        for (var i = 0; i < 20; i++) transport.Add(StartupNotice(method));
        transport.Frames.Enqueue(frames[1]); transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        var backend = Backend(transport, limits: new(10000, 10, TimeSpan.FromSeconds(5)));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input(), default)).Status);
        Assert.DoesNotContain(transport.Writes, x => x.Contains("public objective"));
    }
    [Theory]
    [InlineData("hook/started", false)]
    [InlineData("hook/started", true)]
    [InlineData("hook/completed", false)]
    [InlineData("hook/completed", true)]
    public async Task InheritedHookExecutionIsRefusedBeforeAndAfterTurnResponse(string method, bool beforeResponse)
    {
        foreach (var threadScoped in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            transport.Add(new JsonObject { ["method"] = method, ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1", ["turnId"] = threadScoped ? null : "turn-1",
                ["run"] = new JsonObject { ["id"] = "hook-1", ["displayOrder"] = 1,
                    ["entries"] = new JsonArray(), ["eventName"] = "userPromptSubmit",
                    ["executionMode"] = "sync", ["handlerType"] = "command",
                    ["scope"] = threadScoped ? "thread" : "turn", ["sourcePath"] = "/public/hooks.json",
                    ["startedAt"] = 1, ["status"] = method == "hook/started" ? "running" : "completed" }
            } }.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson); Assert.Null(reply.Usage);
            Assert.Equal(PlannerReplyStatus.ConnectionFailure, (await new CodexPlannerAdapter(new ReplyBackend(reply)).DecideAsync(Input(), default)).Status);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
            Assert.DoesNotContain(transport.Writes, x => x.Contains("hooks.json"));
        }
    }
    private static string RetryNotice() => new JsonObject
    {
        ["method"] = "error", ["params"] = new JsonObject { ["threadId"] = "thread-1", ["turnId"] = "turn-1",
            ["willRetry"] = true, ["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR",
                ["codexErrorInfo"] = new JsonObject { ["responseStreamDisconnected"] = new JsonObject { ["httpStatusCode"] = 503 } } } }
    }.ToJsonString();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrelatedRetryNoticeBeforeOrAfterStartResponseAllowsTerminalCompletion(bool beforeResponse)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        transport.Add(RetryNotice());
        if (beforeResponse) transport.Frames.Enqueue(frames[2]);
        transport.Add(Completed(Decision())); var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, reply.Status); Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        Assert.Single(transport.Writes, x => x.Contains("turn/start"));
    }

    [Theory]
    [InlineData("threadId", "\"wrong\"")]
    [InlineData("turnId", "\"wrong\"")]
    [InlineData("willRetry", "missing")]
    [InlineData("willRetry", "\"true\"")]
    [InlineData("error", "{\"message\":\"PRIVATE_ERROR\",\"codexErrorInfo\":\"unknown\"}")]
    public async Task InvalidRetryNoticesCloseBeforeAndAfterStartResponse(string field, string value)
    {
        foreach (var beforeResponse in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var notice = JsonNode.Parse(RetryNotice())!; var parameters = notice["params"]!.AsObject();
            if (value == "missing") parameters.Remove(field); else parameters[field] = JsonNode.Parse(value);
            transport.Add(notice.ToJsonString()); if (beforeResponse) transport.Frames.Enqueue(frames[2]);
            transport.Add(Completed(Decision())); var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
        }
    }

    [Fact]
    public async Task PreResponseRetryTelemetryConsumesAggregateBufferLimit()
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        for (var index = 0; index < 60; index++) transport.Add(RetryNotice());
        transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"httpConnectionFailed\":null}")]
    [InlineData("{\"httpConnectionFailed\":{\"httpStatusCode\":\"503\"}}")]
    [InlineData("{\"httpConnectionFailed\":{\"httpStatusCode\":-1}}")]
    [InlineData("{\"httpConnectionFailed\":{\"httpStatusCode\":65536}}")]
    [InlineData("{\"activeTurnNotSteerable\":{}}")]
    [InlineData("{\"activeTurnNotSteerable\":{\"turnKind\":\"unknown\"}}")]
    [InlineData("{\"httpConnectionFailed\":{},\"other\":{}}")]
    public async Task MalformedErrorUnionCannotMasqueradeAsBackendFailure(string info)
    {
        foreach (var status in new[] { "failed", "interrupted", "completed" })
        {
            var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
            message["params"]!["turn"]!["status"] = status;
            message["params"]!["turn"]!["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = JsonNode.Parse(info) };
            transport.Add(message.ToJsonString()); var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"other\"")]
    [InlineData("\"serverOverloaded\"")]
    [InlineData("{\"httpConnectionFailed\":{}}")]
    [InlineData("{\"responseStreamConnectionFailed\":{\"httpStatusCode\":null}}")]
    [InlineData("{\"responseStreamDisconnected\":{\"httpStatusCode\":503}}")]
    [InlineData("{\"responseTooManyFailedAttempts\":{\"httpStatusCode\":65535}}")]
    [InlineData("{\"activeTurnNotSteerable\":{\"turnKind\":\"review\"}}")]
    public async Task ValidErrorUnionRetainsSafeConnectionFailure(string info)
    {
        var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
        message["params"]!["turn"]!["status"] = "failed";
        message["params"]!["turn"]!["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = JsonNode.Parse(info) };
        transport.Add(message.ToJsonString()); var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
    }

    [Theory]
    [InlineData("approvalsReviewer", false)]
    [InlineData("cwd", false)]
    [InlineData("model", false)]
    [InlineData("modelProvider", false)]
    [InlineData("cliVersion", true)]
    [InlineData("createdAt", true)]
    [InlineData("updatedAt", true)]
    [InlineData("cwd", true)]
    [InlineData("modelProvider", true)]
    [InlineData("preview", true)]
    [InlineData("projectId", true)]
    [InlineData("sessionId", true)]
    [InlineData("source", true)]
    [InlineData("status", true)]
    [InlineData("turns", true)]
    public async Task CompleteThreadStartShapeIsRequiredBeforeInput(string field, bool nested)
    {
        foreach (var missing in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); var result = ThreadStartResult();
            var target = nested ? result["thread"]!.AsObject() : result;
            if (missing) target.Remove(field); else target[field] = new JsonObject();
            transport.Add(new JsonObject { ["id"] = 2, ["result"] = result }.ToJsonString());
            transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.DoesNotContain(transport.Writes, x => x.Contains("turn/start") || x.Contains("public objective"));
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    [Theory]
    [InlineData("source", "\"unknown-source\"")]
    [InlineData("status", "{\"type\":\"active\"}")]
    [InlineData("createdAt", "9223372036854775808")]
    [InlineData("updatedAt", "1.5")]
    [InlineData("turns", "[null]")]
    [InlineData("turns", "[{\"id\":\"old-turn\",\"items\":[],\"status\":\"completed\"}]")]
    public async Task InvalidOrHistoricalThreadStateCannotOpenNewRun(string field, string value)
    {
        foreach (var noticeFirst in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); var result = ThreadStartResult();
            result["thread"]![field] = JsonNode.Parse(value);
            if (noticeFirst)
            {
                transport.Add(new JsonObject { ["method"] = "thread/started", ["params"] = new JsonObject
                    { ["thread"] = result["thread"]!.DeepClone() } }.ToJsonString());
                transport.Frames.Enqueue(frames[1]);
            }
            else transport.Add(new JsonObject { ["id"] = 2, ["result"] = result }.ToJsonString());
            transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.DoesNotContain(transport.Writes, x => x.Contains("turn/start"));
        }
    }

    [Fact]
    public async Task FullyShapedThreadNoticeMatchesResponseAndAllowsTurn()
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]);
        transport.Add(new JsonObject { ["method"] = "thread/started", ["params"] = new JsonObject
            { ["thread"] = ThreadStartResult()["thread"]!.DeepClone() } }.ToJsonString());
        transport.Frames.Enqueue(frames[1]); transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.Completed, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task CanonicalCompletionTimestampIsRequiredBeforeCaching(string value)
    {
        foreach (var beforeResponse in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var notice = JsonNode.Parse(CanonicalItem(Decision()))!; var parameters = notice["params"]!.AsObject();
            if (value == "missing") parameters.Remove("completedAtMs"); else parameters["completedAtMs"] = JsonNode.Parse(value);
            transport.Add(notice.ToJsonString()); transport.Add(Summary(Decision()));
            if (beforeResponse) transport.Frames.Enqueue(frames[2]);
            var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("interrupted")]
    [InlineData("inProgress")]
    public async Task NonFailedTurnCannotCarryValidBackendError(string status)
    {
        foreach (var info in new[] { "\"usageLimitExceeded\"", "{\"httpConnectionFailed\":{}}" })
        {
            var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
            message["params"]!["turn"]!["status"] = status;
            message["params"]!["turn"]!["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = JsonNode.Parse(info) };
            transport.Add(message.ToJsonString()); var reply = await Backend(transport).DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        }
    }

    [Theory]
    [InlineData("id", "\"other-turn\"")]
    [InlineData("id", "missing")]
    [InlineData("status", "missing")]
    [InlineData("status", "\"completed\"")]
    [InlineData("items", "missing")]
    [InlineData("items", "[null]")]
    public async Task TurnStartedUsesSameShapeAndIdentityBeforeAndAfterRpcResponse(string field, string value)
    {
        foreach (var beforeResponse in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var turn = new JsonObject { ["id"] = "turn-1", ["items"] = new JsonArray(), ["status"] = "inProgress" };
            if (value == "missing") turn.Remove(field); else turn[field] = JsonNode.Parse(value);
            transport.Add(new JsonObject { ["method"] = "turn/started", ["params"] = new JsonObject
                { ["threadId"] = "thread-1", ["turn"] = turn } }.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]);
            transport.Add(Completed(Decision())); var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    [Fact]
    public async Task ValidPostResponseTurnStartedAllowsCorrelatedCompletion()
    {
        var transport = Started(); transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.Completed, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("item/agentMessage/delta")]
    [InlineData("turn/plan/updated")]
    public async Task NeighboringPostResponseStreamNoticesRequireTurnCorrelation(string method)
    {
        var transport = Started(); transport.Add(new JsonObject { ["method"] = method, ["params"] = new JsonObject
            { ["threadId"] = "thread-1", ["turnId"] = "other-turn", ["itemId"] = "message-1", ["delta"] = "text", ["plan"] = new JsonArray() } }.ToJsonString());
        transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("threadId", "missing")]
    [InlineData("threadId", "\"other-thread\"")]
    [InlineData("status", "missing")]
    [InlineData("status", "{\"type\":\"active\"}")]
    [InlineData("status", "{\"type\":\"unknown\"}")]
    public async Task ThreadStatusNoticesUseSameValidationBeforeAndAfterResponse(string field, string value)
    {
        foreach (var beforeResponse in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
            if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
            var parameters = new JsonObject { ["threadId"] = "thread-1", ["status"] = new JsonObject { ["type"] = "idle" } };
            if (value == "missing") parameters.Remove(field); else parameters[field] = JsonNode.Parse(value);
            transport.Add(new JsonObject { ["method"] = "thread/status/changed", ["params"] = parameters }.ToJsonString());
            if (beforeResponse) transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
        }
    }

    [Theory]
    [InlineData("status", "{\"type\":\"systemError\"}")]
    [InlineData("status", "{\"type\":\"notLoaded\"}")]
    [InlineData("status", "{\"type\":\"active\",\"activeFlags\":[]}")]
    [InlineData("cliVersion", "\"0.149.0\"")]
    [InlineData("cliVersion", "\"0.150.10\"")]
    [InlineData("cliVersion", "\"\"")]
    public async Task FreshThreadMustBeIdleAndMatchSupportedCliBeforeInput(string field, string value)
    {
        foreach (var noticeFirst in new[] { false, true })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]); var result = ThreadStartResult(); result["thread"]![field] = JsonNode.Parse(value);
            if (noticeFirst)
            {
                transport.Add(new JsonObject { ["method"] = "thread/started", ["params"] = new JsonObject
                    { ["thread"] = result["thread"]!.DeepClone() } }.ToJsonString());
                transport.Frames.Enqueue(frames[1]);
            }
            else transport.Add(new JsonObject { ["id"] = 2, ["result"] = result }.ToJsonString());
            transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
            var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.DoesNotContain(transport.Writes, x => x.Contains("turn/start") || x.Contains("public objective"));
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    private static JsonObject ThreadStartResult() => new()
    {
        ["approvalPolicy"] = "never", ["approvalsReviewer"] = "user", ["cwd"] = "/public",
        ["model"] = "test-model", ["modelProvider"] = "openai",
        ["sandbox"] = new JsonObject { ["type"] = "readOnly", ["networkAccess"] = false },
        ["thread"] = new JsonObject
        {
            ["id"] = "thread-1", ["ephemeral"] = true, ["cliVersion"] = "0.150.1",
            ["createdAt"] = 1, ["updatedAt"] = 1, ["cwd"] = "/public", ["modelProvider"] = "openai",
            ["preview"] = "", ["projectId"] = null, ["sessionId"] = "session-1", ["source"] = "appServer",
            ["status"] = new JsonObject { ["type"] = "idle" }, ["turns"] = new JsonArray()
        }
    };
    private static string InitializeResult(string agent) => new JsonObject
    {
        ["id"] = 1, ["result"] = new JsonObject { ["userAgent"] = agent, ["codexHome"] = "/codex",
            ["platformFamily"] = "unix", ["platformOs"] = "linux" }
    }.ToJsonString();
    [Theory]
    [InlineData("codexHome")]
    [InlineData("platformFamily")]
    [InlineData("platformOs")]
    public async Task AdjacentInitializeResponseRequiresCompletePinnedShape(string field)
    {
        foreach (var value in new[] { "missing", "null", "42", "{}" })
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            var response = JsonNode.Parse(Encoding.UTF8.GetString(frames[0]!))!; var result = response["result"]!.AsObject();
            if (value == "missing") result.Remove(field); else result[field] = JsonNode.Parse(value);
            transport.Add(response.ToJsonString()); transport.Frames.Enqueue(frames[1]); transport.Frames.Enqueue(frames[2]);
            transport.Add(Completed(Decision())); var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.Single(transport.Writes); Assert.Contains("initialize", transport.Writes[0]);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }
    private static Transport Started()
    {
        var transport = new Transport();
        transport.Add(InitializeResult("codex/0.150.1"));
        transport.Add(new JsonObject { ["id"] = 2, ["result"] = ThreadStartResult() }.ToJsonString());
        transport.Add("{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        return transport;
    }
    private static CodexProtocolBackend Backend(Transport transport, Func<string, string>? redact = null,
        CodexProtocolLimits? limits = null) => new("run-1", transport, new JsonObject { ["type"] = "object" },
            limits ?? new(10000, 100, TimeSpan.FromSeconds(5)), redact ?? (x => x));

    [Theory]
    [InlineData("missing")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    public async Task ThreadMustConfirmEphemeralBeforePlannerInput(string value)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]);
        var response = JsonNode.Parse(Encoding.UTF8.GetString(frames[1]!))!;
        var thread = response["result"]!["thread"]!.AsObject();
        if (value == "missing") thread.Remove("ephemeral"); else thread["ephemeral"] = JsonNode.Parse(value);
        transport.Add(response.ToJsonString()); transport.Frames.Enqueue(frames[2]);
        transport.Add(Completed(Decision())); var backend = Backend(transport);
        var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain(transport.Writes, x => x.Contains("turn/start"));
        Assert.DoesNotContain(transport.Writes, x => x.Contains("public objective"));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedUsageAndRateLimitTelemetryBeforeResponseRemainBounded(bool overflow)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        transport.Add(Usage("turn-1", 3, 2, 5));
        for (var index = 0; index < (overflow ? 100 : 1); index++)
            transport.Add("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{}}}");
        transport.Add(CanonicalItem(Decision())); transport.Add(Summary(Decision())); transport.Frames.Enqueue(frames[2]);
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(overflow ? CodexReplyStatus.OutputInvalid : CodexReplyStatus.Completed, reply.Status);
        if (overflow) Assert.Null(reply.CompletedJson); else Assert.Equal(new CodexUsage(3, 2, 5), reply.Usage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"x\",\"type\":\"unknown\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"agentMessage\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"agentMessage\",\"text\":12}")]
    [InlineData("{\"id\":\"x\",\"type\":\"reasoning\",\"summary\":[null]}")]
    [InlineData("{\"id\":\"x\",\"type\":\"commandExecution\",\"command\":\"x\"}")]
    public async Task InvalidItemMembersCannotBecomeBackendTerminalEvidence(string invalid)
    {
        foreach (var status in new[] { "completed", "failed", "interrupted" })
        {
            var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
            var turn = message["params"]!["turn"]!; turn["status"] = status;
            turn["items"]!.AsArray().Add(JsonNode.Parse(invalid));
            turn["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = "usageLimitExceeded" };
            transport.Add(message.ToJsonString()); var backend = Backend(transport);
            var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        }
    }

    [Fact]
    public async Task InvalidCanonicalItemCannotHydrateSummary()
    {
        var transport = Started(); var item = JsonNode.Parse(CanonicalItem(Decision()))!;
        item["params"]!["item"]!["phase"] = "unknown";
        transport.Add(item.ToJsonString()); transport.Add(Summary(Decision()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Fact]
    public async Task OnlyCompletedCorrelatedMessageBecomesReplyAndCliCopiesBytes()
    {
        var transport = Started();
        transport.Add("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"delta\":\"PRIVATE_REASONING\"}}");
        transport.Add(Completed(Decision()));
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, reply.Status);
        Assert.Equal(Decision(), Encoding.UTF8.GetString(reply.CompletedJson!));
        Assert.Null(reply.Usage);
        Assert.Equal(new[] { "initialize", "initialized", "thread/start", "turn/start" },
            transport.Writes.Select(x => JsonNode.Parse(x)!["method"]!.GetValue<string>()));
        Assert.All(transport.Writes, x => Assert.EndsWith("\n", x));
        var request = JsonNode.Parse(transport.Writes.Last())!["params"]!;
        Assert.Null(request["developerInstructions"]);
        Assert.False(request["sandboxPolicy"]!["networkAccess"]!.GetValue<bool>());
        Assert.DoesNotContain("PRIVATE_REASONING", Encoding.UTF8.GetString(reply.CompletedJson!));
        var adapterReply = await new CodexPlannerAdapter(new ReplyBackend(reply)).DecideAsync(Input(), default);
        Assert.Equal(PlannerReplyStatus.Completed, adapterReply.Status);
        reply.CompletedJson![0] = 0;
        Assert.Equal((byte)'{', adapterReply.CompletedJson![0]);
    }
    private sealed class ReplyBackend(CodexReply reply) : ICodexDecisionBackend
    { public ValueTask<CodexReply> DecideAsync(PlannerInputDocument input, CancellationToken token) => new(reply); }

    private static string CanonicalItem(string text) => new JsonObject
    {
        ["method"] = "item/completed", ["params"] = new JsonObject
        { ["threadId"] = "thread-1", ["turnId"] = "turn-1", ["completedAtMs"] = 1, ["item"] = new JsonObject
            { ["id"] = "message-1", ["type"] = "agentMessage", ["text"] = text } }
    }.ToJsonString();
    private static string Summary(string text)
    {
        var message = JsonNode.Parse(Completed(text))!;
        message["params"]!["turn"]!["itemsView"] = "summary";
        return message.ToJsonString();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSummaryHydratesCanonicalOutputWithoutPersistedHistory(bool beforeResponse)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        if (!beforeResponse) transport.Frames.Enqueue(frames[2]);
        transport.Add(CanonicalItem(Decision())); transport.Add(Usage("turn-1", 3, 2, 5));
        transport.Add(Summary(Decision()));
        if (beforeResponse) transport.Frames.Enqueue(frames[2]);
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, reply.Status); Assert.Equal(new CodexUsage(3, 2, 5), reply.Usage);
        Assert.Equal(Decision(), Encoding.UTF8.GetString(reply.CompletedJson!));
        Assert.True(JsonNode.Parse(transport.Writes[2])!["params"]!["ephemeral"]!.GetValue<bool>());
        Assert.DoesNotContain(transport.Writes, x => x.Contains("thread/read"));
    }
    [Fact]
    public async Task SummaryCannotReplaceCanonicalItemText()
    {
        var transport = Started(); transport.Add(CanonicalItem(Decision()));
        transport.Add(Summary(Decision().Replace("goalClaimed", "inconclusive")));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }
    [Fact]
    public async Task PreResponseBufferCannotExceedAggregateByteLimit()
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        for (var index = 0; index < 40; index++) transport.Add(Usage("turn-1", 3, 2, 5));
        transport.Frames.Enqueue(frames[2]);
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
    }
    [Fact]
    public async Task CanonicalItemCacheCannotExceedAggregateByteLimit()
    {
        var transport = Started();
        for (var index = 0; index < 60; index++)
        {
            var message = JsonNode.Parse(CanonicalItem(Decision()))!;
            message["params"]!["item"]!["id"] = "message-" + index;
            transport.Add(message.ToJsonString());
        }
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
    }
    [Theory]
    [InlineData("threadId")]
    [InlineData("turnId")]
    public async Task StalePreResponseItemNeverSurvivesReplay(string key)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}");
        var message = JsonNode.Parse(CanonicalItem(Decision()))!; message["params"]![key] = "wrong";
        transport.Add(message.ToJsonString()); transport.Frames.Enqueue(frames[2]);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }
    [Theory]
    [InlineData("last")]
    [InlineData("total")]
    public async Task MissingUsageBreakdownIsInvalid(string key)
    {
        var transport = Started(); var message = JsonNode.Parse(Usage("turn-1", 3, 2, 5))!;
        message["params"]!["tokenUsage"]!.AsObject().Remove(key); transport.Add(message.ToJsonString());
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }
    [Theory]
    [InlineData("last", "inputTokens")]
    [InlineData("last", "cachedInputTokens")]
    [InlineData("last", "reasoningOutputTokens")]
    [InlineData("total", "cachedInputTokens")]
    [InlineData("total", "reasoningOutputTokens")]
    public async Task MissingRequiredUsageCounterNeverReturnsAProposal(string breakdown, string counter)
    {
        var transport = Started(); var message = JsonNode.Parse(Usage("turn-1", 3, 2, 5))!;
        message["params"]!["tokenUsage"]![breakdown]!.AsObject().Remove(counter);
        transport.Add(message.ToJsonString()); transport.Add(Completed(Decision()));
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndTurnStartedBeforeRpcResponseAreCorrelated(bool stale)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]); transport.Frames.Enqueue(frames[1]);
        transport.Add(new JsonObject { ["method"] = "thread/status/changed", ["params"] = new JsonObject
            { ["threadId"] = stale ? "other-thread" : "thread-1", ["status"] = new JsonObject
                { ["type"] = "active", ["activeFlags"] = new JsonArray() } } }.ToJsonString());
        transport.Add("{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"inProgress\"}}}");
        transport.Frames.Enqueue(frames[2]); transport.Add(Completed(Decision()));
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(stale ? CodexReplyStatus.OutputInvalid : CodexReplyStatus.Completed, reply.Status);
        if (stale) Assert.Null(reply.CompletedJson);
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("summary", false)]
    [InlineData("notLoaded", false)]
    public async Task OnlyFullTurnViewCanSupplyFinalProposal(string view, bool valid)
    {
        var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
        message["params"]!["turn"]!["itemsView"] = view; transport.Add(message.ToJsonString());
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(valid ? CodexReplyStatus.Completed : view == "summary" ? CodexReplyStatus.ConnectionFailure : CodexReplyStatus.OutputInvalid, reply.Status);
        if (!valid) Assert.Null(reply.CompletedJson);
    }

    [Theory]
    [InlineData("thread/started")]
    [InlineData("turn/started")]
    public async Task AnnouncedLifecycleIdentityMustMatchStartResponse(string method)
    {
        var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Frames.Enqueue(frames[0]);
        if (method == "turn/started") transport.Frames.Enqueue(frames[1]);
        var noticeThread = ThreadStartResult()["thread"]!.DeepClone(); noticeThread["id"] = "wrong-thread";
        transport.Add(method == "thread/started"
            ? new JsonObject { ["method"] = method, ["params"] = new JsonObject { ["thread"] = noticeThread } }.ToJsonString()
            : "{\"method\":\"turn/started\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"wrong-turn\",\"items\":[],\"status\":\"inProgress\"}}}");
        transport.Frames.Enqueue(frames[method == "thread/started" ? 1 : 2]);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Fact]
    public async Task PostInitializeNotificationsAreConsumedWithinMessageBounds()
    {
        Transport WithNotices()
        {
            var transport = Started(); var frames = transport.Frames.ToArray(); transport.Frames.Clear();
            transport.Frames.Enqueue(frames[0]);
            transport.Add("{\"method\":\"configWarning\",\"params\":{\"summary\":\"PRIVATE_CONFIG\",\"details\":null}}");
            transport.Add("{\"method\":\"remoteControl/status/changed\",\"params\":{\"status\":{\"status\":\"disabled\"}}}");
            foreach (var frame in frames.Skip(1)) transport.Frames.Enqueue(frame);
            transport.Add(Completed(Decision())); return transport;
        }
        var reply = await Backend(WithNotices()).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, reply.Status);
        Assert.DoesNotContain("PRIVATE_CONFIG", reply.ToString());
        Assert.Equal(CodexReplyStatus.OutputInvalid,
            (await Backend(WithNotices(), limits: new(10000, 3, TimeSpan.FromSeconds(5))).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("interrupted", false)]
    [InlineData("interrupted", true)]
    public async Task MalformedTerminalItemsAreInvalidBeforeFailureClassification(string status, bool wrongType)
    {
        var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
        var turn = message["params"]!["turn"]!.AsObject(); turn["status"] = status;
        if (wrongType) turn["items"] = new JsonObject(); else turn.Remove("items");
        turn["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = "usageLimitExceeded" };
        transport.Add(message.ToJsonString()); var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codex/0.149.0")]
    [InlineData("codex/0.150.10")]
    [InlineData("codex/0.149.0 (client/0.150.1)")]
    public async Task IncompatibleVersionNeverStartsAThread(string? agent)
    {
        var transport = new Transport();
        var result = new JsonObject();
        if (agent is not null) result["userAgent"] = agent;
        transport.Add(new JsonObject { ["id"] = 1, ["result"] = result }.ToJsonString());
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData("gua-playtest/0.150.1 (Windows 10.0; x86_64) client (gua-playtest; 0.1.0)")]
    [InlineData("codex/0.150.1")]
    public async Task SupportedVersionAllowsClientAndOsUserAgentDetails(string agent)
    {
        var transport = Started(); transport.Frames.Dequeue();
        var remaining = transport.Frames.ToArray(); transport.Frames.Clear();
        transport.Add(InitializeResult(agent));
        foreach (var frame in remaining) transport.Frames.Enqueue(frame);
        transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.Completed, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }

    [Theory]
    [InlineData("interrupted")]
    [InlineData("sessionBudgetExceeded")]
    public async Task BackendTerminalConditionsHaveTypedFailureAndCloseSession(string condition)
    {
        var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
        var turn = message["params"]!["turn"]!;
        turn["status"] = condition == "interrupted" ? "interrupted" : "failed";
        if (condition != "interrupted") turn["error"] = new JsonObject { ["message"] = "PRIVATE_ERROR", ["codexErrorInfo"] = condition };
        transport.Add(message.ToJsonString());
        var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(condition == "interrupted" ? CodexReplyStatus.ConnectionFailure : CodexReplyStatus.UsageLimit, reply.Status);
        Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsynchronousDeliveryCannotBecomeAFinalProposal(bool includeFinal)
    {
        var transport = Started(); var message = JsonNode.Parse(Completed(Decision()))!;
        var items = message["params"]!["turn"]!["items"]!.AsArray();
        var asyncItem = items[0]!.DeepClone(); asyncItem["delivery"] = "async";
        if (!includeFinal) items.Clear();
        items.Add(asyncItem); transport.Add(message.ToJsonString());
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(includeFinal ? CodexReplyStatus.Completed : CodexReplyStatus.OutputInvalid, reply.Status);
        if (!includeFinal) Assert.Null(reply.CompletedJson);
    }

    [Fact]
    public async Task MissingTurnUsageCannotBeChargedToLaterDecisions()
    {
        var transport = Started(); transport.Add(Completed(Decision()));
        var backend = Backend(transport);
        Assert.Null((await backend.DecideAsync(Input(), default)).Usage);
        for (var turn = 2; turn <= 3; turn++)
        {
            transport.Add(new JsonObject { ["id"] = turn + 2, ["result"] = new JsonObject
                { ["turn"] = new JsonObject { ["id"] = "turn-" + turn, ["status"] = "inProgress", ["items"] = new JsonArray() } } }.ToJsonString());
            transport.Add(Usage("turn-" + turn, 10 * turn, 5 * turn, 15 * turn));
            transport.Add(Completed(Decision("request-" + turn), "turn-" + turn));
            var reply = await backend.DecideAsync(Input("request-" + turn), default);
            Assert.Equal(CodexReplyStatus.Completed, reply.Status);
            Assert.Null(reply.Usage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTurnWithMissingOrNullErrorIsAConnectionFailure(bool nullError)
    {
        var transport = Started();
        var message = JsonNode.Parse(Completed(Decision()))!;
        var turn = message["params"]!["turn"]!.AsObject();
        turn["status"] = "failed";
        if (nullError) turn["error"] = null;
        transport.Add(message.ToJsonString());
        var backend = Backend(transport);
        var reply = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status);
        Assert.Null(reply.CompletedJson);
        Assert.Equal(PlannerReplyStatus.ConnectionFailure,
            (await new CodexPlannerAdapter(new ReplyBackend(reply)).DecideAsync(Input(), default)).Status);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
    }

    [Fact]
    public async Task OneThreadPerRunAndFreshTurnsWithoutDuplicateDecisionRequests()
    {
        var transport = Started(); transport.Add(Completed(Decision()));
        var backend = Backend(transport);
        Assert.Equal(CodexReplyStatus.Completed, (await backend.DecideAsync(Input(), default)).Status);
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\",\"status\":\"inProgress\",\"items\":[]}}}");
        transport.Add(Completed(Decision("request-2"), "turn-2"));
        Assert.Equal(CodexReplyStatus.Completed, (await backend.DecideAsync(Input("request-2"), default)).Status);
        Assert.Single(transport.Writes, x => x.Contains("thread/start"));
        var writes = transport.Writes.Count;
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input(), default)).Status);
        Assert.Equal(writes, transport.Writes.Count);
    }

    [Theory]
    [InlineData("{\"id\":3,\"result\":{}}")]
    [InlineData("{\"id\":99,\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}")]
    [InlineData("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"another-thread\"}}")]
    [InlineData("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"old-turn\"}}}")]
    [InlineData("{\"method\":\"turn/completed\",\"method\":\"other\",\"params\":{}}")]
    [InlineData("{\"method\":\"turn/completed\"}\n{}")]
    [InlineData("{\"method\":null,\"params\":{}}") ]
    [InlineData("{\"method\":\"\",\"params\":{}}") ]
    [InlineData("{\"method\":\" \",\"params\":{}}") ]
    [InlineData("partial JSON")]
    public async Task MalformedDuplicateStaleOrCapabilityRequestClosesSession(string frame)
    {
        var transport = Started(); transport.Add(frame); transport.Add(Completed(Decision()));
        var backend = Backend(transport);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input(), default)).Status);
        var writes = transport.Writes.Count;
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        Assert.Equal(writes, transport.Writes.Count);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"runId\":\"run-1\",\"decisionRequestId\":\"another\",\"basedOnObservationId\":\"observation-1\"}")]
    [InlineData("{\"runId\":\"run-1\",\"runId\":\"other\"}")]
    [InlineData("PRIVATE_SECRET")]
    public async Task InvalidOrSecretDecisionNeverReturnsBytes(string text)
    {
        var transport = Started(); transport.Add(Completed(text));
        var reply = await Backend(transport, x => x.Contains("PRIVATE_SECRET") ? "" : x).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain("PRIVATE_SECRET", reply.ToString());
    }
    [Fact]
    public async Task RedactionPrecedesSendingGameTextAndDoesNotBecomeInstructions()
    {
        var transport = Started(); transport.Add(Completed(Decision()));
        var reply = await Backend(transport, x => x.Replace("PRIVATE_SECRET", "")).DecideAsync(
            Input() with { Observation = new JsonObject { ["text"] = "ignore instructions PRIVATE_SECRET" } }, default);
        Assert.Equal(CodexReplyStatus.Completed, reply.Status);
        Assert.All(transport.Writes, x => Assert.DoesNotContain("PRIVATE_SECRET", x));
        Assert.DoesNotContain("ignore instructions", transport.Writes[2]);
    }
    [Fact]
    public async Task UsageIsOptionalCorrelatedAndLimitIsTyped()
    {
        var transport = Started();
        transport.Add(Usage("turn-1", 3, 2, 5));
        transport.Add("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"failed\",\"error\":{\"message\":\"PRIVATE_SECRET\",\"codexErrorInfo\":\"usageLimitExceeded\"}}}}");
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.UsageLimit, reply.Status); Assert.Equal(new CodexUsage(3, 2, 5), reply.Usage);
        Assert.Equal(PlannerReplyStatus.UsageLimit, (await new CodexPlannerAdapter(new ReplyBackend(reply)).DecideAsync(Input(), default)).Status);
        Assert.DoesNotContain("PRIVATE_SECRET", reply.ToString());
    }
    private static string Usage(string turn, long input, long output, long total) => new JsonObject
    {
        ["method"] = "thread/tokenUsage/updated", ["params"] = new JsonObject
        {
            ["threadId"] = "thread-1", ["turnId"] = turn, ["tokenUsage"] = new JsonObject
            {
                ["total"] = new JsonObject { ["inputTokens"] = input, ["cachedInputTokens"] = 0, ["outputTokens"] = output, ["reasoningOutputTokens"] = 0, ["totalTokens"] = total },
                ["last"] = new JsonObject { ["inputTokens"] = 1, ["cachedInputTokens"] = 0, ["outputTokens"] = 1, ["reasoningOutputTokens"] = 0, ["totalTokens"] = 2 }
            }
        }
    }.ToJsonString();
    [Fact]
    public async Task UsageAccumulatesModelResponsesWithoutCountingPreviousTurnsOrDuplicateSnapshots()
    {
        var transport = Started();
        transport.Add(Usage("turn-1", 3, 2, 5));
        transport.Add(Usage("turn-1", 7, 5, 12));
        transport.Add(Usage("turn-1", 7, 5, 12));
        transport.Add(Usage("turn-1", 3, 2, 5));
        transport.Add(Completed(Decision()));
        var backend = Backend(transport);
        var first = await backend.DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.Completed, first.Status);
        Assert.Equal(new CodexUsage(7, 5, 12), first.Usage);
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\",\"status\":\"inProgress\",\"items\":[]}}}");
        transport.Add(Usage("turn-2", 10, 7, 17));
        transport.Add(Usage("turn-2", 15, 11, 26));
        transport.Add(Completed(Decision("request-2"), "turn-2"));
        var second = await backend.DecideAsync(Input("request-2"), default);
        Assert.Equal(CodexReplyStatus.Completed, second.Status);
        Assert.Equal(new CodexUsage(8, 6, 14), second.Usage);
    }
    [Theory]
    [InlineData("inputTokens")]
    [InlineData("outputTokens")]
    [InlineData("totalTokens")]
    public async Task InvalidCumulativeUsageClosesTheExchange(string counter)
    {
        var transport = Started();
        var message = JsonNode.Parse(Usage("turn-1", 3, 2, 5))!;
        message["params"]!["tokenUsage"]!["total"]![counter] = -1;
        transport.Add(message.ToJsonString());
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status);
        Assert.Null(reply.CompletedJson);
    }
    [Fact]
    public async Task DisconnectAndRawTransportExceptionsAreSafeFailures()
    {
        var transport = Started();
        Assert.Equal(CodexReplyStatus.ConnectionFailure, (await Backend(transport).DecideAsync(Input(), default)).Status);
        transport = new Transport { Read = _ => throw new IOException("PRIVATE_SECRET") };
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.DoesNotContain("PRIVATE_SECRET", reply.ToString());
    }
    [Fact]
    public async Task CancellationDiscardsUncooperativeLateReplyAndPermanentlyClosesSession()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = Started();
        transport.Read = _ => { cancellation.Cancel(); return new(Encoding.UTF8.GetBytes(Completed(Decision()))); };
        var backend = Backend(transport);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => backend.DecideAsync(Input(), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var writes = transport.Writes.Count;
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        Assert.Equal(writes, transport.Writes.Count);
    }
    [Fact]
    public async Task ConcurrentCallCannotConsumeActiveResponse()
    {
        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Read = _ => new(pending.Task) }; var backend = Backend(transport);
        var first = backend.DecideAsync(Input(), default).AsTask();
        Assert.Equal(CodexReplyStatus.ConnectionFailure, (await backend.DecideAsync(Input("request-2"), default)).Status);
        Assert.Single(transport.Writes);
        pending.SetResult(null); Assert.Equal(CodexReplyStatus.ConnectionFailure, (await first).Status);
    }
    [Fact]
    public async Task FrameAndNotificationBoundsRejectWithoutReturningProposal()
    {
        var transport = Started(); transport.Frames.Enqueue(new byte[10001]);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
        transport = Started(); transport.Add(Completed(Decision()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport, limits: new(10000, 3, TimeSpan.FromSeconds(5))).DecideAsync(Input(), default)).Status);
    }
    [Fact]
    public async Task TimeoutBoundsIgnoringTransportAndLateFaultCannotReopenSession()
    {
        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Read = _ => new(pending.Task) };
        var backend = Backend(transport, limits: new(10000, 100, TimeSpan.FromMilliseconds(20)));
        var reply = await backend.DecideAsync(Input(), default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status);
        pending.SetException(new IOException("PRIVATE_LATE_FAULT"));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
        Assert.Single(transport.Writes);
    }
    [Fact]
    public async Task InvalidUtf8AndMultipleFinalMessagesAreRejected()
    {
        var transport = Started(); transport.Frames.Enqueue([0xff]);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
        transport = Started(); var completed = JsonNode.Parse(Completed(Decision()))!;
        var items = completed["params"]!["turn"]!["items"]!.AsArray(); items.Add(items[0]!.DeepClone());
        transport.Add(completed.ToJsonString());
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport).DecideAsync(Input(), default)).Status);
    }
    [Fact]
    public async Task EscapedSecretsAreCheckedAfterJsonDecoding()
    {
        const string marker = "秘密<marker>";
        string Redact(string value) => value.Contains(marker) ? "" : value;
        var transport = Started(); transport.Add(Completed(Decision()));
        var input = Input() with { Objective = marker };
        Assert.Equal(CodexReplyStatus.Completed, (await Backend(transport, Redact).DecideAsync(input, default)).Status);
        var sent = JsonNode.Parse(transport.Writes.Last())!["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("", JsonNode.Parse(sent)!["objective"]!.GetValue<string>());
        transport = Started(); var decision = JsonNode.Parse(Decision())!; decision["secret"] = marker;
        transport.Add(Completed(decision.ToJsonString()));
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await Backend(transport, Redact).DecideAsync(Input(), default)).Status);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-thread")]
    public async Task CompletionAndUsageRequireThreadIdentity(string? thread)
    {
        foreach (var usage in new[] { false, true })
        {
            var transport = Started();
            var message = JsonNode.Parse(Completed(Decision()))!;
            if (usage) message["method"] = "thread/tokenUsage/updated";
            if (thread is null) message["params"]!.AsObject().Remove("threadId");
            else message["params"]!["threadId"] = thread;
            transport.Add(message.ToJsonString());
            var backend = Backend(transport); var reply = await backend.DecideAsync(Input(), default);
            Assert.Equal(CodexReplyStatus.OutputInvalid, reply.Status); Assert.Null(reply.CompletedJson);
            var writes = transport.Writes.Count;
            Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input("request-2"), default)).Status);
            Assert.Equal(writes, transport.Writes.Count);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidServiceErrorsWithoutOptionalInfoAreConnectionFailures(bool rpc)
    {
        var transport = rpc ? new Transport() : Started();
        transport.Add(rpc ? "{\"id\":1,\"error\":{\"code\":-32603,\"message\":\"PRIVATE_ERROR\"}}" :
            "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"failed\",\"error\":{\"message\":\"PRIVATE_ERROR\"}}}}");
        var reply = await Backend(transport).DecideAsync(Input(), default);
        Assert.Equal(CodexReplyStatus.ConnectionFailure, reply.Status); Assert.Null(reply.CompletedJson);
        Assert.DoesNotContain("PRIVATE_ERROR", reply.ToString());
    }
    [Fact]
    public async Task SecretInHostOutputSchemaIsRefusedBeforeSendingSchema()
    {
        var transport = Started();
        var backend = new CodexProtocolBackend("run-1", transport, new JsonObject { ["description"] = "PRIVATE_SECRET" },
            new(10000, 100, TimeSpan.FromSeconds(5)), x => x.Contains("PRIVATE_SECRET") ? "" : x);
        Assert.Equal(CodexReplyStatus.OutputInvalid, (await backend.DecideAsync(Input(), default)).Status);
        Assert.All(transport.Writes, x => Assert.DoesNotContain("PRIVATE_SECRET", x));
    }
    [Fact]
    public async Task ProductionPlannerStillFailsClosedWithoutVerifiedIsolation()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => new CodexPlanner<PlannerInputDocument, PlannerReply>().DecideAsync(Input(), default).AsTask());
    }
}
