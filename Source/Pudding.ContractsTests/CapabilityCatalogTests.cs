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
            "webview.navigate|v1|WebView|Mutating, HasSideEffects",
            "webview.execute_javascript|v1|WebView|Mutating, HasSideEffects, RequiresTrustedContext",
            "webview.page_state|v1|WebView|None",
            "shell.notification|v1|Shell|HasSideEffects",
            "shell.status|v1|Shell|None",
            "shell.dialog|v1|Shell|HasSideEffects, RequiresTrustedContext, RequiresUserInteraction",
            "shell.file_picker|v1|Shell|HasSideEffects, RequiresTrustedContext, RequiresUserInteraction",
            "shell.clipboard|v1|Shell|Mutating, HasSideEffects, RequiresTrustedContext",
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
        Assert.Empty(DesktopCapabilities.Enumerate((DesktopCapability)(1 << 20)));
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
        Assert.Equal(DesktopCapabilityTraits.None, state.Traits);
    }
}
