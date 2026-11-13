using System.Collections.ObjectModel;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// ADR-076 §4 语义数据类型目录。物理表、列、日志根与依赖索引只存在于该代码内置白名单，
/// 配置与 API 永远只引用稳定语义 ID。目录是 Preview/清理作业/快照估算唯一的物理映射来源。
/// </summary>
public static class StorageDataClassCatalog
{
    public const int Version = 1;

    /// <summary>物理表映射：时间列 + 可选字段清理列（ClearColumns 非空时为“清字段不删行”模式）。</summary>
    public sealed record StoragePhysicalTable
    {
        public required string Table { get; init; }
        /// <summary>
        /// 按时间清理用的列。**空字符串 = 该表不参与任何按时间清理**，
        /// 只在「界面归因」类数据类里出现（那些类 ManualCleanupAllowed=false，
        /// 永远进不了 Preview，所以该列不会被拼进 DELETE）。
        /// </summary>
        public required string TimestampColumn { get; init; }
        public string[]? ClearColumns { get; init; }
        /// <summary>DELETE 前必须先归档（Evidence 路径专用）。</summary>
        public bool ArchiveBeforeDelete { get; init; }
    }

    public sealed record StorageDataClassDefinition
    {
        public required string TargetId { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required StorageSafetyLevel SafetyLevel { get; init; }
        /// <summary>数据库文件（相对 DatabasesRoot；null = 文件类或派生处理器目标）。</summary>
        public string? DatabaseFile { get; init; }
        public IReadOnlyList<StoragePhysicalTable> Tables { get; init; } = [];
        /// <summary>日志根（相对 DataRoot，不跟随 reparse point）。</summary>
        public IReadOnlyList<string> LogRoots { get; init; } = [];
        /// <summary>该类型依赖的 retention 索引（正式声明在 PlatformDbContext；此处供保护校验）。</summary>
        public IReadOnlyList<string> RetentionIndexes { get; init; } = [];
        public int? DefaultRetentionDays { get; init; }
        public int? MinRetentionDays { get; init; }
        public int? MaxRetentionDays { get; init; }
        public bool ManualCleanupAllowed { get; init; }
        public bool AutomaticCleanupAllowed { get; init; }
        /// <summary>自动清理依赖长期聚合先落地（当前聚合未实现 → 默认自动关闭）。</summary>
        public bool RequiresRollupBeforeAutomatic { get; init; }
        /// <summary>UI 是否出现在人工清理选择器（Evidence 只展示策略状态）。</summary>
        public bool ShowInManualSelector { get; init; } = true;
        /// <summary>派生目标由 IStorageDerivedTargetHandler 执行（code-index / 索引）。</summary>
        public bool RequiresDerivedHandler { get; init; }
        public string? HandlerId { get; init; }
    }

    /// <summary>
    /// ADR-076 §3.2 受保护对象的**结构化**声明：把原来只有人读字符串的保护清单拆成
    /// 「标签 + 受保护的物理表 + 原因」。<see cref="ProtectedObjects"/> 保留原字符串形状
    /// （由本清单的 <see cref="Label"/> 派生，内容与顺序逐字不变），既有消费方零改动。
    /// </summary>
    public sealed record StorageProtectedObject
    {
        /// <summary>人读标签：与原 <see cref="ProtectedObjects"/> 条目逐字一致。</summary>
        public required string Label { get; init; }

        /// <summary>
        /// 该条目覆盖的物理表名（大小写不敏感）。为空表示该条目**没有单一物理表**：
        /// 要么按路径保护（源代码/附件），要么描述的是模块概念（turn/run/outbox）。
        /// 只登记能在 PlatformDbContext 里核到的表名 —— 不猜。
        /// </summary>
        public IReadOnlyList<string> Tables { get; init; } = [];

        /// <summary>保护原因（可判定，界面直接展示，不经任何解析）。</summary>
        public required string Reason { get; init; }
    }

    public const string PlatformDatabaseFile = "pudding_platform.db";
    // const 需要常量表达式（PathJoin/DirectorySeparatorChar 均非 const）；
    // Windows First：字面反斜杠与 PathJoin 输出一致。
    public const string CodeIndexDatabaseFile = @"code-index\code_index.db";

    private static string PathJoin(string a, string b) => string.Join(Path.DirectorySeparatorChar, a, b);

    public static readonly IReadOnlyList<StorageDataClassDefinition> Definitions =
    [
        new()
        {
            TargetId = StorageAdminTargetIds.DebugPayload,
            DisplayName = "Debug 详情",
            Description = "遥测 debug_json 与运行活动 metadata_json 大字段；超期置空，保留低基数字段。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "telemetry_metric_events",
                    TimestampColumn = "occurred_at_utc",
                    ClearColumns = ["debug_json"],
                },
                new StoragePhysicalTable
                {
                    Table = "runtime_activity",
                    TimestampColumn = "started_at_utc",
                    ClearColumns = ["metadata_json"],
                },
            ],
            RetentionIndexes = ["IX_telemetry_metric_events_occurred_at_utc", "IX_runtime_activity_started_at_utc"],
            DefaultRetentionDays = 7,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.TelemetryRaw,
            DisplayName = "原始性能遥测",
            Description = "telemetry_metric_events 原始行。自动清理需要小时聚合先落地，当前默认关闭。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "telemetry_metric_events",
                    TimestampColumn = "occurred_at_utc",
                },
            ],
            RetentionIndexes = ["IX_telemetry_metric_events_occurred_at_utc"],
            DefaultRetentionDays = 14,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
            RequiresRollupBeforeAutomatic = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.ContextLayerRaw,
            DisplayName = "上下文/缓存指标",
            Description = "context_layer_metric_events 原始行。自动清理需要日聚合先落地，当前默认关闭。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "context_layer_metric_events",
                    TimestampColumn = "occurred_at_utc",
                },
            ],
            RetentionIndexes = ["IX_context_layer_metric_events_occurred_at_utc"],
            DefaultRetentionDays = 14,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
            RequiresRollupBeforeAutomatic = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.RuntimeActivity,
            DisplayName = "运行活动明细",
            Description = "组件/操作级运行活动诊断流水，删除不影响会话消息与执行事实。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "runtime_activity",
                    TimestampColumn = "started_at_utc",
                },
            ],
            RetentionIndexes = ["IX_runtime_activity_started_at_utc"],
            DefaultRetentionDays = 14,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.LogsVerbose,
            DisplayName = "普通日志",
            Description = "DataRoot 下 system/diagnostics/sessions/components 日志文件，按最后写入时间清理。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            LogRoots = ["logs/system", "logs/diagnostics", "logs/sessions", "logs/components"],
            DefaultRetentionDays = 7,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.LogsError,
            DisplayName = "Error 日志",
            Description = "logs/error 下 Error 及以上结构化日志，按最后写入时间清理。",
            SafetyLevel = StorageSafetyLevel.Disposable,
            LogRoots = ["logs/error"],
            DefaultRetentionDays = 30,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.Rollups,
            DisplayName = "聚合缓存",
            Description = "已结束日的上下文日聚合缓存（可由原始指标重建）。",
            SafetyLevel = StorageSafetyLevel.Derived,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "context_layer_daily_rollups",
                    TimestampColumn = "day_utc",
                },
            ],
            RetentionIndexes = ["IX_context_layer_daily_rollups_day_utc"],
            DefaultRetentionDays = 365,
            MinRetentionDays = 30,
            MaxRetentionDays = 1825,
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = true,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.ObsoleteCodeIndexScopes,
            DisplayName = "冗余代码索引作用域",
            Description = "已覆盖/已移除或失效超过 24 小时的代码索引派生数据；源代码不受影响。",
            SafetyLevel = StorageSafetyLevel.Derived,
            DatabaseFile = CodeIndexDatabaseFile,
            RequiresDerivedHandler = true,
            HandlerId = "code-index-scopes",
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = false,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.RedundantIndexes,
            DisplayName = "重复或失效的数据库索引",
            Description = "与 EF Core 正式索引完全重复的旧运行时索引；重新校验定义后删除。",
            SafetyLevel = StorageSafetyLevel.Derived,
            DatabaseFile = PlatformDatabaseFile,
            RequiresDerivedHandler = true,
            HandlerId = "redundant-indexes",
            ManualCleanupAllowed = true,
            AutomaticCleanupAllowed = false,
        },
        new()
        {
            TargetId = StorageAdminTargetIds.ConversationEventsEvidence,
            DisplayName = "会话事件证据",
            Description = "conversation_events 权威事件流：按证据保留策略先归档再裁剪，不进入人工清理。",
            SafetyLevel = StorageSafetyLevel.Evidence,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable
                {
                    Table = "conversation_events",
                    TimestampColumn = "committed_at",
                    ArchiveBeforeDelete = true,
                },
            ],
            RetentionIndexes = ["IX_conversation_events_committed_at"],
            DefaultRetentionDays = 30,
            MinRetentionDays = 7,
            MaxRetentionDays = 3650,
            ManualCleanupAllowed = false,
            AutomaticCleanupAllowed = true,
            ShowInManualSelector = false,
        },
        // ── 以下四类：仅用于界面归因，**永不可清理** ──────────────────────────
        // 这 44 张表原本既不属于任何可清理数据类、也不在保护区，全部落进「未归类」，
        // 首屏「各库构成」因此有一大条无法解释的灰段。给它们名字而不是让它们消失：
        // 用户需要知道那几 GB 是「对话编排」还是「任务编排」，而不是「不知道是啥」。
        // 共同点：ManualCleanupAllowed=false、TimestampColumn 为空（不参与任何 DELETE）。
        new()
        {
            TargetId = StorageAdminTargetIds.ConversationOrchestration,
            DisplayName = "对话与执行编排",
            Description = "会话目录/头、turn、执行运行与控制消息、事件队列、投递与会话投影；" +
                "是执行事实，只能由各自的领域逻辑推进，不提供按时间删除。",
            ShowInManualSelector = false,
            SafetyLevel = StorageSafetyLevel.Evidence,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable { Table = "conversation_catalog", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "conversation_heads", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "conversation_projection_checkpoints", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "conversation_turns", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "event_queue", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "execution_control_messages", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "execution_runs", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "chat_execution_commands", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "message_deliveries", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "room_participants", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "session_steering_messages", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "session_sub_agents", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "sub_agent_runs", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "connector_stream_projections", TimestampColumn = "" },
            ],
        },
        new()
        {
            TargetId = StorageAdminTargetIds.TaskOrchestration,
            DisplayName = "任务与目标编排",
            Description = "任务节点/事件/依赖/派发、目标运行与验收、待办；属于编排事实，" +
                "清理必须走任务领域 API，不提供按时间删除。",
            ShowInManualSelector = false,
            SafetyLevel = StorageSafetyLevel.Evidence,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable { Table = "acceptance_batches", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "goal_iterations", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "goal_outbox", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "goal_runs", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "goal_verifications", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_assignment_attempts", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_comments", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_dependencies", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_dispatch_outbox", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_evaluations", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_events", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_execution_bindings", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_goal_bindings", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_nodes", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_plan_runs", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "task_scheduler_intents", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "todo_items", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "todo_lists", TimestampColumn = "" },
                // workspace_tasks 不在此登记：它是受保护表，由
                // Catalog_Never_Maps_Protected_Tables 守门。归因层一旦映射它，
                // 清理目录就指向了受保护数据，删除路径随之可达。
            ],
        },
        new()
        {
            TargetId = StorageAdminTargetIds.ConfigCatalog,
            DisplayName = "配置、凭据与外部接入",
            Description = "外部访问令牌与作用域/审计、幂等记录、Provider 文件引用；" +
                "属于安全态与用户权属数据，清理接口永不可达。",
            ShowInManualSelector = false,
            SafetyLevel = StorageSafetyLevel.UserData,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable { Table = "external_access_tokens", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "external_access_token_scopes", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "external_access_token_workspaces", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "external_access_token_audit_events", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "external_api_idempotency", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "llm_provider_file_refs", TimestampColumn = "" },
            ],
        },
        new()
        {
            TargetId = StorageAdminTargetIds.RuntimeMetadata,
            DisplayName = "运行时投影与缓存",
            Description = "可用性投影、执行预留、日聚合缓存与等待句柄；可从事实源重建，" +
                "但重建时机由运行时决定，不提供按时间删除。",
            ShowInManualSelector = false,
            SafetyLevel = StorageSafetyLevel.Derived,
            DatabaseFile = PlatformDatabaseFile,
            Tables =
            [
                new StoragePhysicalTable { Table = "agent_availability_projection", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "agent_execution_reservations", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "llm_usage_daily_aggregates", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "stats_daily_cache_days", TimestampColumn = "" },
                new StoragePhysicalTable { Table = "work_unit_await_handles", TimestampColumn = "" },
            ],
        },
    ];

    /// <summary>
    /// 强制保护对象（ADR-076 §3.2）的**结构化**声明。任何清理目标不得覆盖。
    /// <para>
    /// 为什么显式声明表名而不是解析下面的人读标签：既有标签有的含两个表名
    /// （"ChatMessages / room_messages"）、有的只写模块概念
    /// （"conversation turn / execution run / ..."）、有的根本不是表（"源代码 / 用户文件"）。
    /// 靠中文全角括号加斜杠切分只能得到"看起来像"的结果，而保护清单不允许"看起来像"。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<StorageProtectedObject> ProtectedObjectDefinitions =
    [
        new()
        {
            Label = "ChatMessages / room_messages（会话正文）",
            Tables = ["ChatMessages", "room_messages"],
            Reason = "会话正文：用户与 Agent 的原始对话记录，只能经会话/消息领域 API 读写。",
        },
        new()
        {
            Label = "session_event_log / conversation_events 未归档部分（执行事实源）",
            // session_event_log 在当前 PlatformDbContext 里没有对应物理表，故不登记表名。
            Tables = ["conversation_events"],
            Reason = "权威执行事实源：必须先归档再按证据策略裁剪，未归档行不可清理。",
        },
        new()
        {
            Label = "conversation turn / execution run / command / control message / delivery / outbox",
            Reason = "执行链与投递链路是审计与重放依据；按模块保护，没有单一物理表。",
        },
        new()
        {
            Label = "llm_gateway_usage_events（Provider 计费事实）",
            Tables = ["llm_gateway_usage_events"],
            Reason = "Provider 计费事实：账单核对依据，删除后无法重建。",
        },
        new()
        {
            Label = "TokenUsageEvents（会话/角色/上下文归因账本）",
            Tables = ["TokenUsageEvents"],
            Reason = "Token 归因账本：会话/角色/上下文的成本归因依据，删除后无法重建。",
        },
        new()
        {
            Label = "TokenUsageStats（长期聚合账本）",
            Tables = ["TokenUsageStats"],
            Reason = "长期聚合账本：TokenUsageEvents 的按月聚合，删除后无法重建。",
        },
        new()
        {
            Label = "Workspace Task / TaskEvent / Assignment / Orchestration Run+Event",
            // workspace_tasks 是唯一有实体表的任务事实：归因靠保护清单而不靠清理目录，
            // 否则清理目录就指向了受保护数据（Catalog_Never_Maps_Protected_Tables）。
            Tables = ["workspace_tasks"],
            Reason = "任务与编排事实是用户可见的执行历史；按模块保护，其中 workspace_tasks 是受保护物理表。",
        },
        new()
        {
            Label = "Agent / Skill / Memory / Knowledge / 配置 / 密钥 / 用户与权限",
            Reason = "定义态与安全态数据属于用户/系统权属；清理接口永不可达。",
        },
        new()
        {
            Label = "源代码 / 用户文件 / 附件 / 不可重建 Artifact",
            Reason = "非数据库对象：按路径保护，永不进入数据库清理。",
        },
    ];

    /// <summary>
    /// 人读保护清单。<b>内容与顺序逐字未变</b>（改为由 <see cref="ProtectedObjectDefinitions"/> 派生，
    /// 避免人读清单与结构化清单各说一套）；既有消费方（<c>StorageAdminController</c> 的
    /// <c>protected-objects</c> 端点）零改动。
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedObjects =
        ProtectedObjectDefinitions.Select(definition => definition.Label).ToArray();

    /// <summary>
    /// 表 → 保护原因。由 <see cref="ProtectedObjectDefinitions"/> 里声明了
    /// <see cref="StorageProtectedObject.Tables"/> 的条目展开；没有物理表的条目
    /// （路径保护、模块概念）只留在结构化清单与人读清单里。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ProtectedTableReasons =
        new ReadOnlyDictionary<string, string>(BuildProtectedTableReasons());

    /// <summary>
    /// 表 → 声明该表的清理数据类 targetId（<b>纯目录派生</b>：唯一来源是 <see cref="Definitions"/> 的
    /// <see cref="StorageDataClassDefinition.Tables"/>，不另建表名清单）。
    /// <para>
    /// 三个刻意选择：① 同一张表被多个数据类声明时（如 <c>telemetry_metric_events</c> 同时属于
    /// debug-payload 与 telemetry-raw）<b>列出全部</b>声明者 —— 这里表达"归属事实"而不是"占用分摊"，
    /// 因此不与 <see cref="StorageDataClassSpaceMapper"/> 的"先声明者为准"保持一致；
    /// ② 值为空数组是<b>可判定的答案</b>（"不属于任何可清理数据类"，典型是受保护表），不是缺失；
    /// ③ 键大小写不敏感（SQLite 表名大小写不敏感，dbstat 回的是建表时的写法）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TableDataClasses =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(BuildTableDataClasses());

    private static Dictionary<string, string> BuildProtectedTableReasons()
    {
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ProtectedObjectDefinitions)
        {
            foreach (var table in definition.Tables)
            {
                // 同一张表被两条保护条目覆盖时以先声明者为准：保护原因只需"能判定"，
                // 而抛异常会让整个存储管理页打不开（与 StorageDataClassSpaceMapper 同一取舍）。
                reasons.TryAdd(table, definition.Reason);
            }
        }

        return reasons;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildTableDataClasses()
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            foreach (var table in definition.Tables)
            {
                if (!owners.TryGetValue(table.Table, out var targetIds))
                {
                    targetIds = [];
                    owners[table.Table] = targetIds;
                }

                if (!targetIds.Contains(definition.TargetId, StringComparer.Ordinal))
                {
                    targetIds.Add(definition.TargetId);
                }
            }
        }

        // 受保护表也给出条目（空数组）：每张已知的表都有可判定的数据类答案，
        // 前端遇到空数组时再查 ProtectedTableReasons 就能区分"未归类"与"受保护"。
        foreach (var protectedTable in ProtectedTableReasons.Keys)
        {
            owners.TryAdd(protectedTable, []);
        }

        return owners.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static StorageDataClassDefinition? Find(string targetId) =>
        Definitions.FirstOrDefault(d => string.Equals(d.TargetId, targetId, StringComparison.Ordinal));

    public static StorageDataClassDefinition Require(string targetId) =>
        Find(targetId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(targetId), $"Unknown storage target: {targetId}");

    /// <summary>可人工清理（Preview 选择器）的目录项。</summary>
    public static IEnumerable<StorageDataClassDefinition> ManualSelectable() =>
        Definitions.Where(d => d.ManualCleanupAllowed && d.ShowInManualSelector);

    /// <summary>旧 /databases 端点目标 ID → 新语义 ID 翻译（过渡期，ADR-076 §4.1）。</summary>
    public static IReadOnlyList<string> TranslateLegacyTarget(string legacyTargetId) => legacyTargetId switch
    {
        StorageAdminTargetIds.LegacyTelemetry =>
        [
            StorageAdminTargetIds.TelemetryRaw,
            StorageAdminTargetIds.ContextLayerRaw,
        ],
        StorageAdminTargetIds.LegacyRuntimeActivity or StorageAdminTargetIds.RuntimeActivity =>
        [
            StorageAdminTargetIds.RuntimeActivity,
        ],
        StorageAdminTargetIds.LegacyDuplicateIndexes =>
        [
            StorageAdminTargetIds.RedundantIndexes,
        ],
        StorageAdminTargetIds.ObsoleteCodeIndexScopes =>
        [
            StorageAdminTargetIds.ObsoleteCodeIndexScopes,
        ],
        _ => [],
    };

    public static IEnumerable<StorageDataClassDto> ToDataClassDtos() => Definitions.Select(d => new StorageDataClassDto
    {
        TargetId = d.TargetId,
        DisplayName = d.DisplayName,
        Description = d.Description,
        SafetyLevel = d.SafetyLevel,
        SafetyLevelName = d.SafetyLevel.ToString(),
        CatalogVersion = Version,
        ManualCleanupAllowed = d.ManualCleanupAllowed,
        AutomaticCleanupAllowed = d.AutomaticCleanupAllowed,
        Protected = d.SafetyLevel is StorageSafetyLevel.Evidence or StorageSafetyLevel.UserData,
        ProtectionReason = d.SafetyLevel switch
        {
            StorageSafetyLevel.Evidence => "证据/审计事实，仅走独立证据保留策略。",
            StorageSafetyLevel.UserData => "用户创作数据，禁止清理。",
            _ => null,
        },
        DefaultRetentionDays = d.DefaultRetentionDays,
        MinRetentionDays = d.MinRetentionDays,
        MaxRetentionDays = d.MaxRetentionDays,
        RequiresRollupBeforeAutomatic = d.RequiresRollupBeforeAutomatic,
    });
}
