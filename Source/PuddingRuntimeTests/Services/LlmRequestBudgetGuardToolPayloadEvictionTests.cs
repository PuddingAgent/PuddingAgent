using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 2026-09-22 超限事故（<c>estimated=608167, limit=605760</c>）的回归：
/// <see cref="LlmRequestBudgetGuard.Prepare"/> 的硬悬崖在抛错前必须先做一次有界「载荷驱逐」——
/// 只截断 <see cref="ChatRole.Tool"/> 的超大载荷，绝不动 System / User 消息，也不减少消息条数；
/// 驱逐后仍超限才 fail-closed 抛错，且异常必须自带「不可裁剪地板」分解。
/// </summary>
[TestClass]
public sealed class LlmRequestBudgetGuardToolPayloadEvictionTests
{
    /// <summary>约 28 万字符的可读载荷：token 估算远高于本测试的有效输入上限（27_976）。</summary>
    private static string BigToolPayload() => string.Concat(Enumerable.Repeat("payload-token ", 20_000));

    private static LlmConfig BuildConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 30_000,
        MaxOutputTokens = 1_000,
    };

    [TestMethod]
    public void Prepare_EvictsOversizedToolPayload_AndKeepsSystemAndUserVerbatim()
    {
        var systemText = "system prompt";
        var userText = "current turn user payload";
        var assistantText = "assistant acknowledgement";
        var huge = BigToolPayload();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemText),
            new(ChatRole.User, userText),
            new(ChatRole.Assistant, assistantText),
            new(ChatRole.Tool, huge, ToolCallId: "call-1", ToolName: "search_grep"),
        };

        var logger = new CapturingLogger();
        var result = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "eviction-session",
            messages,
            tools: null,
            BuildConfig(),
            logger: logger);

        // 驱逐把请求拉回限内 ⇒ 不再抛错。
        Assert.IsLessThanOrEqualTo(result.EffectiveInputLimit, result.Snapshot.UsedTokens);

        // 驱逐载荷不是移除消息：RemovedMessageCount 语义保持纯净。
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(messages.Count, result.Messages.Count);

        // 硬约束：System / User 逐字未变（含当前轮 User）。
        Assert.AreEqual(systemText, result.Messages.Single(m => m.Role == ChatRole.System).Content);
        Assert.AreEqual(userText, result.Messages.Single(m => m.Role == ChatRole.User).Content);
        Assert.AreEqual(assistantText, result.Messages.Single(m => m.Role == ChatRole.Assistant).Content);

        // 只有 Tool 载荷被换成「头部摘要 + 截断标记」，且身份标识保持（不会断开工具调用配对）。
        var tool = result.Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.IsNotNull(tool.Content);
        Assert.IsTrue(tool.Content!.Length < huge.Length, $"evictedLength={tool.Content.Length}");
        StringAssert.Contains(tool.Content, "[ContextCompaction 截断标记]");
        StringAssert.Contains(tool.Content, "原始大小");
        StringAssert.Contains(tool.Content, "conversation_events");
        Assert.AreEqual("call-1", tool.ToolCallId);
        Assert.AreEqual("search_grep", tool.ToolName);

        // 真正执行驱逐时必须留下 Warning 级证据（条数 + 回收 token 估算）。
        Assert.AreEqual(1, logger.Warnings.Count, string.Join(" | ", logger.Warnings));
        StringAssert.Contains(logger.Warnings[0], "[LlmRequestBudgetGuard:PayloadEviction]");
        StringAssert.Contains(logger.Warnings[0], "evictedToolPayloads=1");
        StringAssert.Contains(logger.Warnings[0], "reclaimedTokens=");
    }

    [TestMethod]
    public void Prepare_StillThrowsFailClosed_WhenFloorExceedsLimitAfterEviction()
    {
        // System 消息不可裁剪，它自己就超过有效输入上限 ⇒ 驱逐 Tool 载荷也无济于事。
        var hugeSystem = BigToolPayload();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, hugeSystem),
            new(ChatRole.User, "current turn user payload"),
            new(ChatRole.Tool, BigToolPayload(), ToolCallId: "call-2", ToolName: "file_read"),
        };

        var ex = Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "floor-session",
            messages,
            tools: null,
            BuildConfig()));

        Assert.IsGreaterThan(0, ex.EstimatedTokens);
        Assert.IsGreaterThan(0, ex.EffectiveInputLimit);

        // 既有前缀逐字保留，分解信息只追加在其后。
        StringAssert.StartsWith(
            ex.Message,
            $"LLM request input is too large after history trimming: estimated={ex.EstimatedTokens}, limit={ex.EffectiveInputLimit}.");
        StringAssert.Contains(ex.Message, "breakdown(rawEstimate):");
        StringAssert.Contains(ex.Message, "systemMessages=");
        StringAssert.Contains(ex.Message, "toolDefinitions=");
        StringAssert.Contains(ex.Message, "protectedTail=");
        StringAssert.Contains(ex.Message, "removableCandidates=");

        // 分解数据可编程访问：撑爆请求的是不可裁剪的 System 地板。
        Assert.IsNotNull(ex.Breakdown);
        Assert.IsGreaterThan(0, ex.Breakdown!.SystemMessageTokens);
        Assert.IsGreaterThan(0, ex.Breakdown.ProtectedTailTokens);
        Assert.IsGreaterThanOrEqualTo(0, ex.Breakdown.RemovableCandidateTokens);
    }

    [TestMethod]
    public void Prepare_WithEvictionDisabled_KeepsLegacyHardCliff()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "current turn user payload"),
            new(ChatRole.Assistant, "assistant acknowledgement"),
            new(ChatRole.Tool, BigToolPayload(), ToolCallId: "call-3", ToolName: "search_grep"),
        };

        // 开关关闭 ⇒ 回到旧路径：直接抛错，且不做任何载荷驱逐。
        var ex = Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "disabled-session",
            messages,
            tools: null,
            BuildConfig(),
            evictOversizedToolPayloads: false));
        Assert.IsNotNull(ex.Breakdown);

        // 同一输入在默认（开启）参数下不再抛错：证明开关是行为分界，而不是文案差异。
        var result = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "disabled-session",
            messages,
            tools: null,
            BuildConfig());
        Assert.IsLessThanOrEqualTo(result.EffectiveInputLimit, result.Snapshot.UsedTokens);
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(messages.Count, result.Messages.Count);
    }

    [TestMethod]
    public void Prepare_DoesNotEvictToolPayloadsWithinThreshold()
    {
        // 载荷在阈值（16KB）之内 ⇒ 一个字节都不许动，也不许写 Warning。
        var smallPayload = new string('x', 1_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "current turn user payload"),
            new(ChatRole.Tool, smallPayload, ToolCallId: "call-4", ToolName: "file_read"),
        };
        var logger = new CapturingLogger();

        var result = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "below-threshold-session",
            messages,
            tools: null,
            BuildConfig(),
            logger: logger);

        Assert.AreEqual(smallPayload, result.Messages.Single(m => m.Role == ChatRole.Tool).Content);
        Assert.AreEqual(0, logger.Warnings.Count);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
