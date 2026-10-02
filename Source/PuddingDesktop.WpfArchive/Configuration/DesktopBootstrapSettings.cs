namespace PuddingDesktop.Configuration;

/// <summary>
/// Minimal desktop launcher configuration stored in %LOCALAPPDATA%\Pudding\desktop.json.
/// Only stores DataRoot, optional Core path, window geometry and Shell layout preferences.
/// Must NOT contain Agent, model, port, token, or any Core business config.
/// </summary>
public sealed record DesktopBootstrapSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? DataRoot { get; init; }
    public string? CoreExecutablePath { get; init; }
    public Runtime.DesktopCloseBehavior CloseBehavior { get; init; } = Runtime.DesktopCloseBehavior.MinimizeToTray;
    public bool StartWithWindows { get; init; }
    public DesktopWindowSettings Window { get; init; } = new();
    /// <summary>
    /// Shell 工具区分栏偏好。**null = 用户从未配置过**（首次使用）——用它区分
    /// 「未配置」与「保存了默认值」：前者按设计规格 §13.3 用启动器比例 0.32 初始化，
    /// 后者（哪怕值恰好等于 0.45）必须原样恢复，不能被初始化默认覆盖。
    /// </summary>
    public DesktopToolWorkspaceSettings? ToolWorkspace { get; init; }
    public DesktopDebugSettings Debug { get; init; } = new();

    /// <summary>
    /// 镜像 Core 侧 <c>system.json</c> 的 <c>Desktop</c> 段（切片 C-3 能力通道，见
    /// <c>Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md</c> §8.3）。
    ///
    /// 段名与 Core 侧一致（<c>Desktop:CapabilityChannel</c>）⇒ 同一段配置可以在两个文件之间
    /// 原样复制，运维只需记一个段名。
    ///
    /// <b>null = 用户从未配置过</b>（缺省即关闭，继续走既有 WebSocket Bridge）。
    /// 之所以用可空而不是像 <c>Debug</c> 那样给默认值：<c>Enabled</c> 是**安全相关**开关，
    /// 用户没写过的字段不应该因为「保存了一次设置」就出现在文件里（<c>ToolWorkspace</c> 同此约定）。
    /// </summary>
    public DesktopSectionSettings? Desktop { get; init; }
}

/// <summary>
/// <c>desktop.json</c> 里的 <c>desktop</c> 段：目前只承载能力通道，形态刻意与
/// Core 的 <c>system.json</c> 同名段保持可复制。
/// </summary>
public sealed record DesktopSectionSettings
{
    public DesktopCapabilityChannelFileSettings? CapabilityChannel { get; init; }
}

/// <summary>
/// <c>Desktop:CapabilityChannel</c> 的**文件形态**（纯值对象，<b>不含任何凭据</b>）。
///
/// 缺省值必须与组件侧的 <c>Pudding.DesktopService.DesktopCapabilityChannelSettings</c> 一致，
/// 否则「用户没配」会被当成「配错了」（两侧一致性由测试钉住）。
/// 本工程不得引用 <c>Pudding.DesktopService</c>（它的边界目标禁止引用任何含 <c>PuddingDesktop</c> 的项目），
/// 因此这里是**独立的值对象**，由 Shell 组合根完成到组件设置类型的映射。
/// </summary>
public sealed record DesktopCapabilityChannelFileSettings
{
    /// <summary>唯一开关；缺省 false = 完全不启用（不注册、不连接、不改行为）。</summary>
    public bool Enabled { get; init; }

    /// <summary>必须与 Core 侧配置的同一值一致（握手时校验，不接受对端自称）。</summary>
    public string DesktopId { get; init; } = "default";

    /// <summary>
    /// 承载控制令牌的 Header 名。Core 同时接受既有产品 Header 与 Desktop 侧缺省值，
    /// 因此缺省即可用；显式配置时必须落在 Core 接受的两个名字之一。
    /// </summary>
    public string ControlTokenHeader { get; init; } = "x-pudding-control-token";

    /// <summary>握手超时（秒）：组件侧限定在 [1, 120]，越界会被组件判为无效配置（不是崩溃）。</summary>
    public int HandshakeTimeoutSeconds { get; init; } = 15;
}

/// <summary>
/// Layout preference for the right-hand multi-tab tool workspace.
/// Only layout survives a restart: which tabs were open, their Agent targets and any
/// running process state belong to the Core/Shell session, never to this file.
/// </summary>
public sealed record DesktopToolWorkspaceSettings
{
    /// <summary>Share of the combined chat + tool width; re-clamped against each window.</summary>
    public double WidthRatio { get; init; } = 0.45;

    /// <summary>Off by default: activity raises a marker instead of interrupting typing.</summary>
    public bool AutoExpandOnActivity { get; init; }

    /// <summary>
    /// The workspace starts collapsed on every launch, so no <c>IsExpanded</c> field is
    /// persisted; an invalid ratio falls back to the default instead of a broken split.
    /// The bounds match the Shell layout guard so a saved preference round-trips unchanged.
    /// </summary>
    public DesktopToolWorkspaceSettings Normalize() => this with
    {
        WidthRatio = double.IsFinite(WidthRatio) ? Math.Clamp(WidthRatio, 0.1, 0.9) : 0.45
    };
}

/// <summary>
/// Developer debug mode: Desktop builds Core from source, starts the Admin
/// frontend via `pnpm run start:dev`, and serves a unified loopback entry
/// through its own reverse proxy (ProxyPort). Backend/frontend ports stay
/// separate; only the proxy is the Workbench origin.
/// </summary>
public sealed record DesktopDebugSettings
{
    public bool Enabled { get; init; }
    public string? RepositoryRoot { get; init; }
    public string? FrontendWorkingDirectory { get; init; }
    public string? BackendProjectPath { get; init; }
    public int FrontendPort { get; init; } = 8000;
    public int ProxyPort { get; init; } = 80;
    public int FrontendStartupTimeoutSeconds { get; init; } = 180;
    public int BackendBuildTimeoutSeconds { get; init; } = 300;
}

public sealed record DesktopWindowSettings
{
    public int Width { get; init; } = 1440;
    public int Height { get; init; } = 900;
    public bool IsMaximized { get; init; }
}
