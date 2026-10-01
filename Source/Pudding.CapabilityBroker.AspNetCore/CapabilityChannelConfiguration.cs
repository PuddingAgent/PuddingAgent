using Microsoft.Extensions.Configuration;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>
/// 能力通道的配置绑定（计划 §7、§9「启动就绪描述的演进」）。
///
/// 关键约束：
/// · <b>默认关闭</b>（<c>Enabled</c> 缺省 false）⇒ 不配置的产品行为与今天逐字一致；
/// · 端点形态与管道名由配置 + 派生规则决定，<b>不接受网页或调用方指定</b>；
/// · 凭据不在配置里（ControlToken 仍由既有 <c>system.json</c> 流程管理，本类不读取）；
/// · 配置非法时<b>整体失败</b>（不启动半配置的端点），由宿主记录并继续用旧传输。
/// </summary>
public sealed record CapabilityChannelConfiguration
{
    public const string SectionName = "Desktop:CapabilityChannel";

    /// <summary>缺省授予能力：当前已实现且有 payload 的全部能力。</summary>
    public const DesktopCapability DefaultGrantable =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification
        | DesktopCapability.ShellStatus
        | DesktopCapability.BrowserSnapshot
        | DesktopCapability.BrowserLocate
        | DesktopCapability.BrowserInteract
        | DesktopCapability.BrowserWaitFor
        | DesktopCapability.BrowserContexts;

    public const int DefaultMaxMessageBytes = 1024 * 1024;

    /// <summary>开关。<b>缺省 false</b>：不配置即保持既有（HTTP + WebSocket Bridge）行为。</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 传输形态：<c>named-pipe</c>（缺省，产品默认）/ <c>loopback-h2c</c>（调试备用）/ <c>both</c>（两者都监听）。
    /// 显式声明可避免「只配了 Loopback 却发布了未监听的管道端点」这类静默错配。
    /// </summary>
    public string Transport { get; init; } = CapabilityChannelTransport.NamedPipe;

    /// <summary>本机期望的 Desktop 身份（与 Desktop 侧 desktop.json 的同一值）。</summary>
    public string DesktopId { get; init; } = "default";

    /// <summary>显式管道名覆盖（运维/测试）；缺省按「用户 + 产品实例」派生。</summary>
    public string? NamedPipeName { get; init; }

    /// <summary>调试备用 Loopback h2c 端口；<c>null</c>/0 = 不监听。</summary>
    public int? LoopbackPort { get; init; }

    /// <summary>可授予能力集合（逗号分隔线名）；缺省 <see cref="DefaultGrantable"/>。</summary>
    public string? Grantable { get; init; }

    public int MaxInFlightPerConnection { get; init; } = 8;

    public int MaxQueuedFrames { get; init; } = 128;

    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    public int HandshakeTimeoutSeconds { get; init; } = 15;

    public static CapabilityChannelConfiguration Bind(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new CapabilityChannelConfiguration();
        }

        var section = configuration.GetSection(SectionName);
        if (!section.Exists())
        {
            return new CapabilityChannelConfiguration();
        }

        return new CapabilityChannelConfiguration
        {
            Enabled = section.GetValue("Enabled", false),
            Transport = section["Transport"] is { Length: > 0 } transport
                ? transport
                : CapabilityChannelTransport.NamedPipe,
            DesktopId = section["DesktopId"] is { Length: > 0 } desktopId ? desktopId : "default",
            NamedPipeName = section["NamedPipeName"] is { Length: > 0 } pipe ? pipe : null,
            LoopbackPort = section.GetValue<int?>("LoopbackPort") is > 0 ? section.GetValue<int?>("LoopbackPort") : null,
            Grantable = section["Grantable"] is { Length: > 0 } grantable ? grantable : null,
            MaxInFlightPerConnection = section.GetValue("MaxInFlightPerConnection", 8),
            MaxQueuedFrames = section.GetValue("MaxQueuedFrames", 128),
            MaxMessageBytes = section.GetValue("MaxMessageBytes", DefaultMaxMessageBytes),
            HandshakeTimeoutSeconds = section.GetValue("HandshakeTimeoutSeconds", 15),
        };
    }

    /// <summary>
    /// 构造通道选项。<paramref name="userScope"/> 与 <paramref name="productInstanceId"/> 只用于
    /// 派生隔离的管道名（用户 + 产品实例），不会出现在日志或描述里。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置不可用（开关关闭、缺少端点、能力名未知、参数越界）。</exception>
    public CapabilityChannelOptions CreateOptions(
        string userScope,
        string productInstanceId,
        string? coreInstanceId = null)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException($"{SectionName}:Enabled 未开启，不应构造能力通道选项。");
        }

        var grantable = ParseGrantable(Grantable);
        if (grantable == DesktopCapability.None)
        {
            throw new InvalidOperationException($"{SectionName}:Grantable 为空：没有任何能力会被授予。");
        }

        var transport = ParseTransport(Transport);
        var listensOnPipe = transport is CapabilityChannelTransport.NamedPipe or CapabilityChannelTransport.Both;
        var listensOnLoopback = transport is CapabilityChannelTransport.LoopbackHttp2 or CapabilityChannelTransport.Both;

        if (listensOnLoopback && LoopbackPort is null)
        {
            throw new InvalidOperationException($"{SectionName}:Transport={Transport} 需要 LoopbackPort。");
        }

        if (!listensOnLoopback && LoopbackPort is not null)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Transport={Transport} 不接受 LoopbackPort（避免配置与监听不一致）。");
        }

        if (!listensOnPipe && NamedPipeName is not null)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Transport={Transport} 不接受 NamedPipeName（避免配置与监听不一致）。");
        }

        string? pipeName = null;
        if (listensOnPipe)
        {
            pipeName = NamedPipeName ?? CapabilityEndpointNaming.NamedPipeName(userScope, productInstanceId);
            if (!DesktopCapabilityEndpoint.IsValidPipeName(pipeName))
            {
                throw new InvalidOperationException($"{SectionName}:NamedPipeName 不是合法的管道名。");
            }
        }

        var options = new CapabilityChannelOptions
        {
            ExpectedDesktopId = new DesktopInstanceId(DesktopId),
            Grantable = grantable,
            CoreInstanceId = coreInstanceId ?? $"core-{Environment.ProcessId}",
            MaxInFlightPerConnection = MaxInFlightPerConnection,
            MaxQueuedFrames = MaxQueuedFrames,
            MaxMessageBytes = MaxMessageBytes,
            HandshakeTimeout = TimeSpan.FromSeconds(HandshakeTimeoutSeconds),
            NamedPipeName = pipeName,
            LoopbackPort = listensOnLoopback ? LoopbackPort : null,
        };

        // 越界/不可用参数在这里就暴露，避免把非法配置带进运行期。
        options.Validate();
        return options;
    }

    /// <summary>
    /// 供「启动就绪」流程发布的端点描述（不含凭据）。
    /// <b>必须与宿主实际监听的端点一致</b>：NamedPipe/Both 发布管道，LoopbackHttp2 发布回环；
    /// 通道关闭时返回 <c>null</c>（就绪描述里不出现该字段）。
    /// </summary>
    public DesktopCapabilityEndpoint? Describe(
        string userScope,
        string productInstanceId,
        string? coreInstanceId = null,
        int protocolVersion = DesktopProtocolVersion.Current)
    {
        if (!Enabled)
        {
            return null;
        }

        var instanceId = coreInstanceId ?? $"core-{Environment.ProcessId}";
        var transport = ParseTransport(Transport);

        if (transport == CapabilityChannelTransport.LoopbackHttp2)
        {
            return LoopbackPort is { } port
                ? DesktopCapabilityEndpoint.LoopbackHttp2(new Uri($"http://127.0.0.1:{port}"), protocolVersion, instanceId)
                : throw new InvalidOperationException($"{SectionName}:Transport={Transport} 需要 LoopbackPort。");
        }

        var pipe = NamedPipeName ?? CapabilityEndpointNaming.NamedPipeName(userScope, productInstanceId);
        return DesktopCapabilityEndpoint.NamedPipe(pipe, protocolVersion, instanceId);
    }

    /// <summary>解析传输形态；未知值整体失败（不静默回退到默认传输）。</summary>
    public static string ParseTransport(string? transport) => transport switch
    {
        null or "" or CapabilityChannelTransport.NamedPipe => CapabilityChannelTransport.NamedPipe,
        CapabilityChannelTransport.LoopbackHttp2 => CapabilityChannelTransport.LoopbackHttp2,
        CapabilityChannelTransport.Both => CapabilityChannelTransport.Both,
        _ => throw new InvalidOperationException(
            $"{SectionName}:Transport 取值未知（允许：{CapabilityChannelTransport.NamedPipe} / "
            + $"{CapabilityChannelTransport.LoopbackHttp2} / {CapabilityChannelTransport.Both}）。"),
    };

    /// <summary>解析能力线名列表；未知线名<b>整体失败</b>（不静默忽略，避免配置漂移）。</summary>
    public static DesktopCapability ParseGrantable(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return DefaultGrantable;
        }

        var parsed = DesktopCapability.None;
        var unknown = new List<string>();

        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DesktopCapabilities.TryGetByName(name, out var descriptor))
            {
                parsed |= descriptor.Capability;
                continue;
            }

            unknown.Add(name);
        }

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Grantable 含未登记能力线名：{string.Join(", ", unknown)}。");
        }

        return parsed;
    }
}
