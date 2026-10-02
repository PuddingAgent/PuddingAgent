using Microsoft.Extensions.Logging.Abstractions;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;
using Xunit;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 权限证据链**第二阶段**：副作用类能力的闸门（fail closed）。
///
/// 只测闸门本身：路由固定为"两条都不可用"（通道为 null、旧 Bridge 不可用），因此被拒/放行之后
/// 都不会真的碰到传输 —— 这正是本用例要隔离的变量。副作用能力的**放行**路径由工具层用例
/// （AgentTools 的证据携带断言）与 Bridge 集成用例覆盖。
/// </summary>
public sealed class CapabilityPermissionEvidenceGateTests
{
    private static (TransportRoutedBrowserCapabilitySurface Surface, DesktopTransportUsageTracker Tracker) Build()
    {
        var tracker = new DesktopTransportUsageTracker();
        // 旧 Bridge 传 null 是有意的：路由恒为"两条都不可用"，桥接实现永不被解引用。
        var surface = new TransportRoutedBrowserCapabilitySurface(
            legacyBridge: null!,
            activeChannel: () => null,
            legacyBridgeAvailable: () => false,
            usage: tracker);
        return (surface, tracker);
    }

    private static DesktopCallContext Call(string? evidence) => new(
        new DesktopInstanceId("desk-test"),
        OperationId.NewId(),
        DateTimeOffset.UtcNow.AddMinutes(1))
    {
        PermissionEvidenceSummary = evidence,
    };

    [Fact]
    public async Task SideEffectingCapability_WithoutEvidence_IsRefused_AndCounted()
    {
        var (surface, tracker) = Build();

        var result = await surface.InteractAsync(
            new BrowserInteractRequest(
                new DesktopPageTarget("ctx-1", "page-1"),
                DesktopInteractionAction.Click,
                DesktopPageVersion.Require(1),
                new DesktopLocator(DesktopLocatorKind.Css, "#go")),
            Call(evidence: null));

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(1, tracker.MissingEvidenceCalls);
    }

    [Fact]
    public async Task SideEffectingCapability_WithDeniedEvidence_IsRefused_ButNotCountedAsMissing()
    {
        var (surface, tracker) = Build();

        var result = await surface.NavigateAsync(
            new NavigateRequest(
                new DesktopPageTarget("ctx-1", "page-1"),
                new Uri("https://example.test/"),
                DesktopPageVersion.Require(1),
                DesktopNavigationAction.Goto),
            Call("decision=denied;source=workspace-guard"));

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        // 有证据（只是不允许）≠ 缺证据：观测计数只统计"绕过工具层"的那一类。
        Assert.Equal(0, tracker.MissingEvidenceCalls);
    }

    [Theory]
    [InlineData("decision=allowed;source=workspace-guard")]
    [InlineData("decision=not-required;source=no-guard-configured")]
    public async Task SideEffectingCapability_WithSufficientEvidence_PassesTheGate(string evidence)
    {
        var (surface, tracker) = Build();

        var result = await surface.TabsAsync(
            new BrowserTabsRequest(
                new DesktopPageTarget("ctx-1", "page-1"), DesktopTabAction.Activate, DesktopPageVersion.Require(1)),
            Call(evidence));

        // 过了闸门就会去路由；此处两条传输都不可用 ⇒ 失败原因是"无路由"，**不是**权限拒绝。
        Assert.True(result.IsFailure);
        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, tracker.MissingEvidenceCalls);
    }

    [Fact]
    public async Task ReadOnlyCapability_NeedsNoEvidence()
    {
        var (surface, tracker) = Build();

        var result = await surface.GetContextsAsync(Call(evidence: null));

        Assert.True(result.IsFailure);
        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, tracker.MissingEvidenceCalls);
    }
}
