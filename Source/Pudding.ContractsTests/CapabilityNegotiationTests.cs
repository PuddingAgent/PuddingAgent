using Pudding.Contracts;

namespace Pudding.ContractsTests;

/// <summary>
/// 握手协商：Core 只能授予 Desktop 已声明的能力与不高于声明版本的版本号。
/// </summary>
public sealed class CapabilityNegotiationTests
{
    private static readonly DesktopCapability[] Declared =
    [
        DesktopCapability.WebViewNavigate,
        DesktopCapability.WebViewExecuteJavascript,
        DesktopCapability.ShellNotification,
    ];

    private static DesktopCapabilityDeclaration[] Declare(params DesktopCapability[] capabilities) =>
        DesktopCapabilities.DeclareFor(capabilities.Aggregate(DesktopCapability.None, (acc, c) => acc | c)).ToArray();

    [Fact]
    public void EmptyDeclaration_FailsClosed()
    {
        var error = DesktopCapabilityNegotiation.Validate([], [], out var negotiated, out var declarations);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, error!.Code);
        Assert.Equal(DesktopCapability.None, negotiated);
        Assert.Empty(declarations);
    }

    [Fact]
    public void NullOrEmptyGrant_YieldsNoCapabilitiesButSucceeds()
    {
        Assert.Null(DesktopCapabilityNegotiation.Validate(Declare(Declared), null, out var negotiated, out var declarations));
        Assert.Equal(DesktopCapability.None, negotiated);
        Assert.Empty(declarations);

        Assert.Null(DesktopCapabilityNegotiation.Validate(Declare(Declared), [], out negotiated, out declarations));
        Assert.Equal(DesktopCapability.None, negotiated);
    }

    [Fact]
    public void SubsetGrant_IsAccepted()
    {
        var granted = Declare(DesktopCapability.WebViewNavigate, DesktopCapability.ShellNotification);

        var error = DesktopCapabilityNegotiation.Validate(
            Declare(Declared), granted, out var negotiated, out var declarations);

        Assert.Null(error);
        Assert.Equal(DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification, negotiated);
        Assert.Equal(2, declarations.Count);
    }

    [Fact]
    public void GrantOfUndeclaredCapability_IsProtocolError()
    {
        var granted = Declare(DesktopCapability.ShellClipboard);

        var error = DesktopCapabilityNegotiation.Validate(
            Declare(Declared), granted, out var negotiated, out var declarations);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, error!.Code);
        // 越权授予不做「部分接受」：协商失败时不产生任何可用能力。
        Assert.Equal(DesktopCapability.None, negotiated);
        Assert.Empty(declarations);
    }

    [Fact]
    public void GrantWithHigherVersion_IsProtocolError()
    {
        var declared = new[] { new DesktopCapabilityDeclaration(DesktopCapability.WebViewNavigate, "webview.navigate", 1) };
        var granted = new[] { new DesktopCapabilityDeclaration(DesktopCapability.WebViewNavigate, "webview.navigate", 2) };

        var error = DesktopCapabilityNegotiation.Validate(declared, granted, out var negotiated, out _);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, error!.Code);
        Assert.Equal(DesktopCapability.None, negotiated);
    }

    [Fact]
    public void DuplicateDeclaration_IsRejected()
    {
        var declared = new[]
        {
            new DesktopCapabilityDeclaration(DesktopCapability.WebViewNavigate, "webview.navigate", 1),
            new DesktopCapabilityDeclaration(DesktopCapability.WebViewNavigate, "webview.navigate", 1),
        };

        var error = DesktopCapabilityNegotiation.Validate(declared, [], out _, out _);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, error!.Code);
    }

    [Fact]
    public void NameMismatch_IsRejected()
    {
        var declared = new[] { new DesktopCapabilityDeclaration(DesktopCapability.WebViewNavigate, "webview.goto", 1) };

        var error = DesktopCapabilityNegotiation.Validate(declared, [], out _, out _);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, error!.Code);
    }

    [Fact]
    public void UnregisteredDeclaredCapability_IsRejected()
    {
        var declared = new[] { new DesktopCapabilityDeclaration((DesktopCapability)(1 << 20), "webview.unknown", 1) };

        var error = DesktopCapabilityNegotiation.Validate(declared, [], out _, out _);

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, error!.Code);
    }
}
