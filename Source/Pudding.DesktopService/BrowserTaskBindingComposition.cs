using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 控制面（方案 §4.1）的**装配工厂**：在 DesktopService 层构造签发器与受理/撤销服务。
///
/// 为什么需要它：Shell 组合根（<c>PuddingDesktop</c>）**不引用 Automation 程序集**，
/// 若在 Shell 里写 <c>lifecycle.Authority.ConnectionGeneration</c> 会直接编译失败（CS0012），
/// 而为了这一处给 Shell 加 Automation 引用是架构上的倒退。本层本来就引用 Automation，
/// 因此由本工厂产出两个实例，Shell 只负责"调用 + 传参"。
///
/// 世代取 authority 的**当前**连接世代：未握手时为 <c>None</c> ⇒ 签发被拒（fail-closed），
/// 不会出现"看起来能签但绑了个假世代"的情况。
/// </summary>
public static class BrowserTaskBindingComposition
{
    public static BrowserTaskBoundControlPlane Create(
        BrowserPageLifecycle lifecycle,
        DesktopInstanceId desktopId,
        DesktopProcessInstanceId processInstanceId,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(desktopId);
        ArgumentNullException.ThrowIfNull(processInstanceId);

        var issuer = new BrowserGrantIssuer(
            lifecycle,
            desktopId,
            processInstanceId,
            () => lifecycle.Authority.ConnectionGeneration,
            clock);

        return new BrowserTaskBoundControlPlane(issuer, new BrowserTaskBindingService(lifecycle, issuer));
    }
}

/// <summary>控制面装配产物：签发器 + 受理/撤销服务（**同一** lifecycle 与世代来源）。</summary>
public sealed record BrowserTaskBoundControlPlane(
    BrowserGrantIssuer Issuer,
    BrowserTaskBindingService Service);