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
    {
        switch (capability)
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
}
