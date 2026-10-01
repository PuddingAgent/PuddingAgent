using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.Rpc.IpcProbe;

/// <summary>探针执行器：模拟 WinUI 侧的桌面能力实现（不触碰真实 UI）。</summary>
internal sealed class ProbeExecutor : IDesktopCapabilityExecutor
{
    public Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken) =>
        capability.Capability switch
        {
            DesktopCapability.WebViewNavigate => Task.FromResult(DesktopCapabilityResponse.FromNavigate(
                new NavigateResult(
                    NavigateDisposition.Completed,
                    request.Navigate!.Url,
                    DesktopPageVersion.Require(1)))),

            DesktopCapability.WebViewExecuteJavascript => ExecuteJavascriptAsync(request.Javascript!, cancellationToken),

            DesktopCapability.ShellNotification => Task.FromResult(DesktopCapabilityResponse.FromNotification(
                new DesktopNotificationResult(true, "probe-notification"))),

            DesktopCapability.WebViewPageState => Task.FromResult(DesktopCapabilityResponse.FromPageState(
                new DesktopPageState(
                    request.PageState ?? new DesktopPageTarget("ctx-probe", "page-probe"),
                    new Uri("https://example.com/probe-state"),
                    DesktopPageVersion.Require(5),
                    DesktopPageReadiness.Complete))),

            DesktopCapability.BrowserLocate => Task.FromResult(DesktopCapabilityResponse.FromLocate(
                new DesktopLocateResult(
                    request.Locate?.Target ?? new DesktopPageTarget("ctx-probe", "page-probe"),
                    request.Locate?.Locator ?? new DesktopLocator(DesktopLocatorKind.Css, "button"),
                    [
                        // checked 三态：true / false / 未知(null) 必须都能原样过线。
                        new DesktopElementRef(
                            "e1", "button", DesktopPageVersion.Require(5), role: "button", name: "probe-on", isChecked: true),
                        new DesktopElementRef(
                            "e2", "button", DesktopPageVersion.Require(5), role: "button", name: "probe-off", isChecked: false),
                        new DesktopElementRef(
                            "e3", "div", DesktopPageVersion.Require(5), role: "generic", name: "probe-unknown"),
                    ],
                    truncated: false,
                    DesktopPageVersion.Require(5)))),

            DesktopCapability.BrowserSnapshot => Task.FromResult(DesktopCapabilityResponse.FromSnapshot(
                new DesktopSnapshot(
                    request.Snapshot?.Target ?? new DesktopPageTarget("ctx-probe", "page-probe"),
                    "body > main > h1",
                    "document:Probe",
                    null,
                    truncated: request.Snapshot is { Options.MaxNodes: < 10 },
                    nodeCount: 42,
                    DesktopPageVersion.Require(5)))),

            DesktopCapability.ShellStatus => Task.FromResult(DesktopCapabilityResponse.FromShellStatus(
                new DesktopShellStatus(
                    DesktopWindowState.HiddenToTray,
                    trayVisible: true,
                    DesktopAutomationState.Free,
                    openPageCount: 3))),

            _ => Task.FromResult(DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.UnsupportedCapability(capability.Name))),
        };

    private static Task<DesktopCapabilityResponse> ExecuteJavascriptAsync(
        JavascriptRequest request, CancellationToken cancellationToken)
    {
        if (request.Script.Contains("hang", StringComparison.Ordinal))
        {
            // 取消语义探针：执行器不完成，只有取消/期限能让它结束。
            return HangAsync(cancellationToken);
        }

        var characters = Math.Max(2, request.MaxResultBytes);
        var payload = string.Concat("\"", new string('x', characters - 2), "\"");
        return Task.FromResult(DesktopCapabilityResponse.FromJavascript(
            new JavascriptResult(JavascriptValueKind.String, payload, Truncated: false)));
    }

    private static async Task<DesktopCapabilityResponse> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return DesktopCapabilityResponse.Failure(DesktopCapabilityError.Internal("unreachable"));
    }
}
