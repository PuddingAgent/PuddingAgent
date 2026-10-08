using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Automation;

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

/// <summary>
/// 变更类能力的**后置条件**（2026-10-08 修正，设计 §1.1/§4.2）：
/// 不再要求「所有 mutating 结果版本必须严格递增」——那只对**确实提交新文档**的动作成立。
/// fill 只改 value、click 可能只开菜单或触发 SPA 更新、tab activate 只切焦点，
/// 这些动作成功却不会推进版本；旧规则把它们判成 internal_error（实测假失败）。
/// 现在的判据：必须有活版本 + 只有调用方显式声明的后置条件未满足才失败。
/// </summary>
public sealed class MutationInvariantTests
{
    private const DesktopCapability Allowed =
        DesktopCapability.BrowserInteract | DesktopCapability.WebViewNavigate | DesktopCapability.BrowserTabs;

    [Fact]
    public async Task Interaction_ThatDoesNotAdvanceTheVersion_IsAcceptedWithTheObservedVersion()
    {
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        // surface 回带与请求相同的版本（fill 改 value / click 只开菜单的真实形态）。
        harness.Surface.InteractHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(
                request.Target,
                new DesktopPageState(request.Target, null, request.ExpectedPageVersion, DesktopPageReadiness.Complete))));

        var response = await harness.ExecuteAsync(
            DesktopCapability.BrowserInteract,
            DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
                ServiceHarness.AgentPage, DesktopInteractionAction.Click, DesktopPageVersion.Require(1),
                new DesktopLocator(DesktopLocatorKind.Css, "button"))));

        Assert.False(response.IsFailure);
        // 如实回带**观测到的**事实：不伪造推进，也不假失败。
        Assert.Equal(1, response.Interact!.Page.Version.Value);
    }

    [Fact]
    public async Task Interaction_WithoutALiveVersion_IsStillRejected()
    {
        // 引用必须带活版本：没有活版本时旧引用无法作废、新引用无法建立 ⇒ 这仍是真失败。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.InteractHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(
                request.Target,
                new DesktopPageState(request.Target, null, DesktopPageVersion.Unknown, DesktopPageReadiness.Complete))));

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
        // 只读能力根本不走变更类后置条件（它们本来就可能回带与请求相同的版本）。
        Assert.Null(BrowserMutationPostcondition.Validate(new BrowserMutationOutcome(
            BrowserAutomationOperation.Snapshot, DesktopPageVersion.Require(5), DesktopPageVersion.Require(2))));

        // 变更类：没有活版本 ⇒ 真失败（引用无法建立）。
        Assert.NotNull(BrowserMutationPostcondition.Validate(new BrowserMutationOutcome(
            BrowserAutomationOperation.Interact, DesktopPageVersion.Require(5), DesktopPageVersion.Unknown)));

        // 声明了「应提交新文档」却没观测到提交 ⇒ 结果不明，**不是**可安全重试的失败。
        var declared = BrowserMutationPostcondition.Validate(new BrowserMutationOutcome(
            BrowserAutomationOperation.Navigate, DesktopPageVersion.Require(5), DesktopPageVersion.Require(5),
            BrowserMutationExpectation.DocumentNavigation));
        Assert.NotNull(declared);
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, declared!.Code);
    }

    [Fact]
    public async Task TabClose_IsJudgedByTheClosedFact_NotByTheVersion()
    {
        // 关闭非最后一页不会推进版本；旧规则在这里假失败。现在证据是「确实关掉了」。
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

        Assert.False(response.IsFailure);
        Assert.True(response.Tabs!.TabClosed);
        Assert.Equal(1, response.Tabs.Page.Version.Value);
    }

    [Fact]
    public async Task TabClose_ThatReportsNoClosedTab_IsRejected()
    {
        // 关闭动作**没关掉**才是真失败（拿「另一页的版本推进」当证据是错的）。
        var harness = ServiceHarness.Create(allowed: Allowed, hasThreadAccess: true);
        harness.Surface.TabsHandler = (request, _) => Task.FromResult(
            CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
                request.Target,
                request.Action,
                new DesktopPageState(request.Target, null, DesktopPageVersion.Require(1), DesktopPageReadiness.Complete),
                tabClosed: false,
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

/// <summary>交互脚本生成与解析（平台无关；fail closed）。</summary>
public sealed class DesktopInteractScriptTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");
    private static readonly DesktopLocator Button = new(DesktopLocatorKind.Css, "button");

    [Fact]
    public void InteractScript_CarriesTheActionAndParameters()
    {
        var script = DesktopDomScripts.BuildInteractScript(new BrowserInteractRequest(
            Target, DesktopInteractionAction.Fill, DesktopPageVersion.Require(3), Button, text: "你好\"x\""));

        Assert.Contains("const action = \"fill\"", script, StringComparison.Ordinal);
        Assert.Contains("const selector = \"button\"", script, StringComparison.Ordinal);
        Assert.Contains("\\\"x\\\"", script, StringComparison.Ordinal);   // 文案被 JSON 转义
        Assert.Contains("dispatchEvent", script, StringComparison.Ordinal);

        // scroll 无定位：作用于页面本身。
        var scroll = DesktopDomScripts.BuildInteractScript(new BrowserInteractRequest(
            Target, DesktopInteractionAction.Scroll, DesktopPageVersion.Require(3), deltaY: -200));
        Assert.Contains("const selector = \"body\"", scroll, StringComparison.Ordinal);
        Assert.Contains("const deltaY = -200", scroll, StringComparison.Ordinal);
        Assert.Contains("const checked = null", script, StringComparison.Ordinal);  // 未指定 ⇒ null，不是 false
    }

    [Fact]
    public void ParseInteract_ReadsTheElementWithThePageVersionStamped()
    {
        var json = """{"ok":true,"error":null,"element":{"ref":"e1","tag":"button","role":"button","name":"提交","text":"提交","visible":true,"enabled":true,"checked":false}}""";

        var parsed = DesktopDomScripts.ParseInteract(json, DesktopPageVersion.Require(7));

        Assert.NotNull(parsed);
        Assert.True(parsed!.Succeeded);
        Assert.Null(parsed.Error);
        Assert.Equal(7, parsed.Element!.PageVersion.Value);
        Assert.False(parsed.Element.IsChecked);   // false ≠ 未知
    }

    [Fact]
    public void ParseInteract_ReadsFailuresWithoutInventingAnElement()
    {
        var parsed = DesktopDomScripts.ParseInteract(
            """{"ok":false,"error":"element not found","element":null}""", DesktopPageVersion.Require(1));

        Assert.NotNull(parsed);
        Assert.False(parsed!.Succeeded);
        Assert.Equal("element not found", parsed.Error);
        Assert.Null(parsed.Element);
    }

    [Fact]
    public void ParseInteract_FailsClosedOnMalformedInput()
    {
        Assert.Null(DesktopDomScripts.ParseInteract(null, DesktopPageVersion.Require(1)));
        Assert.Null(DesktopDomScripts.ParseInteract("nope", DesktopPageVersion.Require(1)));
        Assert.Null(DesktopDomScripts.ParseInteract("""{"error":"x"}""", DesktopPageVersion.Require(1)));
        // 元素结构坏了 ⇒ 整体作废，不把坏引用交给上层。
        Assert.Null(DesktopDomScripts.ParseInteract("""{"ok":true,"element":{"tag":"button"}}""", DesktopPageVersion.Require(1)));
        // 没有有效版本 ⇒ 引用失去版本依据。
        Assert.Null(DesktopDomScripts.ParseInteract("""{"ok":true,"element":null}""", DesktopPageVersion.Unknown));
    }
}

/// <summary>等待条件脚本：只回答「此刻是否满足」，无法判定返回 null（≠ 未满足）。</summary>
public sealed class DesktopWaitScriptTests
{
    [Fact]
    public void WaitScript_MapsEveryConditionKind()
    {
        var selector = DesktopDomScripts.BuildWaitScript(new DesktopWaitCondition(DesktopWaitConditionKind.Selector, "#ready"));
        Assert.Contains("const kind = \"selector\"", selector, StringComparison.Ordinal);
        Assert.Contains("document.querySelector(value) !== null", selector, StringComparison.Ordinal);

        var hidden = DesktopDomScripts.BuildWaitScript(new DesktopWaitCondition(DesktopWaitConditionKind.SelectorHidden, ".loading"));
        Assert.Contains("selector-hidden", hidden, StringComparison.Ordinal);
        Assert.Contains("=== null", hidden, StringComparison.Ordinal);

        var url = DesktopDomScripts.BuildWaitScript(new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, "/done"));
        Assert.Contains("location.href.indexOf", url, StringComparison.Ordinal);

        // 未登记条件必须显式返回「无法判定」，而不是默默当成未满足。
        Assert.Contains("satisfied: null", selector.Replace("case 'selector': satisfied = document.querySelector(value) !== null; break;", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseWait_DistinguishesNotYetFromCannotTell()
    {
        Assert.True(DesktopDomScripts.ParseWait("""{"satisfied":true}"""));
        Assert.False(DesktopDomScripts.ParseWait("""{"satisfied":false}"""));

        // 「无法判定」与「未满足」是两件事：前者应让调用方明确处理。
        Assert.Null(DesktopDomScripts.ParseWait("""{"satisfied":null}"""));
        Assert.Null(DesktopDomScripts.ParseWait("""{}"""));
        Assert.Null(DesktopDomScripts.ParseWait("nope"));
        Assert.Null(DesktopDomScripts.ParseWait(null));
    }
}
}
