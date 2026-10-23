using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// S5 接入断言：<see cref="LlmRequestBudgetGuard.Prepare"/> 在发请求边界上必须先跑一次
/// 零 LLM 的确定性剪枝（<c>PuddingContextPruning.DeterministicPayloadCompaction</c>），
/// 且这次剪枝发生在 <c>CaptureLlmRequest</c> 之前——否则账户上冻结的测量与真正发出去的内容对不上。
/// <para>
/// 三个不变量：受保护尾部逐字不变、消息条数与配对不变、关掉开关后逐字回到接入前行为。
/// </para>
/// </summary>
[TestClass]
public sealed class LlmRequestBudgetGuardDeterministicPruneTests
{
    private const string SessionId = "deterministic-prune-session";

    /// <summary>约 1 万字符的工具结果：超过组件去重下限（512）与摘要阈值（8000）。</summary>
    private static string BigToolPayload() => string.Concat(Enumerable.Repeat("payload-token ", 1_000));

    /// <summary>
    /// 14 条历史，受保护尾部 = 最后 8 条（下标 6..13）。
    /// 下标 3 与 5 是两份逐字相同的工具结果 ⇒ 只有它们落在可剪枝区。
    /// 下标 12 的超大工具结果在尾部 ⇒ 必须逐字保留。
    /// </summary>
    private static List<ChatMessage> BuildHistory(string big, out string tailBig)
    {
        tailBig = string.Concat(Enumerable.Repeat("tail-payload ", 1_000));
        return
        [
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "turn 1 request"),
            new(ChatRole.Assistant, null, ToolCalls: [new ToolCall("call-1", "search_grep", "{\"q\":\"a\"}")]),
            new(ChatRole.Tool, big, ToolCallId: "call-1", ToolName: "search_grep"),
            new(ChatRole.Assistant, null, ToolCalls: [new ToolCall("call-2", "search_grep", "{\"q\":\"a\"}")]),
            new(ChatRole.Tool, big, ToolCallId: "call-2", ToolName: "search_grep"),
            new(ChatRole.User, "turn 2 request"),
            new(ChatRole.Assistant, "ack 2"),
            new(ChatRole.User, "turn 3 request"),
            new(ChatRole.Assistant, "ack 3"),
            new(ChatRole.User, "turn 4 request"),
            new(ChatRole.Assistant, null, ToolCalls: [new ToolCall("call-3", "file_read", "{\"path\":\"x\"}")]),
            new(ChatRole.Tool, tailBig, ToolCallId: "call-3", ToolName: "file_read"),
            new(ChatRole.User, "current turn user payload"),
        ];
    }

    private static LlmConfig BuildConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 200_000,
        MaxOutputTokens = 1_000,
    };

    [TestMethod]
    public void Prepare_PrunesDuplicatedToolPayloads_AndKeepsProtectedTailVerbatim()
    {
        var big = BigToolPayload();
        var messages = BuildHistory(big, out var tailBig);
        var logger = new CapturingLogger();

        var pruned = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            SessionId,
            messages,
            tools: null,
            BuildConfig(),
            logger: logger);

        // 剪枝不增删消息：条数与工具调用配对都保持原样。
        Assert.AreEqual(messages.Count, pruned.Messages.Count);
        Assert.AreEqual(0, pruned.RemovedMessageCount);
        Assert.AreEqual("call-1", pruned.Messages[3].ToolCallId);
        Assert.AreEqual("call-2", pruned.Messages[5].ToolCallId);

        // 可剪枝区内的重复载荷被改写，且带统一标记（便于在事件流里反查剪枝产物）。
        var olderDuplicate = pruned.Messages[3].Content;
        Assert.IsNotNull(olderDuplicate);
        Assert.IsTrue(olderDuplicate!.Length < big.Length, $"prunedLength={olderDuplicate.Length}");
        StringAssert.Contains(olderDuplicate, "[deterministic-prune]");

        // 受保护尾部逐字不变：System / 尾部 Tool / 当前轮 User 一个字节都不许动。
        Assert.AreEqual("system prompt", pruned.Messages[0].Content);
        Assert.AreEqual(tailBig, pruned.Messages[12].Content);
        Assert.AreEqual("current turn user payload", pruned.Messages[13].Content);

        // 剪枝必须发生在冻结测量之前 ⇒ 开启后的测量严格小于关闭时的测量。
        var unpruned = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            SessionId,
            messages,
            tools: null,
            BuildConfig(),
            deterministicPruneEnabled: false);
        Assert.IsLessThan(unpruned.Snapshot.UsedTokens, pruned.Snapshot.UsedTokens);
        Assert.AreEqual(big, unpruned.Messages[3].Content);

        // 真正发生剪枝时必须留下 Information 级证据（回收量与前后估算）。
        Assert.AreEqual(1, logger.Information.Count, string.Join(" | ", logger.Information));
        StringAssert.Contains(logger.Information[0], "[LlmRequestBudgetGuard:DeterministicPrune]");
        StringAssert.Contains(logger.Information[0], "reclaimedTokens=");
        StringAssert.Contains(logger.Information[0], "before=");
    }

    [TestMethod]
    public void Prepare_IsVerbatimBackwardCompatible_WhenDeterministicPruneDisabled()
    {
        var big = BigToolPayload();
        var messages = BuildHistory(big, out var tailBig);
        var logger = new CapturingLogger();

        var result = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            SessionId,
            messages,
            tools: null,
            BuildConfig(),
            logger: logger,
            deterministicPruneEnabled: false);

        // 逐字回到接入前的行为：所有内容原样，且没有任何剪枝证据。
        Assert.AreEqual(messages.Count, result.Messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            Assert.AreEqual(messages[index].Content, result.Messages[index].Content, $"index={index}");
            Assert.AreEqual(messages[index].ToolCallId, result.Messages[index].ToolCallId, $"index={index}");
        }

        Assert.AreEqual(big, result.Messages[3].Content);
        Assert.AreEqual(tailBig, result.Messages[12].Content);
        Assert.AreEqual(0, logger.Information.Count, string.Join(" | ", logger.Information));
    }

    [TestMethod]
    public void Prepare_LeavesSmallPayloadsUntouched_AndWritesNoPruneEvidence()
    {
        // 全部内容都远低于组件阈值 ⇒ 收益低于准入地板，必须整份回退（不为了几十 token 重写前缀）。
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "turn 1 request"),
            new(ChatRole.Tool, "small payload", ToolCallId: "call-1", ToolName: "search_grep"),
            new(ChatRole.User, "current turn user payload"),
        };
        var logger = new CapturingLogger();

        var result = LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            SessionId,
            messages,
            tools: null,
            BuildConfig(),
            logger: logger);

        Assert.AreEqual("small payload", result.Messages[2].Content);
        Assert.AreEqual(0, logger.Information.Count, string.Join(" | ", logger.Information));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public List<string> Information { get; } = new();

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
            else if (logLevel == LogLevel.Information)
                Information.Add(formatter(state, exception));
        }
    }
}
