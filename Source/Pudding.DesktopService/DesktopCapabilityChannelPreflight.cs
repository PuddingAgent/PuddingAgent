using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// Desktop 侧"要不要启动能力通道"的**单一判定入口**：组合根只调用它一次，并把理由写进启动日志。
///
/// 判定规则（fail closed，且**绝不跨传输回退**）：
/// ①设置未启用 ⇒ 不启动（继续走既有 WebSocket Bridge，行为与今天一致）；
/// ②启用但拿不到/解析不了 Core 发布的端点描述 ⇒ **不启动**（宁可暂时不启用，也不猜端点、
///   更不像"先试通道、失败再退回 Bridge"那样可能把同一次操作执行两次）；
/// ③启用且解析成功 ⇒ 启动，并报告声明/连接选项是否构造成功。
/// </summary>
public sealed record DesktopCapabilityChannelPreflightReport(
    bool ShouldStart,
    string Summary,
    IReadOnlyList<string> Notes)
{
    public static DesktopCapabilityChannelPreflightReport Skipped(string reason) =>
        new(false, $"capability channel stays disabled: {reason}", [reason]);
}

public static class DesktopCapabilityChannelPreflight
{
    public static DesktopCapabilityChannelPreflightReport Evaluate(
        DesktopCapabilityChannelSettings? settings,
        DesktopProcessInstanceId processInstanceId,
        DesktopChannelAuthentication? authentication,
        string? endpointDescription)
    {
        if (settings is null || !settings.Enabled)
        {
            // 缺省关闭：不注册、不连接、不改行为。回滚即回到这里。
            return DesktopCapabilityChannelPreflightReport.Skipped("Desktop:CapabilityChannel:Enabled is false");
        }

        var options = settings.CreateConnectionOptions(processInstanceId, authentication);
        if (options.IsFailure)
        {
            return DesktopCapabilityChannelPreflightReport.Skipped(
                $"connection options rejected: {options.Error.Code}");
        }

        var transport = settings.ResolveTransportFromDescription(endpointDescription);
        if (transport.IsFailure)
        {
            // 描述缺失/不可解析：不启动，也**不回退**到旧传输（避免同一操作被执行两次）。
            return DesktopCapabilityChannelPreflightReport.Skipped(
                $"core endpoint description is missing or not usable: {transport.Error.Code}");
        }

        var notes = new List<string>
        {
            $"declared capabilities: {DesktopCapabilityChannelSettings.DeclaredCapabilities}",
            $"transport: {transport.Value}",
        };

        return new DesktopCapabilityChannelPreflightReport(
            true,
            $"capability channel can start on {transport.Value}",
            notes);
    }
}