namespace PuddingHost.Hosting;

/// <summary>
/// Options controlling PuddingHost startup and behavior.
/// Shared between Console, Desktop, and DesktopChild modes.
/// </summary>
public sealed record PuddingHostOptions
{
    /// <summary>Console, Desktop, or DesktopChild.</summary>
    public required PuddingHostMode Mode { get; init; }

    /// <summary>Root data directory (PUDDING_DATA_ROOT / --data-root).</summary>
    public required string DataRoot { get; init; }

    /// <summary>HTTP listen URLs.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>
    /// 是否服务 **Web 界面外壳**：Admin SPA 与 Chat SPA 的回退映射，以及 <c>/admin/reload</c> 便利端点。
    /// 静态文件（头像等非 SPA 资源）不受此开关影响；**API 端点一律不受影响**。
    /// Desktop 原生客户端为 <c>false</c>（管理界面已迁移到 WinUI 设置，入口已移除）。
    /// </summary>
    public bool ServeAdminSpa { get; init; } = true;

    /// <summary>Whether to open the admin in an external browser (Console only).</summary>
    public bool OpenExternalBrowser { get; init; }

    /// <summary>Whether WebView2 browser automation is available (Desktop only).</summary>
    public bool BrowserAutomationEnabled { get; init; }

    /// <summary>Parent process ID for DesktopChild mode (used for orphan detection).</summary>
    public int? DesktopParentPid { get; init; }

}
