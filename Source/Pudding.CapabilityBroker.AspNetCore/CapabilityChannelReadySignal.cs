using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>
/// 能力通道在「启动就绪」信号里的机器可读形态。
///
/// 为什么单独抽出来：Core 与 Desktop 之间只有<b>一条</b>靠字符串传递的契约——端点描述。
/// 它一旦漂移（例如版本写成 <c>v1</c> 而 Desktop 侧的严格解析器要求整数），后果不是编译错误，
/// 而是<b>只在开关打开时才暴露</b>的「通道永远不启动」：Desktop 侧只会记一条
/// 「描述不可用」并保持旧 Bridge，看起来像配置没生效。
///
/// 因此这里产出 <c>kind:address|protocolVersion|serverInstanceId</c>，并在产出时用<b>同一个</b>
/// 严格解析器自检：不合法就在启动期大声失败，而不是留给对端静默失败。
/// </summary>
public static class CapabilityChannelReadySignal
{
    /// <summary>
    /// <c>PUDDING_DESKTOP_READY</c> JSON 里的字段名。
    /// 对端为 <c>PuddingDesktop.Core.CoreReadyMessage.CapabilityEndpoint</c>（大小写不敏感匹配），
    /// 两处必须同名——由两侧测试各自钉住。
    /// </summary>
    public const string FieldName = "capabilityEndpoint";

    /// <summary>把端点描述折成 Desktop 侧可严格解析的文本；不含任何凭据。</summary>
    public static string Describe(DesktopCapabilityEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var text = endpoint.ToEndpointString();
        if (!DesktopCapabilityEndpoint.TryParse(text, out _))
        {
            // 不可自解析的描述绝不能上线：宁可启动失败，也不要 Desktop 静默永不连接。
            throw new InvalidOperationException(
                $"capability endpoint description is not self-parseable (kind={endpoint.Kind})");
        }

        return text;
    }

    /// <summary>启动日志里那一行（描述本身与就绪信号逐字一致，避免两套格式各自漂移）。</summary>
    public static string DescribeLogLine(DesktopCapabilityEndpoint endpoint) =>
        $"[CapabilityChannel] 就绪端点描述（不含凭据）：{Describe(endpoint)}";
}
