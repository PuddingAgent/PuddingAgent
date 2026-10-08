using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 引用身份与三个观察事实（设计方案 §5 / 门禁 G4）。
///
/// 被钉住的缺陷：现有 ref 形如 <c>v{version}-n{seq}</c>，只携带文档版本 ⇒
/// 跨页/跨 frame 但版本号相同会被静默接受；身份还依赖页面可自写的 <c>data-pudding-ref</c>。
/// </summary>
[TestClass]
public sealed class BrowserRefIdentityTests
{
    private static readonly BrowserRefScope TopLevel = new("ctx-1", "page-1");

    private static readonly BrowserRefScope FrameA = new("ctx-1", "page-1", "frame-a");

    private static readonly BrowserRefScope OtherPage = new("ctx-1", "page-2");

    private static readonly BrowserRefScope OtherContext = new("ctx-2", "page-1");

    private static BrowserObservationRevisions Revisions(
        long document = 7,
        long dom = 10,
        long viewport = 3) => new(document, dom, viewport);

    // ── 身份绑定 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Issued_Ref_Is_Valid_On_Its_Own_Scope()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-42");

        var decision = registry.Validate(token.Value, TopLevel, Revisions());

        Assert.IsTrue(decision.IsValid);
        Assert.AreEqual(BrowserRefDenial.None, decision.Denial);
        Assert.AreSame(token, decision.Token);
    }

    [TestMethod]
    public void Ref_Is_Rejected_On_Another_Page_Even_At_The_Same_Document_Generation()
    {
        // 设计点名的情况：「即使两页都恰好处于 v7」也必须拒绝。
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-42");

        var decision = registry.Validate(token.Value, OtherPage, Revisions());

        Assert.IsFalse(decision.IsValid);
        Assert.AreEqual(BrowserRefDenial.PageMismatch, decision.Denial);
    }

    [TestMethod]
    public void Ref_Is_Rejected_In_Another_Context_With_The_Same_Page_Id()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-42");

        var decision = registry.Validate(token.Value, OtherContext, Revisions());

        Assert.IsFalse(decision.IsValid);
        Assert.AreEqual(BrowserRefDenial.PageMismatch, decision.Denial);
    }

    [TestMethod]
    public void Ref_Is_Rejected_Across_Frames()
    {
        var registry = new BrowserRefRegistry();
        var frameToken = registry.Issue(FrameA, Revisions(), "backend-7");

        // frame 内的引用不能在顶层文档上使用，反之亦然。
        Assert.AreEqual(
            BrowserRefDenial.FrameMismatch,
            registry.Validate(frameToken.Value, TopLevel, Revisions()).Denial);

        var topToken = registry.Issue(TopLevel, Revisions(), "backend-8");
        Assert.AreEqual(
            BrowserRefDenial.FrameMismatch,
            registry.Validate(topToken.Value, FrameA, Revisions()).Denial);

        // 另一个 frame 同样拒绝。
        Assert.AreEqual(
            BrowserRefDenial.FrameMismatch,
            registry.Validate(frameToken.Value, new BrowserRefScope("ctx-1", "page-1", "frame-b"), Revisions()).Denial);
    }

    [TestMethod]
    public void Unknown_And_Revoked_Refs_Are_Reported_Separately()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-42");

        Assert.AreEqual(
            BrowserRefDenial.UnknownRef,
            registry.Validate("ref-does-not-exist", TopLevel, Revisions()).Denial);
        Assert.AreEqual(
            BrowserRefDenial.UnknownRef,
            registry.Validate(null, TopLevel, Revisions()).Denial);

        Assert.IsTrue(registry.RevokeRef(token.Value));
        Assert.AreEqual(
            BrowserRefDenial.Revoked,
            registry.Validate(token.Value, TopLevel, Revisions()).Denial,
            "「已作废」必须与「从未签发」分开报，调用方才能给对提示");
    }

    [TestMethod]
    public void Issue_Requires_A_Host_Bound_Node_Key()
    {
        // 没有宿主身份就等于回到「信任页面自写属性」，设计禁止。
        var registry = new BrowserRefRegistry();

        Assert.ThrowsExactly<ArgumentException>(
            () => registry.Issue(TopLevel, Revisions(), " "));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => registry.Issue(null!, Revisions(), "backend-1"));
    }

    [TestMethod]
    public void Ref_Values_Are_Opaque_And_Not_Derivable_From_Role_Or_Name()
    {
        var registry = new BrowserRefRegistry();
        var first = registry.Issue(TopLevel, Revisions(), "backend-1", "button", "Learn more");
        var second = registry.Issue(TopLevel, Revisions(), "backend-2", "button", "Learn more");

        // 同角色同名字的两个节点拿到不同引用，且引用值里不含角色/名字。
        Assert.AreNotEqual(first.Value, second.Value);
        Assert.IsFalse(first.Value.Contains("button", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(first.Value.Contains("Learn", StringComparison.OrdinalIgnoreCase));

        // 删掉第一个节点后，同名的新节点**不会**让旧引用复活（禁止自动重绑）。
        Assert.IsTrue(registry.RevokeRef(first.Value));
        Assert.AreEqual(
            BrowserRefDenial.Revoked,
            registry.Validate(first.Value, TopLevel, Revisions()).Denial);
        Assert.IsTrue(registry.Validate(second.Value, TopLevel, Revisions()).IsValid);
    }

    [TestMethod]
    public void Rejection_Carries_A_Minimal_Refresh_Hint_That_Does_Not_Rebind()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-1", "button", "Learn more");

        var decision = registry.Validate(token.Value, TopLevel, Revisions(document: 8));

        Assert.IsFalse(decision.IsValid);
        Assert.IsNotNull(decision.RefreshHint);
        StringAssert.Contains(decision.RefreshHint!, "re-observe");
        StringAssert.Contains(decision.RefreshHint!, "never re-bound");
        // 提示里可以带 role/name 供人阅读，但不得含「把引用改指到 X」这类重绑语义。
        StringAssert.Contains(decision.RefreshHint!, "Learn more");
    }

    // ── 作废分类 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Document_Commit_Invalidates_Every_Ref_Of_That_Page_Only()
    {
        var registry = new BrowserRefRegistry();
        var top = registry.Issue(TopLevel, Revisions(), "backend-1");
        var frame = registry.Issue(FrameA, Revisions(), "backend-2");
        var other = registry.Issue(OtherPage, Revisions(), "backend-3");

        var revoked = registry.Revoke(BrowserRefInvalidation.DocumentCommitted, TopLevel);

        Assert.AreEqual(2, revoked, "整页作废必须覆盖该页所有 frame");
        Assert.AreEqual(BrowserRefDenial.Revoked, registry.Validate(top.Value, TopLevel, Revisions()).Denial);
        Assert.AreEqual(BrowserRefDenial.Revoked, registry.Validate(frame.Value, FrameA, Revisions()).Denial);
        Assert.IsTrue(registry.Validate(other.Value, OtherPage, Revisions()).IsValid, "别的页面不受影响");
    }

    [TestMethod]
    public void Frame_Navigation_Invalidates_Only_That_Frame()
    {
        var registry = new BrowserRefRegistry();
        var top = registry.Issue(TopLevel, Revisions(), "backend-1");
        var frameA = registry.Issue(FrameA, Revisions(), "backend-2");
        var frameB = registry.Issue(new BrowserRefScope("ctx-1", "page-1", "frame-b"), Revisions(), "backend-3");

        Assert.AreEqual(1, registry.Revoke(BrowserRefInvalidation.FrameNavigated, FrameA));

        Assert.AreEqual(BrowserRefDenial.Revoked, registry.Validate(frameA.Value, FrameA, Revisions()).Denial);
        Assert.IsTrue(registry.Validate(top.Value, TopLevel, Revisions()).IsValid);
        Assert.IsTrue(registry.Validate(frameB.Value, new BrowserRefScope("ctx-1", "page-1", "frame-b"), Revisions()).IsValid);
    }

    [TestMethod]
    public void Page_Close_And_Process_Failure_Invalidate_The_Whole_Page()
    {
        foreach (var invalidation in new[]
                 {
                     BrowserRefInvalidation.PageClosed,
                     BrowserRefInvalidation.BrowserProcessFailed,
                 })
        {
            var registry = new BrowserRefRegistry();
            var token = registry.Issue(TopLevel, Revisions(), "backend-1");

            Assert.AreEqual(1, registry.Revoke(invalidation, TopLevel));
            Assert.AreEqual(BrowserRefDenial.Revoked, registry.Validate(token.Value, TopLevel, Revisions()).Denial);
        }
    }

    [TestMethod]
    public void Ordinary_Dom_Change_And_Viewport_Change_Invalidate_Nothing()
    {
        // 设计 §5：普通 value 改变不使所有节点 ref 失效；滚动只让坐标失效、不动身份。
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-1");

        Assert.AreEqual(0, registry.Revoke(BrowserRefInvalidation.DomChanged, TopLevel));
        Assert.AreEqual(0, registry.Revoke(BrowserRefInvalidation.ViewportChanged, TopLevel));

        Assert.IsTrue(registry.Validate(token.Value, TopLevel, Revisions()).IsValid);
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public void Revoking_A_Whole_Page_Requires_A_Scope()
    {
        var registry = new BrowserRefRegistry();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => registry.Revoke(BrowserRefInvalidation.DocumentCommitted));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => registry.Revoke(BrowserRefInvalidation.FrameNavigated));
    }

    [TestMethod]
    public void Node_Detach_Must_Go_Through_The_Single_Ref_Path()
    {
        var registry = new BrowserRefRegistry();

        Assert.ThrowsExactly<ArgumentException>(
            () => registry.Revoke(BrowserRefInvalidation.NodeDetached, TopLevel));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => registry.Revoke((BrowserRefInvalidation)99, TopLevel));
    }

    [TestMethod]
    public void RevokeRef_On_A_Missing_Ref_Returns_False()
    {
        var registry = new BrowserRefRegistry();

        Assert.IsFalse(registry.RevokeRef("ref-missing"));
        Assert.IsFalse(registry.RevokeRef(null));
    }

    // ── 三个事实与视口 ────────────────────────────────────────────────────

    [TestMethod]
    public void Document_Generation_Change_Rejects_The_Ref()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(document: 7), "backend-1");

        var decision = registry.Validate(token.Value, TopLevel, Revisions(document: 8));

        Assert.IsFalse(decision.IsValid);
        Assert.AreEqual(BrowserRefDenial.DocumentChanged, decision.Denial);
    }

    [TestMethod]
    public void Dom_Revision_Change_Does_Not_Reject_The_Ref()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(dom: 10), "backend-1");

        Assert.IsTrue(registry.Validate(token.Value, TopLevel, Revisions(dom: 99)).IsValid);
    }

    [TestMethod]
    public void Viewport_Change_Keeps_Identity_But_Stales_Coordinates()
    {
        // 节点身份仍有效 ⇒ 点击/填写可以继续；坐标操作必须先重新观察。
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(viewport: 3), "backend-1");

        var identity = registry.Validate(token.Value, TopLevel, Revisions(viewport: 4));
        Assert.IsTrue(identity.IsValid);
        Assert.IsTrue(identity.ViewportStale);

        var coordinates = registry.ValidateForCoordinates(token.Value, TopLevel, Revisions(viewport: 4));
        Assert.IsFalse(coordinates.IsValid);
        Assert.AreEqual(BrowserRefDenial.ViewportMismatch, coordinates.Denial);

        // 重新观察后再取坐标即可。
        var refreshed = registry.Issue(TopLevel, Revisions(viewport: 4), "backend-1");
        Assert.IsTrue(registry.ValidateForCoordinates(refreshed.Value, TopLevel, Revisions(viewport: 4)).IsValid);
    }

    [TestMethod]
    public void Host_Liveness_Check_Rejects_A_Detached_Node()
    {
        // 宿主说节点已断开/被替换 ⇒ 即使作用域与世代都对，也必须拒绝。
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-1");

        var decision = registry.Validate(token.Value, TopLevel, Revisions(), hostReportsAttached: false);

        Assert.IsFalse(decision.IsValid);
        Assert.AreEqual(BrowserRefDenial.NodeDetached, decision.Denial);
    }

    [TestMethod]
    public void Clear_Drops_Everything_Including_Tombstones()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-1");
        registry.RevokeRef(token.Value);

        registry.Clear();

        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(0, registry.RevokedCount);
        Assert.AreEqual(BrowserRefDenial.UnknownRef, registry.Validate(token.Value, TopLevel, Revisions()).Denial);
    }

    [TestMethod]
    public void CountForPage_Is_Per_Page()
    {
        var registry = new BrowserRefRegistry();
        registry.Issue(TopLevel, Revisions(), "backend-1");
        registry.Issue(FrameA, Revisions(), "backend-2");
        registry.Issue(OtherContext, Revisions(), "backend-3");

        Assert.AreEqual(2, registry.CountForPage(TopLevel.PageKey));
        Assert.AreEqual(1, registry.CountForPage(OtherContext.PageKey));
        Assert.AreEqual(3, registry.Count);
    }

    // ── 推进规则 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void ReadOnly_Calls_Advance_Nothing()
    {
        // G4：读操作导致 PageVersion 增长次数 = 0。
        var before = Revisions();

        var after = BrowserRevisionPolicy.AfterReadOnly(before);

        Assert.AreEqual(before, after);
        Assert.AreEqual(before.DocumentGeneration, after.DocumentGeneration);
        Assert.AreEqual(before.DomRevision, after.DomRevision);
        Assert.AreEqual(before.ViewportRevision, after.ViewportRevision);
    }

    [TestMethod]
    public void Document_Commit_Advances_All_Three_Facts()
    {
        var after = BrowserRevisionPolicy.AfterDocumentCommitted(Revisions(7, 10, 3));

        Assert.AreEqual(8, after.DocumentGeneration);
        Assert.AreEqual(11, after.DomRevision);
        Assert.AreEqual(4, after.ViewportRevision);
    }

    [TestMethod]
    public void Dom_Mutation_Advances_Only_The_Dom_Revision()
    {
        var after = BrowserRevisionPolicy.AfterDomMutation(Revisions(7, 10, 3));

        Assert.AreEqual(7, after.DocumentGeneration);
        Assert.AreEqual(11, after.DomRevision);
        Assert.AreEqual(3, after.ViewportRevision);
    }

    [TestMethod]
    public void Viewport_Change_Advances_Only_The_Viewport_Revision()
    {
        var after = BrowserRevisionPolicy.AfterViewportChange(Revisions(7, 10, 3));

        Assert.AreEqual(7, after.DocumentGeneration);
        Assert.AreEqual(10, after.DomRevision);
        Assert.AreEqual(4, after.ViewportRevision);
    }

    [TestMethod]
    public void Revision_Transitions_Compose()
    {
        var revisions = BrowserRevisionPolicy.AfterDomMutation(
            BrowserRevisionPolicy.AfterViewportChange(
                BrowserRevisionPolicy.AfterDocumentCommitted(BrowserObservationRevisions.Initial)));

        Assert.AreEqual(1, revisions.DocumentGeneration);
        Assert.AreEqual(2, revisions.DomRevision);
        Assert.AreEqual(2, revisions.ViewportRevision);
    }

    // ── 作用域与线名 ──────────────────────────────────────────────────────

    [TestMethod]
    public void Scope_Comparisons_Are_Explicit_About_Frames()
    {
        Assert.IsTrue(TopLevel.IsSamePageAs(FrameA));
        Assert.IsFalse(TopLevel.IsSameDocumentAs(FrameA));
        Assert.IsFalse(TopLevel.IsSameDocumentAs(OtherPage));
        Assert.IsTrue(FrameA.IsSameDocumentAs(new BrowserRefScope("ctx-1", "page-1", "frame-a")));
    }

    [TestMethod]
    public void Scope_Requires_Identifiers()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new BrowserRefScope(" ", "page-1"));
        Assert.ThrowsExactly<ArgumentException>(() => new BrowserRefScope("ctx-1", ""));
        Assert.ThrowsExactly<ArgumentException>(() => new BrowserRefScope("ctx-1", "page-1", new string('x', 200)));
    }

    [TestMethod]
    public void Denial_Wire_Names_Are_Stable_And_Total()
    {
        Assert.AreEqual("unknown_ref", BrowserRefDenialWire.NameOf(BrowserRefDenial.UnknownRef));
        Assert.AreEqual("page_mismatch", BrowserRefDenialWire.NameOf(BrowserRefDenial.PageMismatch));
        Assert.AreEqual("frame_mismatch", BrowserRefDenialWire.NameOf(BrowserRefDenial.FrameMismatch));
        Assert.AreEqual("document_changed", BrowserRefDenialWire.NameOf(BrowserRefDenial.DocumentChanged));
        Assert.AreEqual("node_detached", BrowserRefDenialWire.NameOf(BrowserRefDenial.NodeDetached));
        Assert.AreEqual("revoked", BrowserRefDenialWire.NameOf(BrowserRefDenial.Revoked));
        Assert.AreEqual("viewport_mismatch", BrowserRefDenialWire.NameOf(BrowserRefDenial.ViewportMismatch));

        foreach (var denial in BrowserRefDenialWire.All)
        {
            Assert.IsTrue(BrowserRefDenialWire.TryParse(BrowserRefDenialWire.NameOf(denial), out var parsed));
            Assert.AreEqual(denial, parsed);
        }

        Assert.IsFalse(BrowserRefDenialWire.TryParse("brand_new_denial", out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserRefDenialWire.NameOf((BrowserRefDenial)1234));
    }

    [TestMethod]
    public void Scope_And_Decision_ToString_Are_Safe_For_Logs()
    {
        var registry = new BrowserRefRegistry();
        var token = registry.Issue(TopLevel, Revisions(), "backend-1");

        Assert.AreEqual("ctx-1/page-1", TopLevel.ToString());
        Assert.AreEqual("ctx-1/page-1[frame-a]", FrameA.ToString());
        StringAssert.Contains(registry.Validate(token.Value, OtherPage, Revisions()).ToString(), "page_mismatch");
    }
}
