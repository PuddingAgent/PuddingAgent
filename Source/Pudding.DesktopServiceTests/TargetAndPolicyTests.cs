using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>目标校验、可信级别策略与页面版本语义。</summary>
public sealed class TargetAndPolicyTests
{
    [Fact]
    public async Task Navigate_OnUnregisteredTarget_IsRejectedWithInvalidTarget()
    {
        var harness = ServiceHarness.Create();
        var unknown = new DesktopPageTarget("ctx-nope", "page-x");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(unknown));

        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
        Assert.Equal(0, harness.Dispatcher.QueuedCount);
    }

    [Fact]
    public async Task Navigate_OnClosedPage_IsRejectedWithInvalidTarget()
    {
        var harness = ServiceHarness.Create();
        harness.Targets.ClosePage(ServiceHarness.AgentPage);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task Navigate_IsAllowedOnEveryTrustLevel()
    {
        foreach (var target in new[] { ServiceHarness.AgentPage, ServiceHarness.WebPage, ServiceHarness.WorkbenchPage })
        {
            var harness = ServiceHarness.Create(hasThreadAccess: true);
            var response = await harness.ExecuteAsync(
                DesktopCapability.WebViewNavigate, harness.NavigateRequest(target));

            Assert.True(response.Navigate is not null, $"navigate should be allowed on {target.Key}");
        }
    }

    [Fact]
    public async Task JavaScript_IsDeniedOnUntrustedAndWorkbenchTargets()
    {
        var untrusted = ServiceHarness.Create(hasThreadAccess: true);
        var untrustedResponse = await untrusted.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript, untrusted.JavascriptRequest(ServiceHarness.WebPage));
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, untrustedResponse.Error!.Code);
        Assert.Equal(0, untrusted.Surface.JavascriptCount);

        // 硬不变式：可信工作台绝不允许被注入脚本。
        var workbench = ServiceHarness.Create(hasThreadAccess: true);
        var workbenchResponse = await workbench.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript, workbench.JavascriptRequest(ServiceHarness.WorkbenchPage));
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, workbenchResponse.Error!.Code);
        Assert.Equal(0, workbench.Surface.JavascriptCount);
    }

    [Fact]
    public async Task JavaScript_IsAllowedOnAuthorizedAgentTarget()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript, harness.JavascriptRequest(ServiceHarness.AgentPage));

        Assert.Equal(JavascriptValueKind.String, response.Javascript!.Kind);
        Assert.Equal(1, harness.Surface.JavascriptCount);
    }

    [Fact]
    public async Task ExpectedPageVersionMismatch_IsRejectedBeforeDispatch()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Targets.UpdatePage(ServiceHarness.AgentPage, DesktopPageVersion.Require(7));

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(1)));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task UnknownExpectedPageVersion_SkipsVersionCheck()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Targets.UpdatePage(ServiceHarness.AgentPage, DesktopPageVersion.Require(7));

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.NotNull(response.Navigate);
    }

    [Fact]
    public async Task CapabilityNotEnabled_IsRejectedWithUnsupportedCapability()
    {
        var harness = ServiceHarness.Create(allowed: DesktopCapability.ShellNotification, hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task UnregisteredCapability_IsRejectedAsUnsupported()
    {
        // not.registered.capability 在目录里已登记但本切片没有 payload；即使被启用/授权也不能假装执行。
        var harness = ServiceHarness.Create(
            allowed: (DesktopCapability)(1 << 20),
            shellCallerTrust: DesktopContextTrust.Workbench,
            hasThreadAccess: true);

        var response = await harness.Service.ExecuteAsync(ServiceHarness.UnregisteredCapabilityDescriptor(), DesktopCapabilityRequest.ForNotification(new DesktopNotificationRequest("t", "m")), harness.Context(), CancellationToken.None);

        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, response.Error!.Code);
    }

    [Fact]
    public void PolicyTable_IsFrozenSnapshot()
    {
        var actual = DesktopCapabilityPolicy.Snapshot
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}=[{string.Join(",", pair.Value)}]")
            .ToArray();

        string[] expected =
        [
            $"BrowserContexts=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserInteract=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserLocate=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserSnapshot=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserTabs=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserWaitFor=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"ShellClipboard=[{nameof(DesktopContextTrust.Workbench)}]",
            $"ShellDialog=[{nameof(DesktopContextTrust.Workbench)}]",
            $"ShellFilePicker=[{nameof(DesktopContextTrust.Workbench)}]",
            $"ShellNotification=[{nameof(DesktopContextTrust.Untrusted)},{nameof(DesktopContextTrust.AgentAuthorized)},{nameof(DesktopContextTrust.Workbench)}]",
            $"ShellStatus=[{nameof(DesktopContextTrust.Untrusted)},{nameof(DesktopContextTrust.AgentAuthorized)},{nameof(DesktopContextTrust.Workbench)}]",
            $"WebViewExecuteJavascript=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"WebViewNavigate=[{nameof(DesktopContextTrust.Untrusted)},{nameof(DesktopContextTrust.AgentAuthorized)},{nameof(DesktopContextTrust.Workbench)}]",
            $"WebViewPageState=[{nameof(DesktopContextTrust.Untrusted)},{nameof(DesktopContextTrust.AgentAuthorized)},{nameof(DesktopContextTrust.Workbench)}]",
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PolicyTable_CoversEveryRegisteredCapability()
    {
        var covered = DesktopCapabilityPolicy.Snapshot.Keys.OrderBy(capability => capability).ToArray();
        var registered = DesktopCapabilities.All.Select(descriptor => descriptor.Capability).OrderBy(c => c).ToArray();

        Assert.Equal(registered, covered);
    }

    [Fact]
    public void PolicyTable_NeverGrantsTrustedContextCapabilitiesToUntrustedTargets()
    {
        var violations = DesktopCapabilities.All
            .Where(descriptor => descriptor.Traits.HasFlag(DesktopCapabilityTraits.RequiresTrustedContext))
            .Where(descriptor => DesktopCapabilityPolicy.IsAllowedForTrust(
                descriptor.Capability, DesktopContextTrust.Untrusted))
            .Select(descriptor => descriptor.Name)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PolicyTable_NeverAllowsWorkbenchScriptInjection()
    {
        Assert.False(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.WebViewExecuteJavascript, DesktopContextTrust.Workbench));
        Assert.True(DesktopCapabilityPolicy.DeniesWorkbenchScriptInjection(
            DesktopCapability.WebViewExecuteJavascript, DesktopContextTrust.Workbench));
    }

    [Fact]
    public void RequiresPageTarget_MatchesContractTraits()
    {
        Assert.True(DesktopCapabilityPolicy.RequiresPageTarget(DesktopCapability.WebViewNavigate));
        Assert.True(DesktopCapabilityPolicy.RequiresPageTarget(DesktopCapability.WebViewExecuteJavascript));
        Assert.True(DesktopCapabilityPolicy.RequiresPageTarget(DesktopCapability.WebViewPageState));
        Assert.False(DesktopCapabilityPolicy.RequiresPageTarget(DesktopCapability.ShellNotification));
    }

    [Fact]
    public void Options_RejectEmptyCapabilitySet()
    {
        Assert.Throws<ArgumentException>(() => new DesktopService(
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            new DesktopTargetRegistry(),
            new DesktopServiceOptions { AllowedCapabilities = DesktopCapability.None }));
    }
}

public sealed class TargetRegistryTests
{
    [Fact]
    public void RegisterPage_RequiresItsContextFirst()
    {
        var registry = new DesktopTargetRegistry();

        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterPage(new DesktopPageTarget("ctx-1", "page-1")));

        registry.RegisterContext("ctx-1", DesktopContextTrust.Untrusted);
        registry.RegisterPage(new DesktopPageTarget("ctx-1", "page-1"));

        Assert.Equal(1, registry.OpenPageCount);
    }

    [Fact]
    public void RegisterContext_RejectsBlankId()
    {
        var registry = new DesktopTargetRegistry();
        Assert.Throws<ArgumentException>(() => registry.RegisterContext("  ", DesktopContextTrust.Untrusted));
    }

    [Fact]
    public void UpdatePage_RejectsVersionRegression()
    {
        var registry = new DesktopTargetRegistry();
        var target = new DesktopPageTarget("ctx-1", "page-1");
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(target, DesktopPageVersion.Require(5));

        Assert.True(registry.UpdatePage(target, DesktopPageVersion.Require(6)));
        Assert.Equal(6, registry.Resolve(target)!.Version.Value);

        // 版本回退会让旧 Snapshot/Locator 重新"有效"，必须拒绝。
        Assert.False(registry.UpdatePage(target, DesktopPageVersion.Require(3)));
        Assert.Equal(6, registry.Resolve(target)!.Version.Value);
    }

    [Fact]
    public void UpdatePage_OnClosedPage_ReturnsFalse()
    {
        var registry = new DesktopTargetRegistry();
        var target = new DesktopPageTarget("ctx-1", "page-1");
        registry.RegisterContext("ctx-1", DesktopContextTrust.Untrusted);
        registry.RegisterPage(target);
        registry.ClosePage(target);

        Assert.False(registry.UpdatePage(target, DesktopPageVersion.Require(2)));
        Assert.Null(registry.Resolve(target));
    }

    [Fact]
    public void CloseContext_RemovesItsPagesAndTheContext()
    {
        var registry = new DesktopTargetRegistry();
        registry.RegisterContext("ctx-1", DesktopContextTrust.Untrusted);
        registry.RegisterPage(new DesktopPageTarget("ctx-1", "page-1"));
        registry.RegisterPage(new DesktopPageTarget("ctx-1", "page-2"));
        registry.RegisterContext("ctx-2", DesktopContextTrust.Workbench);
        registry.RegisterPage(new DesktopPageTarget("ctx-2", "page-1"));

        Assert.Equal(2, registry.CloseContext("ctx-1"));
        Assert.Equal(1, registry.OpenPageCount);
        Assert.Equal(1, registry.ContextCount);
    }
}
