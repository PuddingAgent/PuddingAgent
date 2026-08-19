using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;
using ThinkingChunk = PuddingCode.Services.ReasoningCompactCodec.ThinkingChunk;
using DecodedThinking = PuddingCode.Services.ReasoningCompactCodec.DecodedThinking;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P1-3 T6「端到端回归 + zipRatio 验收」。
/// 覆盖「写侧 v2 落库 → 读侧 codec 解码 → UI DTO 还原」全链路，
/// 含中文多字节偏移与旧格式兼容；并报告 v2 表示开销（合成样本）。
/// 只读验证：不修改任何现有文件。
/// </summary>
[TestClass]
public sealed class MessageDeliveryDispatcherReasoningE2ETests
{
    // ── T6-1 写侧 → 读侧闭环 ─────────────────────────────────────

    [TestMethod]
    public async Task E2E_WriteSideV2_ReadSideDecode_RoundTripsByteIdentical()
    {
        // 经完整 dispatcher 调度路径落库（写侧 v2）。
        var transcript = await PersistSubAgentTranscriptAsync(
        [
            ServerSentEventFrame.Json("thinking", new { delta = "first chunk " }),
            ServerSentEventFrame.Json("thinking", new { delta = "second chunk" }),
            ServerSentEventFrame.Json("delta", new { delta = "reply " }),
            ServerSentEventFrame.Json("done", new { reply = "reply ok" }),
        ]);

        // 写侧必须产出 v2 紧凑格式（含 v/chunks/hash 键）。
        Assert.IsNotNull(transcript.ThinkingJson, "带 thinking 的消息落库后 ThinkingJson 不应为 null");
        StringAssert.Contains(transcript.ThinkingJson!, "\"v\":2");
        StringAssert.Contains(transcript.ThinkingJson!, "\"chunks\"");
        StringAssert.Contains(transcript.ThinkingJson!, "\"hash\"");

        // 读侧 codec 解码：text 逐字节一致、chunks 与累积帧一致、hash 有效。
        var decoded = ReasoningCompactCodec.Decode(transcript.ThinkingJson);
        Assert.IsNotNull(decoded, "v2 ThinkingJson 必须可被 codec 解码");
        Assert.IsTrue(decoded!.IsCompactFormat, "写侧产出必须是紧凑格式");
        Assert.IsTrue(decoded.HashValid, "hash 应与 text 匹配");
        Assert.AreEqual("first chunk second chunk", decoded.Text);
        Assert.HasCount(2, decoded.Chunks);
        Assert.AreEqual("first chunk ", decoded.Chunks[0].Text);
        Assert.AreEqual("second chunk", decoded.Chunks[1].Text);
        Assert.IsTrue(decoded.Chunks[0].Timestamp > 0, "timestamp 应为毫秒级正数");
        Assert.IsTrue(
            decoded.Chunks[1].Timestamp >= decoded.Chunks[0].Timestamp,
            "累积帧 timestamp 应单调不减");
    }

    [TestMethod]
    public async Task E2E_WriteSide_NoThinking_ThinkingJsonStaysNull()
    {
        var transcript = await PersistSubAgentTranscriptAsync(
        [
            ServerSentEventFrame.Json("delta", new { delta = "plain reply" }),
            ServerSentEventFrame.Json("done", new { reply = "plain reply" }),
        ]);

        Assert.IsNull(transcript.ThinkingJson, "无 thinking 帧时 ThinkingJson 必须保持 null");
        Assert.AreEqual("plain reply", transcript.Content);
    }

    // ── T6-2 中文多字节 ─────────────────────────────────────────

    [TestMethod]
    public async Task E2E_ChineseMultiByte_Utf8Offsets_RoundTrip()
    {
        // 中文 3 字节/字：chunk 边界切在汉字之间，是 UTF-8 字节偏移正确性的关键坑。
        var transcript = await PersistSubAgentTranscriptAsync(
        [
            ServerSentEventFrame.Json("thinking", new { delta = "思考：模型需要优化，" }),
            ServerSentEventFrame.Json("thinking", new { delta = "继续分析用户意图，" }),
            ServerSentEventFrame.Json("thinking", new { delta = "最终给出建议。" }),
            ServerSentEventFrame.Json("delta", new { delta = "好的" }),
            ServerSentEventFrame.Json("done", new { reply = "好的" }),
        ]);

        Assert.IsNotNull(transcript.ThinkingJson);
        var decoded = ReasoningCompactCodec.Decode(transcript.ThinkingJson);
        Assert.IsNotNull(decoded, "中文多字节偏移解码不得失败（返回 null 表示偏移切错）");
        Assert.IsTrue(decoded!.IsCompactFormat);
        Assert.IsTrue(decoded.HashValid);

        var expectedText = "思考：模型需要优化，继续分析用户意图，最终给出建议。";
        Assert.AreEqual(expectedText, decoded.Text);
        Assert.HasCount(3, decoded.Chunks);
        Assert.AreEqual("思考：模型需要优化，", decoded.Chunks[0].Text);
        Assert.AreEqual("继续分析用户意图，", decoded.Chunks[1].Text);
        Assert.AreEqual("最终给出建议。", decoded.Chunks[2].Text);
        Assert.IsTrue(
            decoded.Chunks[2].Timestamp >= decoded.Chunks[1].Timestamp,
            "累积帧 timestamp 应单调不减");
    }

    // ── T6-3 读侧 R1：MessageApiController.MapToDto 还原 ────────

    [TestMethod]
    public void E2E_ReadSide_R1MessageApi_MapToDto_RestoresThinkingChunks()
    {
        var (text, chunks) = BuildChunks(includeChinese: true);
        var v2Json = ReasoningCompactCodec.Encode(text, chunks);
        var entity = NewAgentEntity("m-r1", v2Json);

        var dto = InvokeMapToDto(entity);

        Assert.IsNotNull(dto.Thinking, "R1 映射后 Thinking 不应为 null");
        Assert.HasCount(chunks.Count, dto.Thinking!);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.AreEqual(chunks[i].Text, dto.Thinking![i].Text, $"chunk[{i}] text 逐字节还原");
            Assert.AreEqual(chunks[i].Timestamp, dto.Thinking![i].Timestamp, $"chunk[{i}] timestamp 还原");
        }
    }

    // ── T6-4 读侧 R2：AgentConversationProjectionService 还原 ───

    [TestMethod]
    public void E2E_ReadSide_R2Projection_BuildTranscriptProcessItems_RestoresThinking()
    {
        var (text, chunks) = BuildChunks(includeChinese: true);
        var v2Json = ReasoningCompactCodec.Encode(text, chunks);
        var entity = NewAgentEntity("m-r2", v2Json);

        var items = InvokeBuildTranscriptProcessItems(entity);

        Assert.HasCount(chunks.Count, items);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.AreEqual("thinking", items[i].Kind);
            Assert.AreEqual(chunks[i].Text, items[i].Text, $"item[{i}] text 逐字节还原");
            Assert.AreEqual(
                DateTimeOffset.FromUnixTimeMilliseconds(chunks[i].Timestamp),
                items[i].Timestamp,
                $"item[{i}] timestamp 还原");
        }
    }

    // ── T6-5 旧格式兼容（读侧路径仍工作） ───────────────────────

    [TestMethod]
    public void E2E_LegacyArrayFormat_ReadSide_StillRestoresText()
    {
        // 历史存量数据：旧 [{text,timestamp}] 数组（中文 + 英文混合）。
        const string legacyJson =
            """[{"text":"先分析需求","timestamp":1750000000000},{"text":", then plan","timestamp":1750000000150}]""";

        var decoded = ReasoningCompactCodec.Decode(legacyJson);
        Assert.IsNotNull(decoded);
        Assert.IsFalse(decoded!.IsCompactFormat, "旧格式必须被识别为非紧凑格式");
        Assert.IsTrue(decoded.HashValid, "旧格式无 hash，恒视为有效");
        Assert.AreEqual("先分析需求, then plan", decoded.Text);
        Assert.HasCount(2, decoded.Chunks);
        Assert.AreEqual("先分析需求", decoded.Chunks[0].Text);
        Assert.AreEqual(1750000000000, decoded.Chunks[0].Timestamp);
        Assert.AreEqual(", then plan", decoded.Chunks[1].Text);
        Assert.AreEqual(1750000000150, decoded.Chunks[1].Timestamp);

        // 经 R1/R2 读侧路径同样可还原。
        var entity = NewAgentEntity("m-legacy", legacyJson);
        var dto = InvokeMapToDto(entity);
        Assert.IsNotNull(dto.Thinking);
        Assert.HasCount(2, dto.Thinking!);
        Assert.AreEqual("先分析需求", dto.Thinking![0].Text);
        Assert.AreEqual(1750000000000, dto.Thinking![0].Timestamp);
        Assert.AreEqual(", then plan", dto.Thinking![1].Text);

        var items = InvokeBuildTranscriptProcessItems(entity);
        Assert.HasCount(2, items);
        Assert.AreEqual("先分析需求", items[0].Text);
        Assert.AreEqual("thinking", items[0].Kind);
    }

    // ── T6-6 zipRatio 验收（合成样本） ──────────────────────────

    [TestMethod]
    public void ZipRatio_SyntheticThinkingSamples_V2OverheadReported()
    {
        // 假设说明（写入交付报告）：
        // 1. 旧格式按历史写侧序列化语义构造（默认 JsonSerializer，转义非 ASCII）；
        //    v2 由 codec.Encode 产出（UnsafeRelaxedJsonEscaping，不转义非 ASCII）。
        //    对比同时给出「旧格式不转义」参考值，避免序列化选项差异误导结论。
        // 2. 样本 A：短增量帧（每帧 1~3 字符），贴近真实 thinking 增量流；
        //    样本 B：任务书参数（每帧 20~50 字符），贴近分块较大的 delta 流。
        //    均含中文（UTF-8 3 字节/字）与毫秒时间戳，delta 递增。

        var sampleA = BuildSyntheticFrames(500, minCharsPerFrame: 1, maxCharsPerFrame: 3, seed: 42, chineseRatio: 0.85);
        var sampleB = BuildSyntheticFrames(500, minCharsPerFrame: 20, maxCharsPerFrame: 50, seed: 7, chineseRatio: 0.7);

        var report = new StringBuilder();
        report.AppendLine("P1-3 T6 zipRatio 验收（合成样本，详见 temp/p1-3-t6-delivery.md 假设说明）");
        report.AppendLine();
        report.AppendLine("| 样本 | 帧数 | 文本UTF-8字节 | 旧格式字节(转义) | 旧格式字节(不转义) | v2字节 | ratio(旧转义/v2) | v2开销(v2/文本) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        var rows = new[] { ("A-短帧(1-3字符)", sampleA), ("B-中帧(20-50字符)", sampleB) };
        var allOverheadOk = true;
        foreach (var (name, sample) in rows)
        {
            var metrics = MeasureZipRatio(sample);
            allOverheadOk &= metrics.V2Overhead <= 5.0;
            report.AppendLine(
                $"| {name} | {metrics.FrameCount} | {metrics.TextBytes} | {metrics.LegacyEscapedBytes} | " +
                $"{metrics.LegacyRawBytes} | {metrics.V2Bytes} | {metrics.RatioEscaped:F2} | {metrics.V2Overhead:F2} |");
        }

        // 主断言：v2 表示开销 ≤ 5x（任务书目标数量级）。
        Assert.IsTrue(allOverheadOk, $"v2 表示开销必须 ≤5x：{report}");

        // 样本 A（贴近真实增量流）下 v2 必须显著小于旧格式（ratio > 2）。
        var metricsA = MeasureZipRatio(sampleA);
        Assert.IsTrue(
            metricsA.RatioEscaped > 2.0,
            $"短帧样本下 ratio(旧/v2) 应 > 2，实际 {metricsA.RatioEscaped:F2}");

        // 输出完整报告到 temp/（临时产物，不提交）。
        var tempDir = Path.Combine(FindRepoRoot(), "temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "p1-3-t6-zipratio.txt"), report.ToString(), Encoding.UTF8);
    }

    // ── 反射调用读侧 private static 投影方法 ────────────────────

    private static MessageApiController.ChatMessageDto InvokeMapToDto(ChatMessageEntity entity)
    {
        var method = typeof(MessageApiController).GetMethod(
            "MapToDto",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MapToDto not found");
        var result = method.Invoke(null, [entity, NullLogger.Instance]);
        return (MessageApiController.ChatMessageDto)result!;
    }

    private static IReadOnlyList<ProcessSummaryItem> InvokeBuildTranscriptProcessItems(ChatMessageEntity entity)
    {
        var method = typeof(AgentConversationProjectionService).GetMethod(
            "BuildTranscriptProcessItems",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildTranscriptProcessItems not found");
        var result = method.Invoke(null, [entity, NullLogger.Instance]);
        return (IReadOnlyList<ProcessSummaryItem>)result!;
    }

    // ── 样本构造与度量 ──────────────────────────────────────────

    private static (string Text, List<ThinkingChunk> Chunks) BuildChunks(bool includeChinese)
    {
        var chunks = new List<ThinkingChunk>
        {
            new(includeChinese ? "分析" : "analyze ", 1_000),
            new(includeChinese ? "用户意图" : "intent", 1_120),
            new(includeChinese ? "并给出方案" : " and propose", 1_250),
        };
        return (string.Concat(chunks.Select(c => c.Text)), chunks);
    }

    private static List<(string Text, long Timestamp)> BuildSyntheticFrames(
        int count,
        int minCharsPerFrame,
        int maxCharsPerFrame,
        int seed,
        double chineseRatio)
    {
        var random = new Random(seed);
        var frames = new List<(string, long)>(count);
        var ts = 1_750_000_000_000L;
        for (var i = 0; i < count; i++)
        {
            var len = random.Next(minCharsPerFrame, maxCharsPerFrame + 1);
            var sb = new StringBuilder(len);
            for (var j = 0; j < len; j++)
            {
                // 中文为主，英文为辅，模拟真实 thinking 文本分布。
                sb.Append(random.NextDouble() < chineseRatio
                    ? (char)('\u4e00' + random.Next(200))
                    : (char)('a' + random.Next(26)));
            }

            ts += random.Next(5, 200);
            frames.Add((sb.ToString(), ts));
        }

        return frames;
    }

    private sealed record ZipRatioMetrics(
        int FrameCount,
        long TextBytes,
        long LegacyEscapedBytes,
        long LegacyRawBytes,
        long V2Bytes,
        double RatioEscaped,
        double V2Overhead);

    private static ZipRatioMetrics MeasureZipRatio(List<(string Text, long Timestamp)> frames)
    {
        var text = string.Concat(frames.Select(f => f.Text));
        var textBytes = Encoding.UTF8.GetByteCount(text);

        // 旧格式：默认序列化（转义非 ASCII，历史语义）；另算不转义参考值。
        var legacyEscapedJson = JsonSerializer.Serialize(
            frames.Select(f => new { text = f.Text, timestamp = f.Timestamp }));
        var legacyEscapedBytes = Encoding.UTF8.GetByteCount(legacyEscapedJson);

        var legacyRawJson = JsonSerializer.Serialize(
            frames.Select(f => new { text = f.Text, timestamp = f.Timestamp }),
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var legacyRawBytes = Encoding.UTF8.GetByteCount(legacyRawJson);

        // v2：codec 紧凑格式。
        var v2Json = ReasoningCompactCodec.Encode(
            text,
            frames.Select(f => new ThinkingChunk(f.Text, f.Timestamp)).ToList());
        var v2Bytes = Encoding.UTF8.GetByteCount(v2Json);

        return new ZipRatioMetrics(
            frames.Count,
            textBytes,
            legacyEscapedBytes,
            legacyRawBytes,
            v2Bytes,
            (double)legacyEscapedBytes / v2Bytes,
            (double)v2Bytes / textBytes);
    }

    // ── 写侧闭环基础设施（与 MessageDeliveryDispatcherTests 同构） ──

    private static async Task<ChatMessageEntity> PersistSubAgentTranscriptAsync(
        IReadOnlyList<ServerSentEventFrame> frames)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new RecordingMessageInbox
        {
            ClaimMetadata = new Dictionary<string, string>
            {
                ["source"] = "subagent",
                ["intent"] = "subagent_result",
                ["sub_agent_id"] = "sub-1",
            },
            ClaimContent = """
            {
              "schema": "pudding-message",
              "version": 1,
              "message_id": "msg-sub-result",
              "message_type": "subagent_result",
              "from": { "kind": "agent", "id": "sub-1", "display_name": "Sub Agent" },
              "to": [{ "kind": "agent", "id": "parent-agent" }],
              "constraints": ["This message was delivered by Pudding Message Fabric."],
              "context": { "format": "text/markdown", "text": "child completed" }
            }
            """,
        });
        services.AddSingleton(new RecordingRuntimeAgentDispatcher { StreamFrames = frames });
        services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<RecordingMessageInbox>());
        services.AddScoped<IRuntimeAgentDispatcher>(sp => sp.GetRequiredService<RecordingRuntimeAgentDispatcher>());
        services.AddScoped<IWorkspaceAgentCatalog>(_ => new RecordingWorkspaceAgentCatalog(
            Agent("agent-b", mainSessionId: "agent-b-main-session")));
        services.AddScoped<IAgentRuntimeProfileResolver>(_ => new RecordingAgentRuntimeProfileResolver(
            [Agent("agent-b", mainSessionId: "agent-b-main-session")]));
        services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();
        services.AddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
        }

        var dispatcher = new MessageDeliveryDispatcher(
            new RecordingInternalEventBus(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
            new AgentExecutionAdmissionCoordinator(),
            NullLogger<MessageDeliveryDispatcher>.Instance);

        await dispatcher.HandleAsync(CreateSubAgentResultEvent(), CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.ChatMessages.SingleAsync(m => m.SessionId == "session-1" && m.Role == "agent");
    }

    private static ChatMessageEntity NewAgentEntity(string messageId, string? thinkingJson) =>
        new()
        {
            Id = 1,
            MessageId = messageId,
            SessionId = "session-1",
            WorkspaceId = "default",
            AgentInstanceId = "agent-b",
            Role = "agent",
            Content = "reply text",
            ThinkingJson = thinkingJson,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private static WorkspaceAgentDto Agent(string agentId, string? mainSessionId) =>
        new(
            agentId,
            agentId,
            Description: null,
            DisplayName: agentId,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: "general-assistant",
            MainSessionId: mainSessionId,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: true,
            IsFrozen: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static InternalEvent CreateSubAgentResultEvent() =>
        new()
        {
            Type = "message.deliver",
            SessionId = "session-1",
            WorkspaceId = "default",
            Source = new EventSource { SourceType = "message", SourceId = "m-sub" },
            Payload = new MessageDeliverEventPayload
            {
                MessageId = "m-sub",
                DeliveryId = "d-sub",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "parent-sub-child" },
                Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "agent-b" },
                Content = "subagent result",
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "subagent",
                    ["intent"] = "subagent_result",
                },
            },
        };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return @"E:\github\AgentNetworkPlan\PuddingAgent";
    }

    // ── 测试替身（与 MessageDeliveryDispatcherTests 同构的私有辅助） ──

    private sealed class RecordingMessageInbox : IMessageInbox
    {
        private int _claimCount;

        public int ClaimAttemptCount { get; init; } = 1;
        public int? MaxClaimCount { get; init; }
        public IReadOnlyDictionary<string, string>? ClaimMetadata { get; init; }
        public string? ClaimContent { get; init; }
        public IReadOnlyList<MessageInboxItem> BatchClaims { get; init; } = [];

        public Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MessageInboxItem>>([]);

        public Task<IReadOnlyList<MessageDeliveryTarget>> ListPendingTargetsAsync(
            string targetKind,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MessageDeliveryTarget>>([]);

        public Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default)
        {
            if (MaxClaimCount is int maxClaimCount
                && Interlocked.Increment(ref _claimCount) > maxClaimCount)
                return Task.FromResult<MessageInboxItem?>(null);

            return Task.FromResult<MessageInboxItem?>(new MessageInboxItem
            {
                DeliveryId = "d1",
                MessageId = "m1",
                WorkspaceId = "default",
                RoomId = "room-default",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
                Target = request.Endpoint,
                Content = ClaimContent ?? (ClaimMetadata is null ? "hello" : "subagent result"),
                Status = MessageDeliveryStatuses.Delivering,
                Priority = 0,
                AttemptCount = ClaimAttemptCount,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClaimedByExecutionId = request.ExecutionId,
                Metadata = ClaimMetadata ?? new Dictionary<string, string>(),
            });
        }

        public Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
            MessageClaimRequest request,
            int maxBatch,
            CancellationToken ct = default) =>
            Task.FromResult(BatchClaims);

        public Task<bool> RenewLeaseAsync(
            string deliveryId,
            string executionId,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task AckAsync(string deliveryId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RetryAsync(
            string deliveryId,
            string executionId,
            string error,
            DateTimeOffset availableAt,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeferAsync(
            string deliveryId,
            string executionId,
            string error,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeadLetterAsync(
            string deliveryId,
            string executionId,
            string error,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        public IReadOnlyList<ServerSentEventFrame>? StreamFrames { get; set; }
        public List<RuntimeDispatchRequest> StreamRequests { get; } = [];

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamRequests.Add(request);
            if (StreamFrames is not null)
            {
                foreach (var frame in StreamFrames)
                {
                    yield return frame;
                    await Task.Yield();
                }

                yield break;
            }

            yield return ServerSentEventFrame.Json("delta", new { text = "ok" });
            await Task.Yield();
            yield return ServerSentEventFrame.Json("done", new { ok = true });
        }
    }

    private sealed class RecordingWorkspaceAgentCatalog(params WorkspaceAgentDto[] agents) : IWorkspaceAgentCatalog
    {
        public IReadOnlyList<WorkspaceAgentDto> Agents { get; } = agents;

        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) =>
            Task.FromResult(Agents);
    }

    private sealed class RecordingAgentRuntimeProfileResolver(IReadOnlyList<WorkspaceAgentDto> agents)
        : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
        {
            var agent = agents.FirstOrDefault(item =>
                string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
                throw new InvalidOperationException($"Agent '{agentId}' was not found in workspace '{workspaceId}'.");

            return Task.FromResult(new AgentRuntimeProfile
            {
                WorkspaceId = workspaceId,
                AgentId = agent.AgentId,
                DisplayName = agent.DisplayName ?? agent.Name,
                MainSessionId = agent.MainSessionId,
                SourceTemplateId = agent.SourceTemplateId,
                PreferredProviderId = "test",
                PreferredModelId = "test-model",
                LlmConfig = new LlmConfig
                {
                    Endpoint = "https://llm.test/v1",
#pragma warning disable CS0618
                    ApiKey = "test-key",
#pragma warning restore CS0618
                    ModelId = "test-model",
                },
            });
        }
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IEventSubscriptionHandle>(new RecordingEventSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingEventSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = "sub-1";
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
    }
}
