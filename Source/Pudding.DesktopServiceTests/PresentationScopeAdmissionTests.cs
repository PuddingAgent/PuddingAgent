using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using DesktopServiceTests;

namespace Pudding.DesktopServiceTests;

/// <summary>
/// 「展示类」能力的准入回归：<c>shell.document_preview</c> / <c>shell.panel_render</c>
/// 不得继承 Shell 自身 UI 动作的调用方信任。
///
/// <para>
/// 缺陷形态（2026-10-20 实测）：能力通道握手成功、<c>render_panel</c> 也把内容写进了 Core，
/// 但 Desktop 回的是一条授权失败 ——
/// <c>unauthorized: shell.panel_render is not allowed for Untrusted targets</c>。
/// 原因是这两条能力被归进了 <c>ShellCallerTrust</c>（默认 Untrusted，供对话框/Picker/剪贴板使用），
/// 而它们只可能来自 Core 的 Agent 工具层，不是用户在本机点按钮引发的动作。
/// 这与会话上下文那份 P1-1 缺陷同源：<b>信任来源分错了桶</b>。
/// </para>
/// <para>
/// 本文件用**产品缺省装配**钉住修复后的语义，并同时钉住「没有顺带放宽别的面」。
/// </para>
/// </summary>
public sealed class PresentationScopeAdmissionTests
{
    [Fact]
    public void DefaultProductOptions_SeparateTheThreeCallerTrustSources()
    {
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();

        // 展示类走自己那一份（Agent 工具层发起）。
        Assert.Equal(DesktopContextTrust.AgentAuthorized, options.PresentationCallerTrust);

        // 另外两份不受影响。
        Assert.Equal(DesktopContextTrust.AgentAuthorized, options.BrowserContextCallerTrust);
        Assert.Equal(DesktopContextTrust.Untrusted, options.ShellCallerTrust);
    }

    [Theory]
    [InlineData(DesktopCapability.ShellDocumentPreview, true)]
    [InlineData(DesktopCapability.ShellPanelRender, true)]
    [InlineData(DesktopCapability.ShellDialog, false)]
    [InlineData(DesktopCapability.ShellFilePicker, false)]
    [InlineData(DesktopCapability.ShellClipboard, false)]
    [InlineData(DesktopCapability.ShellNotification, false)]
    [InlineData(DesktopCapability.ShellStatus, false)]
    [InlineData(DesktopCapability.BrowserContexts, false)]
    public void PresentationScope_HasASingleDefinition(DesktopCapability capability, bool expected)
    {
        // 「哪些能力属于展示类」必须是**单一定义**：策略表、准入与测试三处都从这里取，
        // 否则新增展示能力时会有第三份清单慢慢漂移。
        Assert.Equal(expected, DesktopCapabilityPolicy.IsPresentationScoped(capability));
    }

    [Fact]
    public async Task DefaultProductOptions_PanelRender_PassTheTrustGate_WhileClipboardStillDoesNot()
    {
        // 产品缺省装配，不是测试自己放宽过的选项。
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();
        var harness = ServiceHarness.Create(hasThreadAccess: true, options: options);

        var panel = await harness.ExecuteAsync(
            DesktopCapability.ShellPanelRender,
            DesktopCapabilityRequest.ForPanelRender(
                new DesktopPanelRenderRequest("main", "<p>hello</p>")));

        // 关注点是信任门禁：不该再是 Unauthorized（替身表面自己的成败不在本用例口径内）。
        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, panel.Error?.Code);

        // 同一份缺省装配下，剪贴板仍然被默认拒绝：本次修复没有顺带放宽别的面。
        Assert.False(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.ShellClipboard, options.ShellCallerTrust));
    }

    [Fact]
    public async Task DefaultProductOptions_DocumentPreview_PassTheTrustGate()
    {
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();
        var harness = ServiceHarness.Create(hasThreadAccess: true, options: options);

        var document = await harness.ExecuteAsync(
            DesktopCapability.ShellDocumentPreview,
            DesktopCapabilityRequest.ForDocumentPreview(
                new DesktopDocumentPreviewRequest("code_map.md")));

        Assert.NotEqual(DesktopCapabilityErrorCode.Unauthorized, document.Error?.Code);
    }

    [Fact]
    public async Task PresentationTrust_CanStillBeMadeFailClosed()
    {
        // 变异点：把展示类那份信任收回 Untrusted，两条链路必须重新被拒。
        // 这条用例存在的意义是让「默认值写错」能被取红 —— 若两者恒为 AgentAuthorized，
        // 上面的用例即便默认值漂移也照样绿。
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions(
            presentationCallerTrust: DesktopContextTrust.Untrusted);
        var harness = ServiceHarness.Create(hasThreadAccess: true, options: options);

        var panel = await harness.ExecuteAsync(
            DesktopCapability.ShellPanelRender,
            DesktopCapabilityRequest.ForPanelRender(
                new DesktopPanelRenderRequest("main", "<p>x</p>")));

        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, panel.Error?.Code);
    }

    [Fact]
    public void PresentationCapabilities_AreNeverOpenToUntrustedTargets()
    {
        // 收成 Untrusted 不是「放宽」，是 fail closed：普通网页仍不得触发。
        Assert.False(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.ShellPanelRender, DesktopContextTrust.Untrusted));
        Assert.False(DesktopCapabilityPolicy.IsAllowedForTrust(
            DesktopCapability.ShellDocumentPreview, DesktopContextTrust.Untrusted));
    }
}
