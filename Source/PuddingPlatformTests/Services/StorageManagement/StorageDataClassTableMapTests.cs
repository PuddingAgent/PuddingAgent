using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 存储页一屏化 S1：「表 → 数据类」与「表 → 保护原因」两个**纯增量**结构化映射。
///
/// <para>钉住的语义：</para>
/// <list type="number">
/// <item>表归属**只能**来自 <see cref="StorageDataClassCatalog.Definitions"/> 的 Tables 声明（不另建清单）；</item>
/// <item>同一张表被多个数据类声明时**全部列出**（telemetry_metric_events 属于 debug-payload 与 telemetry-raw）；</item>
/// <item>受保护表也要有可判定的数据类答案（空数组 = 不属于任何可清理数据类），并在保护映射里带原因；</item>
/// <item>人读清单 <see cref="StorageDataClassCatalog.ProtectedObjects"/> 内容逐字未变（纯增量的守门断言）。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class StorageDataClassTableMapTests
{
    [TestMethod]
    public void TableDataClasses_CoversClassifiedAndProtectedTables()
    {
        var map = StorageDataClassCatalog.TableDataClasses;

        // ① 目录声明的表：归属必须与 Definitions 的声明一致（含"一张表多个声明者"的情形）。
        CollectionAssert.AreEqual(
            new[] { StorageAdminTargetIds.DebugPayload, StorageAdminTargetIds.TelemetryRaw },
            map["telemetry_metric_events"].ToArray(),
            "telemetry_metric_events 同时被 debug-payload 与 telemetry-raw 声明，必须两个都列出");

        CollectionAssert.AreEqual(
            new[] { StorageAdminTargetIds.ConversationEventsEvidence },
            map["conversation_events"].ToArray());

        // ② 受保护表：键必须存在，值是**空数组**（可判定的"不属于任何可清理数据类"）。
        Assert.IsTrue(map.ContainsKey("ChatMessages"), "受保护表也要有可判定的数据类答案（空数组），不能缺键");
        Assert.IsEmpty(map["ChatMessages"], "ChatMessages 不属任何可清理数据类");
        Assert.IsTrue(map.ContainsKey("TokenUsageEvents"), "TokenUsageEvents 必须有键");
        Assert.IsEmpty(map["TokenUsageEvents"], "TokenUsageEvents 不属任何可清理数据类");

        // ③ 键大小写不敏感（SQLite 表名大小写不敏感，dbstat 回的是建表时的写法）。
        Assert.IsEmpty(map["chatmessages"], "键必须大小写不敏感");
        Assert.HasCount(1, map["CONVERSATION_EVENTS"], "键必须大小写不敏感");
    }

    [TestMethod]
    public void TableDataClasses_EveryKeyIsDecidable()
    {
        var map = StorageDataClassCatalog.TableDataClasses;
        var knownTargets = StorageDataClassCatalog.Definitions
            .Select(definition => definition.TargetId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pair in map)
        {
            foreach (var targetId in pair.Value)
            {
                Assert.Contains(
                    targetId,
                    knownTargets,
                    $"{pair.Key} 声明了目录里不存在的 targetId：{targetId}");
            }

            Assert.IsTrue(
                pair.Value.Count > 0 || StorageDataClassCatalog.ProtectedTableReasons.ContainsKey(pair.Key),
                $"{pair.Key} 既不属于任何数据类、也不在保护清单里 —— 前端无法判定它该归到哪一桶");
        }

        // 反向一致性：目录声明过的每一张表都必须在映射里，且带着声明它的那个 targetId（防漏表）。
        foreach (var definition in StorageDataClassCatalog.Definitions)
        {
            foreach (var table in definition.Tables)
            {
                Assert.IsTrue(
                    map.ContainsKey(table.Table),
                    $"目录声明了 {table.Table}，映射里却没有");
                CollectionAssert.Contains(
                    map[table.Table].ToArray(),
                    definition.TargetId,
                    $"{table.Table} 的归属列表缺少 {definition.TargetId}");
            }
        }
    }

    [TestMethod]
    public void ProtectedTableReasons_JudgesRequiredTablesWithReason()
    {
        var reasons = StorageDataClassCatalog.ProtectedTableReasons;

        Assert.IsTrue(
            reasons.TryGetValue("TokenUsageEvents", out var tokenReason),
            "TokenUsageEvents 必须被判为受保护");
        StringAssert.Contains(tokenReason, "归因账本", "原因要能被人读懂，且指出它是什么账本");

        Assert.IsTrue(
            reasons.TryGetValue("llm_gateway_usage_events", out var billingReason),
            "llm_gateway_usage_events 必须被判为受保护");
        StringAssert.Contains(billingReason, "计费", "原因要指出它是计费事实");

        // 可清理的表**不得**出现在保护映射里，否则界面会把能删的东西标成不可删。
        foreach (var targetId in new[]
        {
            StorageAdminTargetIds.TelemetryRaw,
            StorageAdminTargetIds.ContextLayerRaw,
            StorageAdminTargetIds.RuntimeActivity,
            StorageAdminTargetIds.Rollups,
        })
        {
            foreach (var table in StorageDataClassCatalog.Require(targetId).Tables)
            {
                Assert.IsFalse(
                    reasons.ContainsKey(table.Table),
                    $"{table.Table} 属于可清理数据类 {targetId}，不该出现在保护映射里");
            }
        }
    }

    [TestMethod]
    public void ProtectedObjects_KeepsLegacyHumanReadableListVerbatim()
    {
        // 纯增量守门：既有消费方（StorageAdminController 的 protected-objects 端点）拿到的
        // 字符串数组一个字都不能变 —— 改成结构化声明后，人读清单必须由它逐字派生。
        CollectionAssert.AreEqual(
            new[]
            {
                "ChatMessages / room_messages（会话正文）",
                "session_event_log / conversation_events 未归档部分（执行事实源）",
                "conversation turn / execution run / command / control message / delivery / outbox",
                "llm_gateway_usage_events（Provider 计费事实）",
                "TokenUsageEvents（会话/角色/上下文归因账本）",
                "TokenUsageStats（长期聚合账本）",
                "Workspace Task / TaskEvent / Assignment / Orchestration Run+Event",
                "Agent / Skill / Memory / Knowledge / 配置 / 密钥 / 用户与权限",
                "源代码 / 用户文件 / 附件 / 不可重建 Artifact",
            },
            StorageDataClassCatalog.ProtectedObjects.ToArray());
    }

    [TestMethod]
    public void Maps_AreSharedReadOnlyInstances()
    {
        // 两个映射是跨请求共享的静态实例：调用方若能把它们当 Dictionary 改，会污染后续所有响应。
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, IReadOnlyList<string>>)StorageDataClassCatalog.TableDataClasses)
                .Add("evil_table", Array.Empty<string>()));

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, string>)StorageDataClassCatalog.ProtectedTableReasons)
                .Add("evil_table", "x"));
    }

    [TestMethod]
    public void Enrichment_AttachesBothMaps_AndLeavesExistingFieldsUntouched()
    {
        var analysis = new StorageDatabaseAnalysis
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            TotalBytes = 123,
            Databases = [],
            Items = [],
            Warnings = ["w"],
        };

        // 旧语义保留：不调用 enrichment 时两个新字段是空字典（"未附带映射"），而不是 null。
        Assert.IsNotNull(analysis.TableToDataClasses);
        Assert.IsEmpty(analysis.TableToDataClasses);
        Assert.IsNotNull(analysis.TableProtectionReasons);
        Assert.IsEmpty(analysis.TableProtectionReasons);

        var enriched = StorageAnalysisEnrichment.Apply(analysis);

        Assert.IsEmpty(
            enriched.TableToDataClasses["ChatMessages"],
            "受保护表 = 可判定的空归属");
        Assert.AreEqual(
            StorageAdminTargetIds.ConversationEventsEvidence,
            enriched.TableToDataClasses["conversation_events"].Single());
        Assert.IsTrue(
            enriched.TableProtectionReasons.ContainsKey("TokenUsageEvents"),
            "保护原因必须随分析响应一起下发");

        // 纯增量：既有字段逐项未变。
        Assert.AreEqual(analysis.CapturedAt, enriched.CapturedAt);
        Assert.AreEqual(analysis.TotalBytes, enriched.TotalBytes);
        Assert.HasCount(analysis.Databases.Count, enriched.Databases);
        Assert.HasCount(analysis.Items.Count, enriched.Items);
        CollectionAssert.AreEqual(analysis.Warnings.ToArray(), enriched.Warnings.ToArray());
    }

    [TestMethod]
    public void Json_ExposesNewFieldsAsCamelCase_AndKeepsExistingOnes()
    {
        // 前端看到的是 JSON，不是 C# 属性名：这里用 Web 默认序列化（camelCase）钉住实际线上形状。
        var analysis = StorageAnalysisEnrichment.Apply(new StorageDatabaseAnalysis
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            TotalBytes = 1,
            Databases = [],
            Items = [],
            Warnings = [],
        });

        var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"tableToDataClasses\"", "新字段名必须是前端可直接读的 camelCase");
        StringAssert.Contains(json, "\"tableProtectionReasons\"");
        StringAssert.Contains(json, "\"ChatMessages\":[]", "受保护表在 JSON 里是空数组（可判定）");
        StringAssert.Contains(json, "\"TokenUsageEvents\":", "受保护表在保护映射里要带原因");

        // 纯增量：既有字段一个不少（旧前端不会被破坏）。
        StringAssert.Contains(json, "\"totalBytes\"");
        StringAssert.Contains(json, "\"databases\"");
        StringAssert.Contains(json, "\"items\"");
    }
}
