namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// **I2 会话准入的源码不变量守卫**（与 R4 的撤销守卫同一手法）：
/// Bridge 派发器只能有一个准入来源（会话权威），不得悄悄退回旧的页级 grant 闸门。
///
/// 破坏方式（每条都会让本测试变红）：
/// · 在派发器里恢复 `HasSufficientPageGrant` / `page_grant_required`；
/// · 删掉「首条可信命令确认归属 / 触碰前复检 / finally 精确释放」中的任一步；
/// · 把 owner 从宿主注入的 Origin 改为其它来源；
/// · 把 `PuddingBrowser.Authorization` 从生命周期组件的引用里移除。
/// </summary>
public sealed class BrowserSessionAdmissionSourceGuardTests
{
    [Fact]
    public void BridgeDispatcher_HasExactlyOneSessionAdmissionPath()
    {
        var dispatcher = ReadRepositoryFile(
            "Source", "PuddingDesktop.WpfArchive", "Browser", "BrowserBridgeCommandDispatcher.cs");

        // 旧闸门必须彻底消失：恢复它就等于"两条准入路径同时裁决"。
        Assert.DoesNotContain("HasSufficientPageGrant", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("page_grant_required", dispatcher, StringComparison.Ordinal);

        // 单一路径的四个必要步骤。
        Assert.Contains("AuthorizeSessionCommand", dispatcher, StringComparison.Ordinal);
        Assert.Contains("TryClaimControlOwner", dispatcher, StringComparison.Ordinal);
        Assert.Contains("AuthorizePageCommand", dispatcher, StringComparison.Ordinal);
        Assert.Contains("RevalidatePageCommand", dispatcher, StringComparison.Ordinal);
        Assert.Contains("CompletePageCommand", dispatcher, StringComparison.Ordinal);

        // owner 必须来自宿主注入的 Origin（模型不可自报）。
        Assert.Contains("command.Origin?.ConversationId", dispatcher, StringComparison.Ordinal);
        Assert.Contains("command.Origin?.SessionId", dispatcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_OwnsTheSessionAuthorityAndExposesOnlyScalarFacade()
    {
        var lifecycle = ReadRepositoryFile(
            "Source", "Pudding.DesktopService", "BrowserPageLifecycle.cs");

        Assert.Contains("AgentTargetSessionAuthority", lifecycle, StringComparison.Ordinal);
        Assert.Contains("OfferTarget", lifecycle, StringComparison.Ordinal);
        Assert.Contains("TryClaimOwner", lifecycle, StringComparison.Ordinal);
        Assert.Contains("_sessions = new()", lifecycle, StringComparison.Ordinal);

        // 门面类型必须自己声明（Shell/桥侧不引用 Authorization 组件）。
        var facade = ReadRepositoryFile(
            "Source", "Pudding.DesktopService", "BrowserSessionAdmission.cs");
        Assert.Contains("BrowserSessionControlHandle", facade, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionPermit", facade, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionErrorCodes", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentTargetSessionAuthority", facade, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopService_ReferencesTheAuthorizationLeaf()
    {
        var csproj = ReadRepositoryFile("Source", "Pudding.DesktopService", "Pudding.DesktopService.csproj");
        Assert.Contains("PuddingBrowser.Authorization", csproj, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PuddingAgentNetwork.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(new[] { root.FullName }.Concat(relativeSegments).ToArray());
        Assert.True(File.Exists(path), $"expected repository file not found: {path}");
        return File.ReadAllText(path);
    }
}
