namespace Pudding.Contracts.Desktop;

/// <summary>
/// **能力 → 自动化操作**的映射（方案 §4.2 的前置件）。
///
/// 为什么必须有：准入（<c>Admit</c>）判的是 <see cref="BrowserAutomationOperation"/>，而能力通道与 Bridge
/// 传的是**能力**。此前只有个别分支按字面量传操作 ⇒ 新能力一旦忘了映射，就会**静默绕过闸门**。
/// 因此这里给出唯一真源，并由用例保证：**能力目录里每个 WebView 系能力都必须有映射**。
///
/// Shell 系能力（通知/状态/对话框/文件选择器/剪贴板）不是浏览器操作 ⇒ 返回 <c>false</c>（不适用），
/// 调用方据此**不要求** browser grant（方案 §4.2：不能把非浏览器能力错误地要求 browser grant）。
/// </summary>
public static class DesktopCapabilityOperations
{
    /// <summary>尝试取得该能力对应的自动化操作；非浏览器能力返回 <c>false</c>。</summary>
    public static bool TryGetBrowserOperation(DesktopCapability capability, out BrowserAutomationOperation operation)
    {        switch (capability)
        {
            case DesktopCapability.WebViewNavigate:
                operation = BrowserAutomationOperation.Navigate;
                return true;
            case DesktopCapability.WebViewExecuteJavascript:
                operation = BrowserAutomationOperation.Script;
                return true;
            case DesktopCapability.WebViewPageState:
                operation = BrowserAutomationOperation.PageState;
                return true;
            case DesktopCapability.BrowserSnapshot:
                operation = BrowserAutomationOperation.Snapshot;
                return true;
                case DesktopCapability.BrowserScreenshot:
                operation = BrowserAutomationOperation.Screenshot;
                return true;
            case DesktopCapability.BrowserLocate:
                operation = BrowserAutomationOperation.Locate;
                return true;
            case DesktopCapability.BrowserInteract:
                operation = BrowserAutomationOperation.Interact;
                return true;
            case DesktopCapability.BrowserWaitFor:
                operation = BrowserAutomationOperation.WaitFor;
                return true;
            case DesktopCapability.BrowserContexts:
                operation = BrowserAutomationOperation.ContextsList;
                return true;
            case DesktopCapability.BrowserContextCreate:
                operation = BrowserAutomationOperation.ContextCreate;
                return true;
            case DesktopCapability.BrowserContextClose:
                operation = BrowserAutomationOperation.ContextClose;
                return true;
            case DesktopCapability.BrowserTabs:
                // 标签页能力同时含"激活"与"关闭"：按**最严**的那一个映射（关闭是变更类），
                // 这样激活也不会因为"操作更轻"而少要一次授权。
                operation = BrowserAutomationOperation.TabClose;
                return true;
            default:
                // 调用方**必须**先看返回值（TryGet 惯例）；这个默认值不代表任何操作。
                operation = default;
                return false;
        }
    }

    /// <summary>
    /// **request-aware** 映射（方案 §5.3）：同一能力下的不同**具体动作**可能有**不同的最小准入**。
    ///
    /// 现修 `browser.tabs`：`new`（Management）· `activate`（目标页 Write，并按 UI 焦点副作用走控制许可）·
    /// `close`（目标页 Manage|Read）必须**分别**映射 —— 旧实现一律当作 `TabClose`，于是 `activate`/`new`
    /// 都被套上"最严"的关闭判据（2026-10-12 复核定位）。
    ///
    /// 注：契约侧 `DesktopTabAction` 只有 New/Activate/Close（**没有 list**）——
    /// "列出标签页"走的是 `browser.contexts`（<see cref="BrowserAutomationOperation.ContextsList"/>）。
    /// 其余能力仍走能力级映射。
    /// </summary>
    public static bool TryGetBrowserOperation(
        DesktopCapabilityRequest request,
        out BrowserAutomationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Tabs is { } tabs)
        {
            switch (tabs.Action)
            {
                case DesktopTabAction.New:
                    operation = BrowserAutomationOperation.TabNew;
                    return true;
                case DesktopTabAction.Activate:
                    operation = BrowserAutomationOperation.TabActivate;
                    return true;
                case DesktopTabAction.Close:
                    operation = BrowserAutomationOperation.TabClose;
                    return true;
                default:
                    // 未来新增动作若未在这里登记 ⇒ 明确不支持，而不是蒙成某个已有操作。
                    operation = default;
                    return false;
            }
        }

        return TryGetBrowserOperation(CapabilityOf(request), out operation);
    }

    /// <summary>取出请求的能力标签（用于能力级映射的回退路径）。</summary>
    private static DesktopCapability CapabilityOf(DesktopCapabilityRequest request)
    {
        // 请求是"载荷联合"：由哪个载荷非空决定能力。这里只覆盖**需要浏览器操作**的那几个；
        // 其余（Shell 系）返回一个必然命中 default 的值 ⇒ TryGetBrowserOperation 返回 false。
        if (request.Tabs is not null)
        {
            return DesktopCapability.BrowserTabs;
        }

        if (request.Screenshot is not null)
        {
            return DesktopCapability.BrowserScreenshot;
        }

        if (request.Snapshot is not null)
        {
            return DesktopCapability.BrowserSnapshot;
        }

        if (request.Locate is not null)
        {
            return DesktopCapability.BrowserLocate;
        }

        if (request.Interact is not null)
        {
            return DesktopCapability.BrowserInteract;
        }

        if (request.WaitFor is not null)
        {
            return DesktopCapability.BrowserWaitFor;
        }

        return DesktopCapability.None;
    }
}
