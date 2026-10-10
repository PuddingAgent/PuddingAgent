using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 能力 → 自动化操作映射（准入的前置件）。**关键判据**：能力目录里每个 WebView 系能力都必须有映射，
/// 否则新能力会静默绕过闸门（本系列反复出现的失败形态）。
/// </summary>
public sealed class DesktopCapabilityOperationsTests
{
    [Theory]
    [InlineData(DesktopCapability.WebViewNavigate, BrowserAutomationOperation.Navigate)]
    [InlineData(DesktopCapability.WebViewExecuteJavascript, BrowserAutomationOperation.Script)]
    [InlineData(DesktopCapability.WebViewPageState, BrowserAutomationOperation.PageState)]
    [InlineData(DesktopCapability.BrowserSnapshot, BrowserAutomationOperation.Snapshot)]
    [InlineData(DesktopCapability.BrowserLocate, BrowserAutomationOperation.Locate)]
    [InlineData(DesktopCapability.BrowserInteract, BrowserAutomationOperation.Interact)]
    [InlineData(DesktopCapability.BrowserWaitFor, BrowserAutomationOperation.WaitFor)]
    [InlineData(DesktopCapability.BrowserContexts, BrowserAutomationOperation.ContextsList)]
    [InlineData(DesktopCapability.BrowserContextCreate, BrowserAutomationOperation.ContextCreate)]
    [InlineData(DesktopCapability.BrowserContextClose, BrowserAutomationOperation.ContextClose)]
    public void WebViewCapabilitiesMapToTheirAutomationOperation(
        DesktopCapability capability,
        BrowserAutomationOperation expected)
    {
        Assert.True(DesktopCapabilityOperations.TryGetBrowserOperation(capability, out var operation));
        Assert.Equal(expected, operation);
    }

    [Fact]
    public void ShellCapabilitiesAreNotBrowserOperations()
    {
        // 非浏览器能力**不得**被要求 browser grant，否则会出现"通知也要页面授权"这类错拒。
        foreach (var shell in new[]
                 {
                     DesktopCapability.ShellNotification,
                     DesktopCapability.ShellStatus,
                     DesktopCapability.ShellDialog,
                     DesktopCapability.ShellFilePicker,
                     DesktopCapability.ShellClipboard,
                 })
        {
            // 返回值即判据（出参在 false 时无意义，不得当作操作使用）。
            Assert.False(DesktopCapabilityOperations.TryGetBrowserOperation(shell, out _), shell.ToString());
        }
    }

    [Fact]
    public void EveryWebViewCapabilityInTheCatalogHasAMapping()
    {
        // **防静默绕过**：目录里凡属 WebView 系的能力都必须有映射。新增能力却忘记映射时，本用例变红。
        var unmapped = DesktopCapabilities.All
            .Where(descriptor => descriptor.Kind == DesktopCapabilityKind.WebView)
            .Where(descriptor => !DesktopCapabilityOperations.TryGetBrowserOperation(descriptor.Capability, out _))
            .Select(descriptor => descriptor.Name)
            .ToArray();

        Assert.Empty(unmapped);
    }
}
