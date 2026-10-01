namespace Pudding.Contracts;

/// <summary>
/// 能力通道协议版本。
///
/// 线上字段、编号与 service 定义的<b>真源是 proto</b>（<c>Pudding.Rpc.Protocol</c>）；
/// 这里只保存「双方协商用的版本区间」这一平台无关常量。
/// <c>Pudding.DesktopConnection</c> 的映射测试会断言 proto 的协商区间与这些常量一致，
/// 避免出现第二份真源。
/// </summary>
public static class DesktopProtocolVersion
{
    /// <summary>当前实现支持的版本。</summary>
    public const int Current = 1;

    /// <summary>仍可协商的最低版本。</summary>
    public const int Minimum = 1;

    public static bool IsSupported(int version) => version >= Minimum && version <= Current;

    /// <summary>协商结果：双方区间取交集，取较高者（当前实现只有 v1）。</summary>
    public static bool TryNegotiate(int peerMinimum, int peerMaximum, out int negotiated)
    {
        var lower = Math.Max(Minimum, peerMinimum);
        var upper = Math.Min(Current, peerMaximum);
        if (lower > upper)
        {
            negotiated = 0;
            return false;
        }

        negotiated = upper;
        return true;
    }
}
