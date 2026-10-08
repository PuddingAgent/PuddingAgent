using Pudding.Contracts;
using Pudding.DesktopService;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// Shell 侧测试的共享夹具：**唯一**权威实例与生命周期端口。
///
/// 为什么测试也要统一来源：控制权状态在 Shell 侧曾有"每个闸门一个 bool"的历史
/// （审计 2026-10-08 结论 #1），测试若各自 new 一个调度器/状态，就再次允许那种结构存在。
/// </summary>
internal static class BrowserTestState
{
    internal static BrowserPageLifecycle CreateLifecycle() =>
        BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-test"),
            new DesktopProcessInstanceId("proc-test"));

    internal static BrowserBridgeCommandDispatcher CreateDispatcher() => new(CreateLifecycle());
}
