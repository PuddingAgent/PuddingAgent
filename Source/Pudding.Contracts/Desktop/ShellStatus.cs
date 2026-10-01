namespace Pudding.Contracts.Desktop;

/// <summary>窗口形态（只读 Shell 状态的一部分）。</summary>
public enum DesktopWindowState
{
    Unknown,

    /// <summary>窗口可见（前台或后台）。</summary>
    Visible,

    /// <summary>已隐藏到系统托盘（Desktop 仍在运行，Core 未被停止）。</summary>
    HiddenToTray,

    /// <summary>正在退出（托盘/窗口已进入关闭流程）。</summary>
    Closing,
}

/// <summary>自动化状态：与 Desktop 侧的暂停/用户接管轴对应，供 Core 在派发变更类能力前判断。</summary>
public enum DesktopAutomationState
{
    /// <summary>可自动化。</summary>
    Free,

    /// <summary>工具运行时被暂停：变更类能力会被拒绝。</summary>
    Paused,

    /// <summary>用户接管了浏览器/窗口：自动化已停止。</summary>
    UserTakeover,
}

/// <summary>
/// 只读 Shell 状态：窗口形态、托盘可见性、自动化状态与打开的页面数。
/// 不含任何页面内容、URL、剪贴板或凭据。
/// </summary>
public sealed record DesktopShellStatus
{
    public DesktopShellStatus(
        DesktopWindowState windowState,
        bool trayVisible,
        DesktopAutomationState automation,
        int openPageCount)
    {
        if (!Enum.IsDefined(windowState))
        {
            throw new ArgumentOutOfRangeException(nameof(windowState), windowState, "Window state is not registered.");
        }

        if (!Enum.IsDefined(automation))
        {
            throw new ArgumentOutOfRangeException(nameof(automation), automation, "Automation state is not registered.");
        }

        if (openPageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openPageCount), openPageCount, "Page count cannot be negative.");
        }

        WindowState = windowState;
        TrayVisible = trayVisible;
        Automation = automation;
        OpenPageCount = openPageCount;
    }

    public DesktopWindowState WindowState { get; }

    public bool TrayVisible { get; }

    public DesktopAutomationState Automation { get; }

    public int OpenPageCount { get; }

    public override string ToString() =>
        $"{WindowState}/tray={TrayVisible}/{Automation}/pages={OpenPageCount}";
}

/// <summary>
/// 只读状态线名真源（窗口形态 / 自动化状态）。未知线名折叠为安全的保守值，
/// 便于 Core 在不重新发版的情况下识别 Desktop 新增状态。
/// </summary>
public static class DesktopShellStatusWire
{
    public static string NameOf(DesktopWindowState state) => state switch
    {
        DesktopWindowState.Visible => "visible",
        DesktopWindowState.HiddenToTray => "hidden_to_tray",
        DesktopWindowState.Closing => "closing",
        _ => "unknown",
    };

    public static DesktopWindowState ParseWindowState(string? name) => name switch
    {
        "visible" => DesktopWindowState.Visible,
        "hidden_to_tray" => DesktopWindowState.HiddenToTray,
        "closing" => DesktopWindowState.Closing,
        _ => DesktopWindowState.Unknown,
    };

    public static string NameOf(DesktopAutomationState state) => state switch
    {
        DesktopAutomationState.Paused => "paused",
        DesktopAutomationState.UserTakeover => "user_takeover",
        _ => "free",
    };

    /// <summary>未知线名按「不可自动化」保守处理（fail safe）：宁可少做，不可误自动。</summary>
    public static DesktopAutomationState ParseAutomationState(string? name) => name switch
    {
        "free" => DesktopAutomationState.Free,
        "paused" => DesktopAutomationState.Paused,
        "user_takeover" => DesktopAutomationState.UserTakeover,
        _ => DesktopAutomationState.UserTakeover,
    };
}
