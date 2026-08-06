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
}
