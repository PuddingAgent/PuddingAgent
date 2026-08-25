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
    ];

    /// <summary>
    /// 强制保护对象（ADR-076 §3.2）。任何清理目标不得覆盖；供目录保护测试与 UI 受保护区展示。
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedObjects =
    [
        "ChatMessages / room_messages（会话正文）",
        "session_event_log / conversation_events 未归档部分（执行事实源）",
        "conversation turn / execution run / command / control message / delivery / outbox",
        "llm_gateway_usage_events（Provider 计费事实）",
        "TokenUsageEvents（会话/角色/上下文归因账本）",
        "TokenUsageStats（长期聚合账本）",
        "Workspace Task / TaskEvent / Assignment / Orchestration Run+Event",
        "Agent / Skill / Memory / Knowledge / 配置 / 密钥 / 用户与权限",
        "源代码 / 用户文件 / 附件 / 不可重建 Artifact",
    ];

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
