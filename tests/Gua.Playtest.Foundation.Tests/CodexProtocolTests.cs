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
    private static Transport Started()
    {
        var transport = new Transport();
        transport.Add("{\"id\":1,\"result\":{\"userAgent\":\"codex/0.150.1\"}}");
        transport.Add("{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}");
        transport.Add("{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\"}}}");
        return transport;
    }
    private static CodexProtocolBackend Backend(Transport transport, Func<string, string>? redact = null,
        CodexProtocolLimits? limits = null) => new("run-1", transport, new JsonObject { ["type"] = "object" },
            limits ?? new(10000, 100, TimeSpan.FromSeconds(5)), redact ?? (x => x));

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
        transport.Add(new JsonObject { ["id"] = 1, ["result"] = new JsonObject { ["userAgent"] = agent } }.ToJsonString());
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
                { ["turn"] = new JsonObject { ["id"] = "turn-" + turn, ["status"] = "inProgress" } } }.ToJsonString());
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
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\"}}}");
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
                ["total"] = new JsonObject { ["inputTokens"] = input, ["outputTokens"] = output, ["totalTokens"] = total },
                ["last"] = new JsonObject { ["inputTokens"] = 1, ["outputTokens"] = 1, ["totalTokens"] = 2 }
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
        transport.Add("{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\",\"status\":\"inProgress\"}}}");
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
