using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 子代理轮内软压缩回归测试（P1 能耗修复，2026-08-22）。
/// 背景：Buffered 子代理路径此前只有 LlmRequestBudgetGuard 硬悬崖，
/// 上下文被养到 ~61 万 tokens 才一次性裁剪；软压缩在 0.65×有效上限即驱逐最旧会话单元。
/// </summary>
[TestClass]
public sealed class LlmRequestBudgetGuardTests
{
    private static readonly string Big = new('a', 4_000);

    private static LlmConfig BuildConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 30_000,
        MaxOutputTokens = 1_000,
    };

    private static List<ChatMessage> BuildHistory(int pairs)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt") };
        for (var i = 0; i < pairs; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"{Big} user-{i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"{Big} assistant-{i}"));
        }
        return messages;
    }

    [TestMethod]
    public void PrepareSoftCompaction_NoOp_BelowTriggerRatio()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 2);

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", history, tools: null, BuildConfig());

        Assert.IsFalse(result.Compacted);
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(history.Count, result.Messages.Count);
        Assert.AreEqual(result.InitialUsedTokens, result.Snapshot.UsedTokens);
    }

    [TestMethod]
    public void PrepareSoftCompaction_EvictsUntilTarget_AndKeepsSystemAndTail()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var expectedTail = history.TakeLast(8).Select(static m => m.Content).ToList();

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", history, tools: null, BuildConfig());

        Assert.IsTrue(result.Compacted, $"initial={result.InitialUsedTokens} limit={result.EffectiveInputLimit}");
        Assert.IsTrue(result.RemovedMessageCount > 0);
        Assert.IsTrue(result.Snapshot.UsedTokens < result.InitialUsedTokens);
        Assert.AreEqual(history.Count, result.InitialMessageCount);
        Assert.IsTrue(result.Messages.Count < history.Count);

        // System 消息永远保留；保留部分足以覆盖受保护尾部 8 条。
        Assert.AreEqual(ChatRole.System, result.Messages[0].Role);
        Assert.IsTrue(result.Messages.Count >= 9, $"kept={result.Messages.Count}");
        var actualTail = result.Messages.TakeLast(8).Select(static m => m.Content).ToList();
        CollectionAssert.AreEqual(expectedTail, actualTail);

        // 压缩后处于目标水位之下，下一轮软压缩应为 no-op（锯齿下沿）。
        var second = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", result.Messages, tools: null, BuildConfig());
        Assert.IsFalse(second.Compacted);
    }

    [TestMethod]
    public void PrepareSoftCompaction_NeverThrows_WhenNothingRemovable()
    {
        var store = new ContextUsageSnapshotStore();
        var hugeSystem = new List<ChatMessage> { new(ChatRole.System, new string('s', 40_000)) };

        var result = LlmRequestBudgetGuard.PrepareSoftCompaction(
            store, "session-1", hugeSystem, tools: null, BuildConfig());

        Assert.IsFalse(result.Compacted);
        Assert.AreEqual(0, result.RemovedMessageCount);
        Assert.AreEqual(1, result.Messages.Count);
    }

    [TestMethod]
    public void RuntimeExecutionConfig_Seeds_And_Clamps_SoftCompactionRatios()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-exec-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            // 全新根：自动补齐默认值。
            var service = new RuntimeExecutionConfigService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<RuntimeExecutionConfigService>.Instance);
            var seeded = service.GetOptions().SubAgents;
            Assert.AreEqual(0.65, seeded.ContextSoftCompactionTriggerRatio, 0.0001);
            Assert.AreEqual(0.5, seeded.ContextSoftCompactionTargetRatio, 0.0001);

            // 非法配置：trigger > 1、target > trigger —— 必须被夹取回有效区间。
            var configPath = Path.Combine(root, "config", "runtime.execution.json");
            File.WriteAllText(configPath, """
            {
              "subAgents": {
                "contextSoftCompactionTriggerRatio": 5.0,
                "contextSoftCompactionTargetRatio": 1.5
              }
            }
            """);
            var repaired = new RuntimeExecutionConfigService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<RuntimeExecutionConfigService>.Instance).GetOptions().SubAgents;
            Assert.AreEqual(1.0, repaired.ContextSoftCompactionTriggerRatio, 0.0001);
            Assert.AreEqual(1.0, repaired.ContextSoftCompactionTargetRatio, 0.0001);
            Assert.IsTrue(repaired.ContextSoftCompactionTargetRatio <= repaired.ContextSoftCompactionTriggerRatio);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
