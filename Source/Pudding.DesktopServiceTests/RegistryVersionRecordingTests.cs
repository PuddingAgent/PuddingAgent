using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 「观察到的页面版本必须回写目标注册表」——准入按注册表版本判 <c>page_version_mismatch</c>，
/// 而 Core 钉的版本来自**上一次能力结果**。若结果不回写，注册表会永远停在页面创建时的版本（本夹具为 1），
/// 于是**第一个带版本的操作就被判版本不符**：通道握手成功却什么都做不了。
///
/// 这里逐能力钉住回写（而不是只测一条路径）：本系列已两次踩到「新增分支/结果类型时漏改某个聚合点」
/// 的缺陷（plan §10.2 第 3、4 条）。夹具返回的版本是 9（交互/标签页为 expected+1），
/// 与注册表初值 1 不同，因此这些断言**不是空转**。
/// </summary>
public sealed class RegistryVersionRecordingTests
{
    private const DesktopCapability Allowed =
        DesktopCapability.BrowserSnapshot
        | DesktopCapability.BrowserLocate
        | DesktopCapability.BrowserInteract
        | DesktopCapability.BrowserWaitFor
        | DesktopCapability.BrowserTabs
        | DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewPageState
        | DesktopCapability.WebViewExecuteJavascript;

    private const long FixtureObservedVersion = 9;
    private const long InitialRegisteredVersion = 1;

    [Fact]
    public async Task Snapshot_RecordsTheObservedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(
                new BrowserSnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(InitialRegisteredVersion))),
            FixtureObservedVersion);
    }

    [Fact]
    public async Task Locate_RecordsTheObservedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.BrowserLocate,
            DesktopCapabilityRequest.ForLocate(
                new BrowserLocateRequest(ServiceHarness.AgentPage, new DesktopLocator(DesktopLocatorKind.Css, "a"))),
            FixtureObservedVersion);
    }

    [Fact]
    public async Task PageState_RecordsTheObservedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.WebViewPageState,
            DesktopCapabilityRequest.ForPageState(ServiceHarness.AgentPage),
            FixtureObservedVersion);
    }

    [Fact]
    public async Task Navigate_RecordsTheObservedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.WebViewNavigate,
            DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
                ServiceHarness.AgentPage, new Uri("https://example.com/a"), DesktopPageVersion.Require(InitialRegisteredVersion))),
            FixtureObservedVersion);
    }

    [Fact]
    public async Task WaitFor_RecordsTheObservedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.BrowserWaitFor,
            DesktopCapabilityRequest.ForWaitFor(new BrowserWaitForRequest(
                ServiceHarness.AgentPage, new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, "/done"))),
            FixtureObservedVersion);
    }

    [Fact]
    public async Task Interact_RecordsTheAdvancedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.BrowserInteract,
            DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                ServiceHarness.AgentPage,
                DesktopInteractionAction.Click,
                DesktopPageVersion.Require(InitialRegisteredVersion),
                new DesktopLocator(DesktopLocatorKind.Css, "button"))),
            InitialRegisteredVersion + 1);
    }

    [Fact]
    public async Task Tabs_RecordsTheAdvancedVersion()
    {
        await AssertAdvancesAsync(
            DesktopCapability.BrowserTabs,
            DesktopCapabilityRequest.ForTabs(new BrowserTabsRequest(
                ServiceHarness.AgentPage, DesktopTabAction.Activate, DesktopPageVersion.Require(InitialRegisteredVersion))),
            InitialRegisteredVersion + 1);
    }

    [Fact]
    public async Task ExecuteJavascript_DoesNotInventAVersion()
    {
        // 脚本的返回值里没有页面版本，而按设计脚本**不推进版本**：
        // 既不能凭空推一个，也不能因为"没版本"就把它当失败。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript, harness.JavascriptRequest(ServiceHarness.AgentPage));

        Assert.False(response.IsFailure);
        Assert.Equal(InitialRegisteredVersion, VersionOf(harness, ServiceHarness.AgentPage));
    }

    [Fact]
    public async Task UnregisteredPage_IsNotInventedByASuccessfulLookingResult()
    {
        // 准入先拒（目标未登记），因此根本到不了"回写"这一步；
        // 这里同时钉住"注册表不会多出页面"（凭空造目标等于对一个不存在的页面放行）。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        var ghost = new DesktopPageTarget("ctx-agent", "page-ghost");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewPageState, DesktopCapabilityRequest.ForPageState(ghost));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, response.Error!.Code);
        Assert.Null(harness.Targets.Resolve(ghost));
        Assert.Equal(3, harness.Targets.OpenPageCount);
    }

    [Fact]
    public async Task RecordedVersionMakesTheNextPinnedCallAdmissible()
    {
        // 端到端意义：第一次调用把版本推到夹具版本，第二次**按该版本钉住**必须被准入接受。
        // 没有回写时，第二次会被判 page_version_mismatch —— 这正是本缺陷的产品后果。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(
                new BrowserSnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(InitialRegisteredVersion))));

        var recorded = VersionOf(harness, ServiceHarness.AgentPage);
        var second = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(
                new BrowserSnapshotRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(recorded))));

        Assert.False(second.IsFailure);
    }

    private static async Task AssertAdvancesAsync(
        DesktopCapability capability,
        DesktopCapabilityRequest request,
        long expectedVersion)
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        Assert.Equal(InitialRegisteredVersion, VersionOf(harness, ServiceHarness.AgentPage));

        var response = await harness.ExecuteAsync(capability, request);

        Assert.False(response.IsFailure);
        Assert.Equal(expectedVersion, VersionOf(harness, ServiceHarness.AgentPage));
    }

    private static long VersionOf(ServiceHarness harness, DesktopPageTarget target) =>
        harness.Targets.Resolve(target)!.Version.Value;
}
