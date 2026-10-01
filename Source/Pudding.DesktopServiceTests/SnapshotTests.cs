using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>`browser.snapshot`（切片 D 首个能力）：准入、版本校验与结果传递。</summary>
public sealed class SnapshotTests
{
    private const DesktopCapability Allowed =
        DesktopCapability.BrowserSnapshot | DesktopCapability.WebViewNavigate | DesktopCapability.WebViewPageState;

    [Fact]
    public async Task Snapshot_OnAgentAuthorizedPage_ReachesTheSurfaceWithTheBudget()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        var request = DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(
            ServiceHarness.AgentPage,
            DesktopPageVersion.Require(1),
            new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: false, maxNodes: 250)));

        var response = await harness.ExecuteAsync(DesktopCapability.BrowserSnapshot, request);

        Assert.False(response.IsFailure);
        Assert.Equal(42, response.Snapshot!.NodeCount);
        Assert.Equal(ServiceHarness.AgentPage, response.Snapshot.Target);
        Assert.Equal(1, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task Snapshot_IsDeniedOnUntrustedPages()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        var request = DesktopCapabilityRequest.ForSnapshot(
            new BrowserSnapshotRequest(ServiceHarness.WebPage, DesktopPageVersion.Require(5)));

        var response = await harness.ExecuteAsync(DesktopCapability.BrowserSnapshot, request);

        // 快照会遍历 DOM（等同注入脚本）：普通网页一律拒绝。
        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, response.Error!.Code);
        Assert.Equal(0, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task Snapshot_RequiresItsPayloadAndAValidBudget()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        var mismatched = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot, harness.NavigateRequest(ServiceHarness.AgentPage));
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, mismatched.Error!.Code);

        // 预算越界在契约层就被拒绝（fail closed，不做无界调用）。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopSnapshotOptions(maxNodes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopSnapshotOptions(maxTextLength: DesktopSnapshotOptions.MaxMaxTextLength + 1));
        Assert.False(new DesktopSnapshotOptions(
            includeDom: false, includeAccessibilityTree: false, includeHtml: false).HasContent);

        Assert.Equal(0, harness.Surface.SnapshotCount);
    }

    [Fact]
    public async Task Snapshot_PropagatesPageVersionMismatchFromTheSurface()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.SnapshotHandler = (_, _) => Task.FromResult(
            CapabilityResult<DesktopSnapshot>.Failure(
                new DesktopCapabilityError(
                    DesktopCapabilityErrorCode.PageVersionMismatch, "stale page version", retryable: true)));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(
                ServiceHarness.AgentPage, DesktopPageVersion.Require(99))));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, response.Error!.Code);
    }

    [Fact]
    public async Task Budgets_AreEnforcedByTheServiceEvenIfTheSurfaceIgnoresThem()
    {
        // 咽喉点强制：surface 可以「不守规矩」，但服务返回给调用方的结果必须已按预算截断。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.SnapshotHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopSnapshot>.Success(new DesktopSnapshot(
                request.Target,
                new string('d', 5000),
                null,
                null,
                truncated: false,
                nodeCount: 99,
                DesktopPageVersion.Require(1))));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(new BrowserSnapshotRequest(
                ServiceHarness.AgentPage,
                DesktopPageVersion.Require(1),
                new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: false, maxTextLength: 64))));

        Assert.False(response.IsFailure);
        Assert.Equal(64, response.Snapshot!.DomText!.Length);
        Assert.True(response.Snapshot.Truncated);
        // 预算不改变观测事实（节点数仍如实回传）。
        Assert.Equal(99, response.Snapshot.NodeCount);
    }
}

/// <summary>变更类能力的结果不变量：返回版本必须严格推进，否则折成 internal_error（fail closed）。</summary>
public sealed class MutationInvariantTests
{
    private const DesktopCapability Allowed =
        DesktopCapability.BrowserInteract | DesktopCapability.WebViewNavigate | DesktopCapability.BrowserTabs;

    [Fact]
    public async Task Mutation_ThatDoesNotAdvanceTheVersion_IsRejectedAsInternalError()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        // 不守规矩的 surface：固定交互 v5，却回带 v5（版本没推进）。
        harness.Surface.InteractHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(
                request.Target,
                new DesktopPageState(request.Target, null, request.ExpectedPageVersion, DesktopPageReadiness.Complete))));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserInteract,
            DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                ServiceHarness.AgentPage, DesktopInteractionAction.Click, DesktopPageVersion.Require(1),
                new DesktopLocator(DesktopLocatorKind.Css, "button"))));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, response.Error!.Code);
    }

    [Fact]
    public async Task Mutation_ThatAdvancesTheVersion_Passes()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserInteract,
            DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                ServiceHarness.AgentPage, DesktopInteractionAction.Click, DesktopPageVersion.Require(1),
                new DesktopLocator(DesktopLocatorKind.Css, "button"))));

        // 夹具返回 v2（= 请求 + 1）。
        Assert.False(response.IsFailure);
        Assert.Equal(2, response.Interact!.Page.Version.Value);
    }

    [Fact]
    public void ReadOnlyCapabilities_AreExemptFromTheAdvanceRule()
    {
        // 只读能力本来就可能回带与请求相同的版本（例如 page_state 观测当前版本）。
        Assert.Null(DesktopMutationInvariants.RequireVersionAdvanced(
            DesktopCapability.WebViewPageState, DesktopPageVersion.Require(5), DesktopPageVersion.Require(5)));
        Assert.Null(DesktopMutationInvariants.RequireVersionAdvanced(
            DesktopCapability.BrowserLocate, DesktopPageVersion.Require(9), DesktopPageVersion.Require(2)));

        // 变更类：同版本或不带版本都不通过。
        Assert.NotNull(DesktopMutationInvariants.RequireVersionAdvanced(
            DesktopCapability.BrowserTabs, DesktopPageVersion.Require(5), DesktopPageVersion.Require(5)));
        Assert.NotNull(DesktopMutationInvariants.RequireVersionAdvanced(
            DesktopCapability.WebViewNavigate, DesktopPageVersion.Require(5), DesktopPageVersion.Unknown));
    }

    [Fact]
    public async Task TabClose_ThatDoesNotAdvanceTheVersion_IsRejected()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.TabsHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
                request.Target,
                request.Action,
                new DesktopPageState(request.Target, null, DesktopPageVersion.Require(1), DesktopPageReadiness.Complete),
                tabClosed: true,
                new DesktopContexts([]))));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserTabs,
            DesktopCapabilityRequest.ForTabs(new BrowserTabsRequest(
                ServiceHarness.AgentPage, DesktopTabAction.Close, DesktopPageVersion.Require(1))));

        Assert.True(response.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, response.Error!.Code);
    }

/// <summary>DOM 脚本生成与解析：平台无关部分（可脱离 WebView2 测试），解析 fail closed。</summary>
public sealed class DesktopDomScriptsTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    [Fact]
    public void SnapshotScript_EmbedsTheBudgetAndRequestedFields()
    {
        var script = DesktopDomScripts.BuildSnapshotScript(
            new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: false, maxNodes: 42, maxTextLength: 777));

        Assert.Contains("const maxNodes = 42", script, StringComparison.Ordinal);
        Assert.Contains("const maxText = 777", script, StringComparison.Ordinal);
        Assert.Contains("let domText = true", script, StringComparison.Ordinal);
        Assert.Contains("const a11y = false", script, StringComparison.Ordinal);
        Assert.Contains("JSON.stringify", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LocateScript_UsesTheLocatorStrategyAndIsJsonEscaped()
    {
        var escaped = DesktopDomScripts.BuildLocateScript(
            new DesktopLocator(DesktopLocatorKind.TestId, "quote\"and\\slash"), maxResults: 5);

        Assert.Contains("const max = 5", escaped, StringComparison.Ordinal);
        Assert.Contains("\\\"", escaped, StringComparison.Ordinal);
        Assert.Contains("data-testid", escaped, StringComparison.Ordinal);

        Assert.Equal("[data-testid=\"x\"]", DesktopDomScripts.SelectorFor(new DesktopLocator(DesktopLocatorKind.TestId, "x")));
        Assert.Equal("*", DesktopDomScripts.SelectorFor(new DesktopLocator(DesktopLocatorKind.Ref, "e1")));
    }

    [Fact]
    public void ParseSnapshot_ReadsTheScriptShapeAndAppliesTheBudget()
    {
        var options = new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: false, maxTextLength: 10);
        var json = """{"nodeCount":123,"truncated":false,"domText":"0123456789ABCDEF","accessibilityTree":"tree"}""";

        var snapshot = DesktopDomScripts.ParseSnapshot(json, Target, DesktopPageVersion.Require(4), options);

        Assert.NotNull(snapshot);
        Assert.Equal(123, snapshot!.NodeCount);
        Assert.Equal(10, snapshot.DomText!.Length);
        Assert.Null(snapshot.AccessibilityTree);   // 未请求的字段不得回传
        Assert.True(snapshot.Truncated);           // 截断必须如实标注
        Assert.Equal(4, snapshot.PageVersion.Value);
    }

    [Fact]
    public void ParseSnapshot_FailsClosedOnMalformedInput()
    {
        var options = new DesktopSnapshotOptions();

        Assert.Null(DesktopDomScripts.ParseSnapshot(null, Target, DesktopPageVersion.Require(1), options));
        Assert.Null(DesktopDomScripts.ParseSnapshot("not json", Target, DesktopPageVersion.Require(1), options));
        Assert.Null(DesktopDomScripts.ParseSnapshot("[1,2,3]", Target, DesktopPageVersion.Require(1), options));
        Assert.Null(DesktopDomScripts.ParseSnapshot("""{"truncated":true}""", Target, DesktopPageVersion.Require(1), options));

        // 没有有效 PageVersion 的观测一律作废（引用会失去版本依据）。
        Assert.Null(DesktopDomScripts.ParseSnapshot("""{"nodeCount":1}""", Target, DesktopPageVersion.Unknown, options));
    }

    [Fact]
    public void ParseLocate_ReadsElementsWithThePageVersionStamped()
    {
        var request = new BrowserLocateRequest(
            Target, new DesktopLocator(DesktopLocatorKind.Css, "button"), DesktopPageVersion.Unknown, maxResults: 2);
        var json = """
            {"truncated":false,"elements":[
              {"ref":"e1","tag":"button","role":"button","name":"提交","text":"提交","visible":true,"enabled":true,"checked":true},
              {"ref":"e2","tag":"input","checked":null},
              {"ref":"e3","tag":"div"}
            ]}
            """;

        var located = DesktopDomScripts.ParseLocate(json, request, DesktopPageVersion.Require(6));

        Assert.NotNull(located);
        Assert.Equal(2, located!.Elements.Count);          // maxResults 生效
        Assert.True(located.Truncated);                    // 被裁剪必须标注
        Assert.Equal(6, located.Elements[0].PageVersion.Value);
        Assert.True(located.Elements[0].IsChecked);
        Assert.Null(located.Elements[1].IsChecked);        // null 与 false 必须区分
        Assert.Equal(request.Locator, located.Locator);
    }

    [Fact]
    public void ParseLocate_FailsClosedOnMalformedInput()
    {
        var request = new BrowserLocateRequest(Target, new DesktopLocator(DesktopLocatorKind.Css, "button"));

        Assert.Null(DesktopDomScripts.ParseLocate(null, request, DesktopPageVersion.Require(1)));
        Assert.Null(DesktopDomScripts.ParseLocate("""{"elements":"nope"}""", request, DesktopPageVersion.Require(1)));
        Assert.Null(DesktopDomScripts.ParseLocate("""{"elements":[{"tag":"div"}]}""", request, DesktopPageVersion.Require(1)));
        Assert.Null(DesktopDomScripts.ParseLocate("""{"elements":[{"ref":"e1"}]}""", request, DesktopPageVersion.Require(1)));
    }
}
}
