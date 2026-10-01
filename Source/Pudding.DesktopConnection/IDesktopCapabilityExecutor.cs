using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopConnection;

/// <summary>
/// 桌面能力执行器：由消费方（WinUI 侧 DesktopService）实现，负责把动作调度到 UI 线程。
///
/// 契约要点：
/// · 只依赖 Contracts（不依赖 proto、不依赖 WinUI 类型）；
/// · 尊重 <paramref name="cancellationToken"/>（取消是尽力而为，不撤销已执行的脚本）；
/// · 用 <see cref="DesktopCapabilityResponse.Failure"/> 返回领域失败，不要用异常表达业务失败；
/// · 抛出的异常由连接层折叠为 <c>internal_error</c>（不泄漏异常消息与 payload）。
/// </summary>
public interface IDesktopCapabilityExecutor
{
    Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken);
}
