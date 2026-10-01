using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 变更类能力的**结果不变量**：返回的页面版本必须严格推进。
///
/// 为什么必须强制：本系列的核心语义是「Ref 随 PageVersion 失效」——Core 侧据此拒绝陈旧引用。
/// 如果一次导航/交互/标签页操作返回的版本**没有推进**（或不带版本），
/// 那么交互前的旧引用在 Core 眼里仍然"有效"，整套保护就被静默破坏。
/// 因此这里 fail closed：不诚实的版本一律折叠为 <c>internal_error</c>，而不是把可疑版本放行。
/// </summary>
public static class DesktopMutationInvariants
{
    /// <summary>检查结果版本是否严格推进；返回 <c>null</c> 表示通过。</summary>
    public static DesktopCapabilityError? RequireVersionAdvanced(
        DesktopCapability capability,
        DesktopPageVersion requested,
        DesktopPageVersion returned)
    {
        if (!DesktopCapabilities.TryGet(capability, out var descriptor)
            || !descriptor.Traits.HasFlag(DesktopCapabilityTraits.Mutating))
        {
            // 只读能力不做此检查（它们本来就可能回带与请求相同的版本）。
            return null;
        }

        if (returned.Value > requested.Value)
        {
            return null;
        }

        return DesktopCapabilityError.Internal(
            $"mutating capability '{descriptor.Name}' reported page version {returned.Value}, "
            + $"which does not advance past the pinned version {requested.Value}");
    }
}
