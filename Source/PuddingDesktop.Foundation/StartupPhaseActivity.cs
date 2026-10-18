namespace PuddingDesktop.Foundation;

/// <summary>
/// 从 Core 子进程日志尾部解析启动阶段，映射成启动面板上的一句人话与「最新输出」。
/// <para>
/// 输入是 <c>CoreLogBuffer.Snapshot()</c> 形态的行（每行可能带 <c>[stdout] </c> 前缀），
/// 阶段行由 <c>PuddingHost.Hosting.StartupPhaseTracker</c> 输出：
/// <c>[StartupPhase] &lt;phase&gt; total=...ms delta=...ms</c>。
/// 这里只做纯字符串判断、无 IO/无进程依赖，因此可在 Foundation 边界内确定性单测；
/// 组件不得反向引用 PuddingHost（阶段名按契约在这里独立识别）。
/// </para>
/// </summary>
public static class StartupPhaseActivity
{
    /// <summary>Core 启动阶段行标记（与 <c>StartupPhaseTracker.Format</c> 的输出一致）。</summary>
    public const string LogPrefix = "[StartupPhase] ";

    /// <summary>进程刚起来、Host 尚未构建完成时的阶段文案（启动面板据此判断第一步是否已完成）。</summary>
    public const string ProcessActivity = "正在启动 Core 进程…";

    private const string DataActivity = "正在初始化数据与索引…";
    private const string ServicesActivity = "正在初始化本地服务…";
    private const string ServerActivity = "正在启动本地接口…";
    private const string ReadyActivity = "Core 已就绪，正在等待桌面连接…";
    private const string DefaultActivity = "正在启动 Core…";

    /// <summary>
    /// 日志尾部里最后一条阶段行的 phase token（marker 可出现在行内任意位置）；
    /// 找不到阶段行时返回 <c>null</c>。
    /// </summary>
    public static string? FindLatestPhase(IReadOnlyList<string> logTail)
    {
        ArgumentNullException.ThrowIfNull(logTail);
        for (var index = logTail.Count - 1; index >= 0; index--)
        {
            if (TryReadPhaseLine(logTail[index], out var phase, out _)) return phase;
        }
        return null;
    }

    /// <summary>
    /// phase token → 启动面板的当前活动文案。大小写不敏感；未知/空值回落到
    /// 「正在启动 Core…」，保证面板永远有一句可显示的话。
    /// </summary>
    public static string? Describe(string? phase)
    {
        var token = phase?.Trim();
        if (string.IsNullOrEmpty(token)) return DefaultActivity;
        if (token.StartsWith("schema-", StringComparison.OrdinalIgnoreCase)) return DataActivity;

        return token.ToLowerInvariant() switch
        {
            "platform-db-ensure" or "event-store-ensure" or "memory-db-ensure"
                or "workspace-catalog" or "jieba-backfill" => DataActivity,
            "process-start" or "options-resolved" or "data-root-lease" or "data-root-bootstrapped"
                or "logging-ready" or "services-registered" or "host-built" or "middleware-mapped" => ProcessActivity,
            "initialized" => ServicesActivity,
            "server-started" => ServerActivity,
            "ready" => ReadyActivity,
            _ => DefaultActivity,
        };
    }

    /// <summary>
    /// 一次解析出「当前活动」与「最新输出」：Detail 是最后一条阶段行去掉 marker 后的整行，
    /// 例如 <c>platform-db-ensure total=1820ms delta=1820ms</c>；没有阶段行时返回 (null, null)。
    /// </summary>
    public static (string? Activity, string? Detail) Read(IReadOnlyList<string> logTail)
    {
        ArgumentNullException.ThrowIfNull(logTail);
        for (var index = logTail.Count - 1; index >= 0; index--)
        {
            if (!TryReadPhaseLine(logTail[index], out var phase, out var detail)) continue;
            return (Describe(phase), detail);
        }
        return (null, null);
    }

    /// <summary>解析一行日志里的阶段 marker 与 phase token；marker 允许出现在行内任意位置。</summary>
    private static bool TryReadPhaseLine(string? line, out string phase, out string detail)
    {
        phase = "";
        detail = "";
        if (line is null) return false;

        var marker = line.IndexOf(LogPrefix, StringComparison.Ordinal);
        if (marker < 0) return false;

        var rest = line[(marker + LogPrefix.Length)..].Trim();
        if (rest.Length == 0) return false;

        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        phase = rest[..end];
        detail = rest;
        return true;
    }
}
