using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Automation;

namespace DesktopServiceTests;

/// <summary>
/// 测试用的**唯一权威实例**与生命周期端口。
///
/// 这些类型有一个共同的前置条件：接管/暂停/授权/租约都只能有一个事实源
/// （设计方案 §3.1）。因此测试不再各自 new 一个 bool 状态，而是统一从这里取，
/// 让"又一个权威实例"在测试里也无法悄悄出现。
/// </summary>
internal static class TestAuthorities
{
    internal static BrowserAutomationAuthority Create(
        DesktopInstanceId? desktopId = null,
        DesktopProcessInstanceId? processInstanceId = null,
        TimeProvider? clock = null) =>
        new(
            desktopId ?? new DesktopInstanceId("desk-test"),
            processInstanceId ?? new DesktopProcessInstanceId("proc-test"),
            clock);

    /// <summary>生命周期端口 = 权威 + 两个注册表 + 关闭清理点，三者必须配套使用。</summary>
    internal static BrowserPageLifecycle CreateLifecycle(
        DesktopTargetRegistry? pageTargets = null,
        BrowserAutomationAuthority? authority = null,
        BrowserTargetRegistry? browserTargets = null) =>
        new(
            browserTargets ?? new BrowserTargetRegistry(),
            pageTargets ?? new DesktopTargetRegistry(),
            authority ?? Create());
}
