using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// I2-c 接线缝：让已经通过会话准入的命令改由**工作区执行器**执行。
///
/// 约定：
/// · 返回非 null = 本命令由执行器路由处理（结果必须真实反映执行事实）；
/// · 返回 null = 本命令不在执行器覆盖面内，回退 <see cref="IBrowserCommandHandler"/>（迁移期）；
/// · 抛异常 = 执行结果未知，**不得**静默回退（否则双执行/绕过门禁）；由派发器按未知结果处理。
/// </summary>
public delegate Task<BrowserBridgeCommandResult?> BrowserCommandExecutorRoute(
    BrowserBridgeCommand command,
    CancellationToken cancellationToken);
