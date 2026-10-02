using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// Desktop 侧能力通道设置（`desktop.json`）——与 Core 侧 <c>CapabilityChannelConfiguration</c> 对称。
///
/// 关键规则：
/// · <b>默认关闭</b>（<c>Enabled</c> 缺省 false）⇒ 继续用既有 WebSocket Bridge，产品行为不变；
/// · <b>传输二选一</b>：启用即走能力通道，不做跨传输回退（避免同一操作被执行两次）；
/// · 凭据不从配置里读，由主机侧注入（<see cref="DesktopConnectionOptions.Authentication"/>）；
/// · 声明集合来自<strong>代码事实</strong>（<see cref="DeclaredCapabilities"/>）而不是配置：
///   配置只会放宽上限，不能替 Desktop 宣告它没实现的能力。
/// </summary>
public sealed record DesktopCapabilityChannelSettings
{
    public const string SectionName = "Desktop:CapabilityChannel";

    public const string DefaultControlTokenHeader = "x-pudding-control-token";

    public bool Enabled { get; init; }

    public string DesktopId { get; init; } = "default";

    public string ControlTokenHeader { get; init; } = DefaultControlTokenHeader;

    public int HandshakeTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// 本记录是**纯值对象**：由宿主把 `desktop.json` 的对应字段映射进来。
    /// 组件不读配置文件——保持平台无关子库不引入配置/宿主依赖（依赖方向由编译期约束）。
    /// 缺省值即「关闭 + 沿用旧 Bridge」，因此宿主不配置时行为与今天一致。
    /// </summary>
    public static DesktopCapabilityChannelSettings Disabled { get; } = new();

    /// <summary>
    /// Desktop 实际实现（因而声明）的能力集合。**实现即声明**：写到配置里会让「没实现却宣告」成为可能。
    /// 与策略表的一致性由测试断言兜底（能准入的能力必须被声明）。
    /// </summary>
    public static DesktopCapability DeclaredCapabilities =>
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification
        | DesktopCapability.ShellStatus
        | DesktopCapability.BrowserSnapshot
        | DesktopCapability.BrowserLocate
        | DesktopCapability.BrowserInteract
        | DesktopCapability.BrowserWaitFor
        | DesktopCapability.BrowserContexts
        | DesktopCapability.BrowserTabs
        | DesktopCapability.BrowserContextCreate
        | DesktopCapability.BrowserContextClose
        | DesktopCapability.ShellDialog
        | DesktopCapability.ShellFilePicker
        | DesktopCapability.ShellClipboard;

    /// <summary>构造连接选项；未启用时明确失败（不静默降级到旧传输）。</summary>
    public CapabilityResult<DesktopConnectionOptions> CreateConnectionOptions(
        DesktopProcessInstanceId processInstanceId,
        DesktopChannelAuthentication? authentication = null)
    {
        if (!Enabled)
        {
            return CapabilityResult<DesktopConnectionOptions>.Failure(
                DesktopCapabilityError.InvalidRequest("capability channel is disabled; the legacy bridge stays selected"));
        }

        if (HandshakeTimeoutSeconds is < 1 or > 120)
        {
            return CapabilityResult<DesktopConnectionOptions>.Failure(
                DesktopCapabilityError.InvalidRequest("handshake timeout must be in [1, 120] seconds"));
        }

        try
        {
            return CapabilityResult<DesktopConnectionOptions>.Success(new DesktopConnectionOptions
            {
                DesktopId = new DesktopInstanceId(DesktopId),
                ProcessInstanceId = processInstanceId ?? throw new ArgumentNullException(nameof(processInstanceId)),
                SupportedCapabilities = DeclaredCapabilities,
                Authentication = authentication,
                HandshakeTimeout = TimeSpan.FromSeconds(HandshakeTimeoutSeconds),
            });
        }
        catch (ArgumentException ex)
        {
            return CapabilityResult<DesktopConnectionOptions>.Failure(
                DesktopCapabilityError.InvalidRequest($"desktop capability settings are not usable ({ex.ParamName})"));
        }
    }

    /// <summary>把 Core 发布的端点描述解析成传输；未启用时明确失败。</summary>
    public CapabilityResult<DesktopChannelTransport> ResolveTransportFromDescription(string? endpointText)
    {
        if (!Enabled)
        {
            return CapabilityResult<DesktopChannelTransport>.Failure(
                DesktopCapabilityError.InvalidRequest("capability channel is disabled; the legacy bridge stays selected"));
        }

        return DesktopChannelTransportResolver.ResolveFromText(endpointText);
    }
}
