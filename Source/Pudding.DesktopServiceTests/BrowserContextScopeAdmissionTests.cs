using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using DesktopServiceTests;

namespace Pudding.DesktopServiceTests;

/// <summary>
/// 外部审查（2026-10-06）P1-1 的回归：**无页面目标**的浏览器能力不得继承 Shell 的调用方信任。
///
/// <para>
/// 缺陷形态：产品缺省装配把 <c>ShellCallerTrust</c> 设为 <c>Untrusted</c>（fail closed，用于对话框/Picker/剪贴板），
/// 而 <c>browser.contexts</c> / <c>browser.context.create</c> / <c>browser.context.close</c> 是**无页面目标**的能力
/// ⇒ 准入拿不到 <c>pageState.Trust</c>，于是沿用 Shell 信任被默认拒绝：通道握手成功，但
/// <c>browser_context</c> 的 list/create/close 全部 Unauthorized（工具省略 contextId 时先列上下文的路径同样失败）。
/// </para>
/// <para>
/// 本文件用**产品缺省装配**（而不是测试自己放宽过的选项）钉住修复后的语义。
/// </para>
/// </summary>
public sealed class BrowserContextScopeAdmissionTests
{
    [Fact]
    public void DefaultProductOptions_SeparateTheTwoCallerTrustSources()
    {
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();

        // 浏览器上下文作用域走自己那一份（Agent 工具层发起）。
        Assert.Equal(DesktopContextTrust.AgentAuthorized, options.BrowserContextCallerTrust);

        // Shell 面保持 fail closed：本次修复**不**放宽对话框/Picker/剪贴板。
        Assert.Equal(DesktopContextTrust.Untrusted, options.ShellCallerTrust);
    }

    [Theory]
    [InlineData(DesktopCapability.BrowserContexts, true)]
    [InlineData(DesktopCapability.BrowserContextCreate, true)]
    [InlineData(DesktopCapability.BrowserContextClose, true)]
    [InlineData(DesktopCapability.ShellDialog, false)]
    [InlineData(DesktopCapability.ShellClipboard, false)]
    [InlineData(DesktopCapability.ShellFilePicker, false)]
    [InlineData(DesktopCapability.ShellNotification, false)]
    [InlineData(DesktopCapability.ShellStatus, false)]
    public void BrowserContextScope_HasASingleDefinition(DesktopCapability capability, bool expected)
    {
        // "哪些能力属于浏览器上下文作用域"必须是**单一定义**，否则策略表/准入/测试三处会各自漂移。
        Assert.Equal(expected, DesktopCapabilityPolicy.IsBrowserContextScoped(capability));
    }

    [Fact]
    public async Task DefaultProductOptions_BrowserContexts_PassTheTrustGate_WhileShellDialogStillDoesNot()
    {
        // **产品缺省装配**（不是显式放宽的测试选项）。
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();
        var harness = ServiceHarness.Create(hasThreadAccess: true, options: options);

        var contexts = await harness.ExecuteAsync(
            DesktopCapability.BrowserContexts, DesktopCapabilityRequest.ForContexts());

        // 关注点是**信任门禁**：不该再是 Unauthorized（替身表面自己的成功/失败不在本用例口径内）。
        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, contexts.Error?.Code);

        // 同一份缺省装配下，Shell 对话框仍然被默认拒绝（本次修复没有顺带放宽别的面）。
        Assert.False(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.ShellDialog, options.ShellCallerTrust));
    }

    [Fact]
    public async Task ExplicitShellTrustOverride_StillAppliesToShellCapabilitiesOnly()
    {
        // 显式把 Shell 信任放宽到 Workbench：对话框面随之开放，但**不影响**浏览器上下文那份来源。
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions(
            shellCallerTrust: DesktopContextTrust.Workbench);
        var harness = ServiceHarness.Create(hasThreadAccess: true, options: options);

        Assert.Equal(DesktopContextTrust.Workbench, options.ShellCallerTrust);
        Assert.Equal(DesktopContextTrust.AgentAuthorized, options.BrowserContextCallerTrust);
        Assert.True(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.ShellDialog, options.ShellCallerTrust));

        var contexts = await harness.ExecuteAsync(
            DesktopCapability.BrowserContexts, DesktopCapabilityRequest.ForContexts());
        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, contexts.Error?.Code);
    }
}
