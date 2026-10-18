namespace PuddingCode.Configuration;

/// <summary>
/// WP-L2d：SessionChunkVectors 存量回填 job 的控制开关与批参数。
/// 默认关闭（Enabled=false）；开启后宿主启动时扫描历史 ChatMessages（platform.db）
/// 并按批切块索引到 SessionChunkVectors（memory library 库）。
/// </summary>
public sealed class SessionChunkBackfillOptions
{
    public const string SectionName = "SessionChunkBackfill";

    /// <summary>
    /// 是否启用存量回填。默认 false（opt-in）——与 SubconsciousOptions.EnableWorker 同风格。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>每批扫描的行数（键集分页窗口，禁用 Skip/Take 偏移分页）。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>批间延迟毫秒数，用于限速 embedding 调用（lmstudio qwen3-0.6b 单次约 30ms）。</summary>
    public int DelayMs { get; set; } = 200;

    /// <summary>
    /// checkpoint 文件路径；为空时使用 &lt;DataRoot&gt;/runtime/backfill/session-chunk-checkpoint.json。
    /// </summary>
    public string? CheckpointFilePath { get; set; }

    /// <summary>
    /// 索引配置/embedding 版本（分块规则、provider/model、维度等）。版本变化时旧 checkpoint 失效，
    /// 从头重建，避免把旧向量当作新配置已覆盖。
    /// </summary>
    public string IndexConfigVersion { get; set; } = "1";

    /// <summary>
    /// Ready 后的启动保护窗口（毫秒）：先让首屏/交互稳定再扫描历史。默认 60s。
    /// </summary>
    public int StartupDelayMs { get; set; } = 60_000;
}
