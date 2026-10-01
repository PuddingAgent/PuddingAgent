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

            DesktopCapability.ShellClipboard => ReadClipboard(request),

            DesktopCapability.BrowserTabs => Tabs(request),

            DesktopCapability.BrowserContexts => Task.FromResult(DesktopCapabilityResponse.FromContexts(Contexts())),

            DesktopCapability.BrowserWaitFor => WaitFor(request),

            DesktopCapability.BrowserInteract => Interact(request),

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


    /// <summary>交互：返回的状态版本比请求高一级 ⇒ 交互前的 Ref 在 Core 侧立即作废。</summary>
    private static Task<DesktopCapabilityResponse> Interact(DesktopCapabilityRequest request)
    {
        var interact = request.Interact ?? throw new InvalidOperationException("probe: interact payload missing");
        var nextVersion = DesktopPageVersion.Require(interact.ExpectedPageVersion.Value + 1);

        return Task.FromResult(DesktopCapabilityResponse.FromInteract(new DesktopInteractionResult(
            interact.Target,
            new DesktopPageState(interact.Target, new Uri("https://example.com/probe-after-interact"), nextVersion, DesktopPageReadiness.Complete),
            interact.Locator is null ? null : new DesktopElementRef("e1", "button", nextVersion, role: "button", name: "probe"))));
    }

    /// <summary>等待：条件含 "never" 时演示超时语义（TimedOut=true，仍然回带页面状态）。</summary>
    private static Task<DesktopCapabilityResponse> WaitFor(DesktopCapabilityRequest request)
    {
        var wait = request.WaitFor ?? throw new InvalidOperationException("probe: wait_for payload missing");
        var timedOut = wait.Condition.Value.Contains("never", StringComparison.OrdinalIgnoreCase);

        return Task.FromResult(DesktopCapabilityResponse.FromWait(new DesktopWaitResult(
            wait.Target,
            wait.Condition,
            timedOut,
            new DesktopPageState(
                wait.Target,
                new Uri("https://example.com/probe-waited"),
                DesktopPageVersion.Require(5),
                DesktopPageReadiness.Complete),
            timedOut ? "probe: condition never satisfied" : null)));
    }

    /// <summary>上下文清单：一个 Agent 上下文 + 两个页面（含活动页与导航能力标志）。</summary>
    private static DesktopContexts Contexts() => new(
    [
        new DesktopContextInfo("ctx-probe", DesktopContextTrust.AgentAuthorized,
        [
            new DesktopPageInfo(
                new DesktopPageTarget("ctx-probe", "page-probe"),
                DesktopPageVersion.Require(5),
                title: "探针页",
                url: new Uri("https://example.com/probe"),
                isActive: true,
                isAgentTarget: true,
                canGoBack: true),
            new DesktopPageInfo(
                new DesktopPageTarget("ctx-probe", "page-2"),
                DesktopPageVersion.Require(2),
                title: "第二页",
                url: new Uri("https://example.com/second"),
                isLoading: true),
        ]),
    ]);

    /// <summary>标签页：关闭后剩余清单为空、激活后活动页切换（均为版本 +1）。</summary>
    private static Task<DesktopCapabilityResponse> Tabs(DesktopCapabilityRequest request)
    {
        var tabs = request.Tabs ?? throw new InvalidOperationException("probe: tabs payload missing");
        var next = DesktopPageVersion.Require(tabs.ExpectedPageVersion.Value + 1);
        var closed = tabs.Action == DesktopTabAction.Close;

        var remaining = closed
            ? new DesktopContexts([])
            : new DesktopContexts(
            [
                new DesktopContextInfo("ctx-probe", DesktopContextTrust.AgentAuthorized,
                [
                    new DesktopPageInfo(tabs.Target, next, title: "activated", isActive: true, isAgentTarget: true),
                ]),
            ]);

        return Task.FromResult(DesktopCapabilityResponse.FromTabs(new DesktopTabsResult(
            tabs.Target,
            tabs.Action,
            new DesktopPageState(tabs.Target, new Uri("https://example.com/probe-tab"), next, DesktopPageReadiness.Complete),
            closed,
            remaining)));
    }

    /// <summary>剪贴板：回带 100 字符文本；预算很小则演示截断语义。</summary>
    private static Task<DesktopCapabilityResponse> ReadClipboard(DesktopCapabilityRequest request)
    {
        var clipboard = request.Clipboard ?? throw new InvalidOperationException("probe: clipboard payload missing");
        var text = new string('c', 100);
        var truncated = text.Length > clipboard.MaxCharacters;

        return Task.FromResult(DesktopCapabilityResponse.FromClipboard(new DesktopClipboardContent(
            truncated ? text[..clipboard.MaxCharacters] : text, truncated)));
    }
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
