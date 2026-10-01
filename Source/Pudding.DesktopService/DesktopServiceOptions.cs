using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>DesktopService 的准入与超时策略。</summary>
public sealed record DesktopServiceOptions
{
    /// <summary>本窗口实际启用的能力上限（≤ 与 Core 协商成功的集合）；未列出的能力一律拒绝。</summary>
    public required DesktopCapability AllowedCapabilities { get; init; }

    /// <summary>
    /// 无页面目标的 Shell 能力所采用的可信级别。
    /// 默认 <see cref="DesktopContextTrust.Untrusted"/>：在 Core 侧授权链路接线之前，
    /// 对话框/Picker/剪贴板一律不开放（计划 §8 切片 E：逐能力声明支持与授权）。
    /// </summary>
    public DesktopContextTrust ShellCallerTrust { get; init; } = DesktopContextTrust.Untrusted;

    internal void Validate()
    {
        if (AllowedCapabilities == DesktopCapability.None)
        {
            throw new ArgumentException("At least one capability must be enabled.", nameof(AllowedCapabilities));
        }
    }
}
