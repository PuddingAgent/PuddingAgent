namespace Pudding.CapabilityBroker;

/// <summary>
/// 迁移期的传输使用统计（切片 F 的决策依据）。
///
/// 目的：把「现在可以退役旧 Bridge 了吗」从**感觉**变成**可判定谓词**。
/// 判据很保守：只有在「能力通道确实在用」且「迁移期内一次都没有回退到旧 Bridge」时才允许退役；
/// 否则退役会静默把某些操作变成无路可走。
/// </summary>
public sealed record DesktopTransportUsage
{
    public DesktopTransportUsage(long capabilityChannelCalls, long legacyBridgeCalls, long noRouteCalls)
    {
        if (capabilityChannelCalls < 0 || legacyBridgeCalls < 0 || noRouteCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilityChannelCalls), "Counters must not be negative.");
        }

        CapabilityChannelCalls = capabilityChannelCalls;
        LegacyBridgeCalls = legacyBridgeCalls;
        NoRouteCalls = noRouteCalls;
    }

    public long CapabilityChannelCalls { get; }

    public long LegacyBridgeCalls { get; }

    public long NoRouteCalls { get; }

    public static DesktopTransportUsage Empty { get; } = new(0, 0, 0);

    /// <summary>是否已经观测到至少一次真实的能力通道使用（"它确实在工作"）。</summary>
    public bool ChannelProven => CapabilityChannelCalls > 0;

    /// <summary>
    /// 能否安全退役旧 Bridge：
    /// ①能力通道已被证明在用；②统计窗口内**零次**回退；③没有"无路可走"的记录
    /// （后者说明当时确实依赖过旧 Bridge）。
    /// </summary>
    public bool CanRetireLegacyBridge =>
        ChannelProven && LegacyBridgeCalls == 0 && NoRouteCalls == 0;

    /// <summary>记录一次实际使用的传输（<see cref="DesktopTransportRoute.None"/> 表示无路可走）。</summary>
    public DesktopTransportUsage Record(DesktopTransportRoute route) => route switch
    {
        DesktopTransportRoute.CapabilityChannel => new DesktopTransportUsage(CapabilityChannelCalls + 1, LegacyBridgeCalls, NoRouteCalls),
        DesktopTransportRoute.LegacyBridge => new DesktopTransportUsage(CapabilityChannelCalls, LegacyBridgeCalls + 1, NoRouteCalls),
        _ => new DesktopTransportUsage(CapabilityChannelCalls, LegacyBridgeCalls, NoRouteCalls + 1),
    };

    /// <summary>给运维看的一句结论（不含任何页面内容或凭据）。</summary>
    public string Explain() => CanRetireLegacyBridge
        ? $"可退役旧 Bridge：能力通道调用 {CapabilityChannelCalls} 次，窗口内零回退"
        : $"暂不可退役：{Reason()}";

    private string Reason()
    {
        if (!ChannelProven)
        {
            return "能力通道尚未观测到任何成功使用（先在迁移模式下运行一段时间）";
        }

        if (LegacyBridgeCalls > 0)
        {
            return $"窗口内仍有 {LegacyBridgeCalls} 次回退到旧 Bridge（说明能力通道尚未覆盖全部路径）";
        }

        return $"窗口内有 {NoRouteCalls} 次无路可走（说明旧 Bridge 当时仍在被依赖）";
    }

    public override string ToString() =>
        $"transport(channel={CapabilityChannelCalls}, legacy={LegacyBridgeCalls}, noRoute={NoRouteCalls}, retire={CanRetireLegacyBridge})";
}