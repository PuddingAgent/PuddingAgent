using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>预检结论：逐项通过名单 + 失败原因（失败原因不含任何凭据）。</summary>
public sealed record CapabilityChannelPreflightReport(
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Failures)
{
    public bool IsHealthy => Failures.Count == 0;

    public string Summary => IsHealthy
        ? $"capability channel preflight ok: {string.Join("; ", Checks)}"
        : $"capability channel preflight FAILED: {string.Join("; ", Failures)}";
}

/// <summary>
/// 能力通道启动预检：把接线手册里的两条**手工检查**变成可复用判定。
///
/// 背景（本系列实测约束）：`KestrelServerOptions.Listen*` 会覆盖 `UseUrls`，
/// 一旦为能力通道新增监听，原有 REST 端点可能**静默消失**。这类故障在启动时判定一次，
/// 比等运维发现 REST 不可用便宜得多。产品可在启动日志里输出 <see cref="CapabilityChannelPreflightReport.Summary"/>。
///
/// 判定只看「有没有」，不解析凭据，也不读页面数据。
/// </summary>
public static class CapabilityChannelPreflight
{
    /// <summary>
    /// 检查绑定结果与就绪描述。
    /// </summary>
    /// <param name="addresses">启动后 `IServerAddressesFeature.Addresses` 的内容。</param>
    /// <param name="expectedRestAddresses">应当仍然在监听的 REST 地址（例如配置里的 `urls`）。</param>
    /// <param name="description">准备发布的能力端点描述；通道关闭时为 <c>null</c>。</param>
    public static CapabilityChannelPreflightReport Check(
        IEnumerable<string>? addresses,
        IEnumerable<string>? expectedRestAddresses,
        DesktopCapabilityEndpoint? description)
    {
        var observed = (addresses ?? []).ToArray();
        var expected = (expectedRestAddresses ?? []).ToArray();
        var checks = new List<string>();
        var failures = new List<string>();

        foreach (var restAddress in expected)
        {
            if (observed.Any(address => SameEndpoint(address, restAddress)))
            {
                checks.Add($"REST 仍在监听 {restAddress}");
                continue;
            }

            // 这正是「Listen* 覆盖 UseUrls」的失败形态：必须显式指出，避免被当成网络问题排查。
            failures.Add(
                $"REST 地址 {restAddress} 未出现在监听列表（常见原因：为能力通道调用 Kestrel.Listen* 时覆盖了 UseUrls；"
                + "组合根必须显式绑定 REST 与能力通道两者）");
        }

        if (description is null)
        {
            checks.Add("能力通道未启用（未发布端点描述）");
        }
        else if (observed.Any(address => SameEndpoint(address, ExpectedAddress(description))))
        {
            checks.Add($"能力端点已监听 {description.Address}");
        }
        else
        {
            failures.Add($"能力端点 {description.Address}（{description.Kind}）未出现在监听列表");
        }

        if (description is not null && description.ServerInstanceId is null)
        {
            // 不是致命错误，但会让 Desktop 无法识别「Core 是否换了实例」，重连语义会退化。
            failures.Add("端点描述缺少 Core 实例 ID：Desktop 无法识别 Core 是否更换实例（重连语义退化）");
        }

        return new CapabilityChannelPreflightReport(checks, failures);
    }

    private static string ExpectedAddress(DesktopCapabilityEndpoint description) =>
        description.Kind == DesktopCapabilityEndpointKind.NamedPipe
            ? $@"\\.\pipe\{description.Address}"
            : description.Address;

    /// <summary>宽松比较：忽略大小写与结尾斜杠，兼容 `http://127.0.0.1:5000` 与 `http://127.0.0.1:5000/`。</summary>
    private static bool SameEndpoint(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
