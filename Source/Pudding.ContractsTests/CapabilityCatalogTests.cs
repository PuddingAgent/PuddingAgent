using Pudding.Contracts;

namespace Pudding.ContractsTests;

/// <summary>
/// 能力目录是协商身份的真源：线名/版本/执行语义一旦改变就是破坏性变更，
/// 因此用快照断言冻结，而不是靠约定。
/// </summary>
public sealed class CapabilityCatalogTests
{
    [Fact]
    public void Catalog_IsFrozenSnapshot()
    {
        var actual = DesktopCapabilities.All
            .Select(d => $"{d.Name}|v{d.Version}|{d.Kind}|{d.Traits}")
            .ToArray();

        string[] expected =
        [
            "webview.navigate|v1|WebView|Mutating, HasSideEffects, RequiresPageTarget",
            "webview.execute_javascript|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext, RequiresPageTarget",
            "webview.page_state|v1|WebView|RequiresPageTarget",
            "shell.notification|v1|Shell|HasSideEffects",
            "shell.status|v1|Shell|None",
            "shell.dialog|v1|Shell|HasSideEffects, RequiresTrustedContext, RequiresUserInteraction",
            "shell.file_picker|v1|Shell|HasSideEffects, RequiresTrustedContext, RequiresUserInteraction",
            "shell.clipboard|v1|Shell|RequiresTrustedContext",
            "shell.document_preview|v1|Shell|HasSideEffects, RequiresTrustedContext",
            "shell.panel_render|v1|Shell|HasSideEffects, RequiresTrustedContext",
            "browser.snapshot|v1|WebView|RequiresTrustedContext, RequiresPageTarget",
            "browser.screenshot|v1|WebView|RequiresTrustedContext, RequiresPageTarget",
            "browser.locate|v1|WebView|RequiresTrustedContext, RequiresPageTarget",
            "browser.interact|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext, RequiresPageTarget",
            "browser.wait_for|v1|WebView|RequiresTrustedContext, RequiresPageTarget",
            "browser.contexts|v1|WebView|RequiresTrustedContext",
            "browser.context.create|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext",
            "browser.context.close|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext",
            "browser.tabs|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext, RequiresPageTarget",
        // 控制面（§4.1）：刻意新增。
        "browser.binding.apply|v1|Control|RequiresTrustedContext",
        "browser.binding.revoke|v1|Control|RequiresTrustedContext",
        ];

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, DesktopCapabilities.All.Select(d => d.Capability).Distinct().Count());
    }

    [Fact]
    public void AllCapabilities_IsUnionOfCatalog()
    {
        var union = DesktopCapabilities.All.Aggregate(DesktopCapability.None, (acc, d) => acc | d.Capability);
        Assert.Equal(union, DesktopCapabilities.AllCapabilities);
    }

    [Fact]
    public void Enumerate_FollowsCatalogOrder_AndIgnoresUnknownBits()
    {
        var set = DesktopCapability.ShellNotification | DesktopCapability.WebViewNavigate;
        var enumerated = DesktopCapabilities.Enumerate(set).ToArray();

        Assert.Equal([DesktopCapability.WebViewNavigate, DesktopCapability.ShellNotification], enumerated);
        Assert.Empty(DesktopCapabilities.Enumerate(DesktopCapability.None));

        // “未知位”必须是真的未知：写死的位一旦被登记（历史教训：1 << 20 变成了 shell.panel_render），
        // 这条断言就会变成「列举一个已登记能力居然为空」——仍然会红，但红到了错的方向。
        var unused = FindUnregisteredCapabilityBit();
        Assert.Empty(DesktopCapabilities.Enumerate(unused));
    }

    [Fact]
    public void DeclareFor_ProducesWireNamesAndVersions()
    {
        var declarations = DesktopCapabilities.DeclareFor(DesktopCapability.WebViewPageState | DesktopCapability.ShellNotification);

        Assert.Equal(2, declarations.Count);
        Assert.Equal("webview.page_state", declarations[0].Name);
        Assert.Equal(DesktopCapability.WebViewPageState, declarations[0].Capability);
        Assert.Equal(DesktopCapabilities.InitialVersion, declarations[0].Version);
        Assert.Equal("shell.notification", declarations[1].Name);
    }

    [Fact]
    public void TryGetByName_IsCaseSensitive()
    {
        Assert.True(DesktopCapabilities.TryGetByName("webview.navigate", out var descriptor));
        Assert.Equal(DesktopCapability.WebViewNavigate, descriptor.Capability);
        Assert.False(DesktopCapabilities.TryGetByName("WebView.Navigate", out _));
        Assert.False(DesktopCapabilities.TryGetByName(null, out _));
        Assert.False(DesktopCapabilities.TryGetByName(string.Empty, out _));
    }

    [Fact]
    public void NameOf_RejectsNoneAndComposite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopCapabilities.NameOf(DesktopCapability.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopCapabilities.NameOf(DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification));
        Assert.Equal("webview.navigate", DesktopCapabilities.NameOf(DesktopCapability.WebViewNavigate));
    }

    [Fact]
    public void MutatingTraits_CoverPageAndClipboardWrites()
    {
        Assert.True(DesktopCapabilities.TryGet(DesktopCapability.WebViewExecuteJavascript, out var execute));
        Assert.True(execute.Traits.HasFlag(DesktopCapabilityTraits.Mutating));
        Assert.True(execute.Traits.HasFlag(DesktopCapabilityTraits.RequiresTrustedContext));

        Assert.True(DesktopCapabilities.TryGet(DesktopCapability.WebViewPageState, out var state));
        Assert.Equal(DesktopCapabilityTraits.RequiresPageTarget, state.Traits);
    }

    [Fact]
    public void RequiresPageTarget_MarksExactlyTheWebViewCapabilities()
    {
        var requiring = DesktopCapabilities.All
            .Where(d => d.Traits.HasFlag(DesktopCapabilityTraits.RequiresPageTarget))
            .Select(d => d.Capability)
            .ToArray();

        Assert.Equal(
            [
                DesktopCapability.WebViewNavigate,
                DesktopCapability.WebViewExecuteJavascript,
                DesktopCapability.WebViewPageState,
                DesktopCapability.BrowserSnapshot,
                DesktopCapability.BrowserScreenshot,
                DesktopCapability.BrowserLocate,
                DesktopCapability.BrowserInteract,
                DesktopCapability.BrowserWaitFor,
                DesktopCapability.BrowserTabs,
            ],
            requiring);

        // Shell 能力没有页面目标：可信级别由服务侧调用方策略决定，而不是从目标推断。
        Assert.All(
            DesktopCapabilities.All.Where(d => d.Kind == DesktopCapabilityKind.Shell),
            d => Assert.False(d.Traits.HasFlag(DesktopCapabilityTraits.RequiresPageTarget)));
    }

    [Fact]
    public void RequiresTrustedContext_IsNeverGrantedToUntrustedTargets()
    {
        // 契约层的自洽断言：标记为「需要可信上下文」的能力，级别语义必须由策略显式收窄（见 DesktopService）。
        var trustedOnly = DesktopCapabilities.All
            .Where(d => d.Traits.HasFlag(DesktopCapabilityTraits.RequiresTrustedContext))
            .Select(d => d.Name)
            .ToArray();

        Assert.Equal(
            ["webview.execute_javascript", "shell.dialog", "shell.file_picker", "shell.clipboard", "shell.document_preview", "shell.panel_render", "browser.snapshot", "browser.screenshot", "browser.locate", "browser.interact", "browser.wait_for", "browser.contexts", "browser.context.create", "browser.context.close", "browser.tabs", "browser.binding.apply", "browser.binding.revoke"],
            trustedOnly);
    }

    /// <summary>找到一个目录里确实没用到的位（从高位往下，避开 1&lt;&lt;31 的符号位）。</summary>
    private static DesktopCapability FindUnregisteredCapabilityBit()
    {
        var used = DesktopCapabilities.AllCapabilities;
        for (var shift = 30; shift >= 1; shift--)
        {
            var candidate = (DesktopCapability)(1 << shift);
            if ((used & candidate) == 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No unused capability bit is left for the unknown-bit assertion.");
    }
}
