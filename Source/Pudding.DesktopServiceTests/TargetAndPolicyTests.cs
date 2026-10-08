using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Automation;

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
            $"BrowserContextClose=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
            $"BrowserContextCreate=[{nameof(DesktopContextTrust.AgentAuthorized)}]",
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
    public void RequiresAgentTarget_AppliesOnlyToAgentAuthorizedOnlyPageCapabilities()
    {
        // 被闸门覆盖：需要页面目标 + **只**对 AgentAuthorized 开放。
        foreach (var gated in new[]
                 {
                     DesktopCapability.WebViewExecuteJavascript,
                     DesktopCapability.BrowserSnapshot,
                     DesktopCapability.BrowserLocate,
                     DesktopCapability.BrowserInteract,
                     DesktopCapability.BrowserWaitFor,
                     DesktopCapability.BrowserTabs,
                 })
        {
            Assert.True(DesktopCapabilityPolicy.RequiresAgentTarget(gated), $"{gated} 应要求 Agent 目标页");
        }

        // 通用浏览能力：对普通网页/工作台也开放，因此**不**收紧到目标页
        // （把导航也收紧属 grant 模型切片，见设计 §3.2 写权限行）。
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.WebViewNavigate));
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.WebViewPageState));

        // 无页面目标的能力没有「哪个页面」可判。
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.BrowserContexts));
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.BrowserContextCreate));
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.BrowserContextClose));
        Assert.False(DesktopCapabilityPolicy.RequiresAgentTarget(DesktopCapability.ShellNotification));
    }

    [Fact]
    public void RequiresAgentTarget_IsDerivedFromTheTrustTable_NotASecondSourceOfTruth()
    {
        // 推导规则：要求页面目标，且对 Untrusted / Workbench 都不开放。
        // 这条断言保证新增能力时不会出现「信任表收紧、目标闸门忘了跟」的漂移。
        foreach (var capability in DesktopCapabilities.All.Select(descriptor => descriptor.Capability))
        {
            var expected = DesktopCapabilityPolicy.RequiresPageTarget(capability)
                && DesktopCapabilityPolicy.IsAllowedForTrust(capability, DesktopContextTrust.AgentAuthorized)
                && !DesktopCapabilityPolicy.IsAllowedForTrust(capability, DesktopContextTrust.Untrusted)
                && !DesktopCapabilityPolicy.IsAllowedForTrust(capability, DesktopContextTrust.Workbench);

            Assert.Equal(expected, DesktopCapabilityPolicy.RequiresAgentTarget(capability));
        }
    }

    [Fact]
    public void Options_RejectEmptyCapabilitySet()
    {
        Assert.Throws<ArgumentException>(() => new DesktopService(
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            new DesktopTargetRegistry(),
            new DesktopServiceOptions { AllowedCapabilities = DesktopCapability.None },
            new BrowserAutomationAuthority(
                new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test"))));
    }

    [Fact]
    public void Authority_IsRequired_FailClosed()
    {
        // 没有权威实例就没有唯一事实源：宁可构造失败，也不要退化成「各自维护标志」。
        Assert.Throws<ArgumentNullException>(() => new DesktopService(
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            new DesktopTargetRegistry(),
            new DesktopServiceOptions { AllowedCapabilities = DesktopCapability.BrowserSnapshot },
            null!));
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
    public void SetAgentTarget_KeepsAtMostOneTargetAndClearsThePrevious()
    {
        var registry = new DesktopTargetRegistry();
        var first = new DesktopPageTarget("ctx-1", "page-1");
        var second = new DesktopPageTarget("ctx-1", "page-2");
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(first);
        registry.RegisterPage(second);

        Assert.True(registry.SetAgentTarget(first.ContextId, first.PageId));
        Assert.True(registry.Resolve(first)!.IsAgentTarget);
        Assert.False(registry.Resolve(second)!.IsAgentTarget);

        Assert.True(registry.SetAgentTarget(second.ContextId, second.PageId));
        Assert.False(registry.Resolve(first)!.IsAgentTarget);
        Assert.True(registry.Resolve(second)!.IsAgentTarget);

        // 撤销必须真的发生：否则注册表会继续替旧页面背书。
        Assert.False(registry.SetAgentTarget(null, null));
        Assert.False(registry.Resolve(second)!.IsAgentTarget);
    }

    [Fact]
    public void SetAgentTarget_DoesNotInventAPageThatWasNeverRegistered()
    {
        var registry = new DesktopTargetRegistry();
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(new DesktopPageTarget("ctx-1", "page-1"));

        // 不凭一个 id 凭空造出目标（fail closed）。
        Assert.False(registry.SetAgentTarget("ctx-1", "page-unknown"));
        Assert.False(registry.SetAgentTarget("ctx-unknown", "page-1"));
        Assert.False(registry.Resolve(new DesktopPageTarget("ctx-1", "page-1"))!.IsAgentTarget);
    }

    [Fact]
    public void SetAgentTarget_OnAClosedPageMarksNothing()
    {
        var registry = new DesktopTargetRegistry();
        var target = new DesktopPageTarget("ctx-1", "page-1");
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(target, isAgentTarget: true);
        registry.ClosePage(target);

        Assert.False(registry.SetAgentTarget(target.ContextId, target.PageId));
    }

    [Fact]
    public void RegisterPage_CanMarkTheAgentTargetDirectly()
    {
        var registry = new DesktopTargetRegistry();
        var target = new DesktopPageTarget("ctx-1", "page-1");
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(target, isAgentTarget: true);

        Assert.True(registry.Resolve(target)!.IsAgentTarget);
        // 缺省仍是不标记（fail closed）。
        var other = new DesktopPageTarget("ctx-1", "page-2");
        registry.RegisterPage(other);
        Assert.False(registry.Resolve(other)!.IsAgentTarget);
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
