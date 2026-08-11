namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 诊断 append-only 表保留期裁剪服务（DiagnosticRetentionService）的配置。
/// 配置节：Diagnostics:Retention。
///
/// 目标表（platform.db 的 append-only 诊断表）：
///   telemetry_metric_events / context_layer_metric_events / runtime_activity
///   （按时间戳直接裁剪）
/// conversation_events 与 session_event_log 是权威执行事实源，不参与此后台裁剪。
/// ChatMessages 绝不参与裁剪。
/// </summary>
public sealed class DiagnosticRetentionOptions
{
    public const string SectionName = "Diagnostics:Retention";

    /// <summary>是否启用裁剪后台服务。默认 false（opt-in，与 SessionChunkBackfill 同风格）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>两次裁剪运行之间的间隔小时数。默认 24。</summary>
    public int RunIntervalHours { get; set; } = 24;

    /// <summary>宿主启动后的延迟秒数（避开启动峰值）。默认 60。</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>单批删除行数上限（SQLite 不支持 DELETE...LIMIT，用 rowid 子查询 LIMIT 分批）。默认 5000。</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>批间延迟毫秒数（限速，避免长时间写锁）。默认 100。</summary>
    public int BatchDelayMs { get; set; } = 100;

    /// <summary>每张表的保留天数。key 必须是受支持的表名（其余被忽略并告警）。</summary>
    public Dictionary<string, DiagnosticRetentionTableOptions> Tables { get; set; } = new(StringComparer.Ordinal);

    /// <summary>VACUUM 选项（默认关闭；SQLite 大表 VACUUM 会产生整库重写与长时间锁）。</summary>
    public DiagnosticRetentionVacuumOptions Vacuum { get; set; } = new();
}

/// <summary>单表保留策略。</summary>
public sealed class DiagnosticRetentionTableOptions
{
    /// <summary>是否裁剪该表。默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>保留天数（&lt;=0 视为不裁剪）。</summary>
    public int RetentionDays { get; set; } = 14;
}

/// <summary>VACUUM 策略。</summary>
public sealed class DiagnosticRetentionVacuumOptions
{
    /// <summary>裁剪结束后是否执行 VACUUM。默认 false。</summary>
    public bool Enabled { get; set; } = false;
}
