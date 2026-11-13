using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 存储「归因覆盖率」的回归：平台库里的每一张表都必须有**可判定的归属**。
///
/// <para>
/// 为什么需要这组用例：2026-11-13 实测平台库 12 GB 里约 6 GB 在首屏「各库构成」上是无名灰段。
/// 根因不是样式，而是 <c>StorageDataClassCatalog.Definitions</c> 只为 5 张表声明了可清理归属，
/// 另 4 张在保护区，剩下几十张编排/配置/投影表既不属于任何数据类、也不在保护清单 ——
/// <c>StorageDataClassSpaceMapper</c> 只能把它们全推进「未归类」桶。
/// </para>
/// <para>
/// 三条约束：①每张平台表都有归属（数据类非空，或受保护）；②「仅归因」的四类永远不可清理；
/// ③它们的表声明 <c>TimestampColumn</c> 为空 —— 空列是「不参与按时间清理」的唯一标记。
/// </para>
/// </summary>
[TestClass]
public sealed class StorageAttributionCoverageTests
{
    /// <summary>
    /// <c>PlatformDbContext</c> 里 <c>ToTable(..., "platform")</c> 声明的全部表（2026-11-13 快照，53 张）。
    /// 新增表时这张清单会过时 —— 过时本身不会让测试变红，但第 ① 条会覆盖清单内的每一张，
    /// 所以「加表却忘了归因」只要有人同步本清单就能立刻被抓到。
    /// </summary>
    private static readonly string[] PlatformTables =
    [
        // 已有可清理/证据归属的 5 张
        "context_layer_daily_rollups",
        "context_layer_metric_events",
        "conversation_events",
        "runtime_activity",
        "telemetry_metric_events",
        // 受保护（保护清单给出原因）
        "ChatMessages",
        "llm_gateway_usage_events",
        "room_messages",
        "TokenUsageEvents",
        "TokenUsageStats",
        // 仅归因：对话与执行编排
        "conversation_catalog",
        "conversation_heads",
        "conversation_projection_checkpoints",
        "conversation_turns",
        "event_queue",
        "execution_control_messages",
        "execution_runs",
        "chat_execution_commands",
        "message_deliveries",
        "room_participants",
        "session_steering_messages",
        "session_sub_agents",
        "sub_agent_runs",
        "connector_stream_projections",
        // 仅归因：任务与目标编排
        "acceptance_batches",
        "goal_iterations",
        "goal_outbox",
        "goal_runs",
        "goal_verifications",
        "task_assignment_attempts",
        "task_comments",
        "task_dependencies",
        "task_dispatch_outbox",
        "task_evaluations",
        "task_events",
        "task_execution_bindings",
        "task_goal_bindings",
        "task_nodes",
        "task_plan_runs",
        "task_scheduler_intents",
        "todo_items",
        "todo_lists",
        "workspace_tasks",
        // 仅归因：配置、凭据与外部接入
        "external_access_tokens",
        "external_access_token_scopes",
        "external_access_token_workspaces",
        "external_access_token_audit_events",
        "external_api_idempotency",
        "llm_provider_file_refs",
        // 仅归因：运行时投影与缓存
        "agent_availability_projection",
        "agent_execution_reservations",
        "llm_usage_daily_aggregates",
        "stats_daily_cache_days",
        "work_unit_await_handles",
    ];

    private static readonly string[] AttributionOnlyTargetIds =
    [
        StorageAdminTargetIds.ConversationOrchestration,
        StorageAdminTargetIds.TaskOrchestration,
        StorageAdminTargetIds.ConfigCatalog,
        StorageAdminTargetIds.RuntimeMetadata,
    ];

    [TestMethod]
    public void EveryPlatformTable_HasADecidableAttribution()
    {
        var map = StorageDataClassCatalog.TableDataClasses;
        var reasons = StorageDataClassCatalog.ProtectedTableReasons;

        foreach (var table in PlatformTables)
        {
            var owned = map.TryGetValue(table, out var classes) && classes.Count > 0;
            var protectedTable = reasons.ContainsKey(table);
            Assert.IsTrue(
                owned || protectedTable,
                $"{table} 既没有数据类归属、也不在保护清单 —— 首屏只能把它算进「未归类」，用户看不出那几 GB 是什么");
        }
    }

    [TestMethod]
    public void AttributionOnlyClasses_AreNeverCleanable()
    {
        var selectable = StorageDataClassCatalog.ManualSelectable()
            .Select(definition => definition.TargetId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var targetId in AttributionOnlyTargetIds)
        {
            var definition = StorageDataClassCatalog.Require(targetId);
            Assert.IsFalse(definition.ManualCleanupAllowed, $"{targetId} 不允许人工清理");
            Assert.IsFalse(definition.AutomaticCleanupAllowed, $"{targetId} 不允许自动清理");
            Assert.IsFalse(definition.ShowInManualSelector, $"{targetId} 不应出现在人工清理选择器");
            Assert.IsFalse(selectable.Contains(targetId), $"{targetId} 不得进入 ManualSelectable()");
            Assert.IsFalse(
                StorageDataClassCatalog.ProtectedTableReasons.ContainsKey(definition.Tables[0].Table),
                $"{targetId} 是「未归类」的归因，不是保护区，不应混进保护原因映射");
        }
    }

    [TestMethod]
    public void AttributionOnlyClasses_DeclareEmptyTimestampColumn()
    {
        // 空 TimestampColumn 是「不参与按时间清理」的唯一标记。若有人给它填了列名，
        // 说明这张表可能被当成可清理对象，必须重新评审 —— 这里显式拦住。
        foreach (var targetId in AttributionOnlyTargetIds)
        {
            var definition = StorageDataClassCatalog.Require(targetId);
            Assert.IsTrue(definition.Tables.Count > 0, $"{targetId} 必须声明它覆盖的表");
            foreach (var table in definition.Tables)
            {
                Assert.AreEqual(
                    string.Empty,
                    table.TimestampColumn,
                    $"{targetId}.{table.Table} 是仅归因的表，不应声明按时间清理列");
            }
        }
    }
}
