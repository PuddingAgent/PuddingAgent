using PuddingCore.Panels;

namespace PuddingCoreTests.Panels;

/// <summary>
/// <see cref="InMemoryPanelCanvasStore"/> 的行为断言。
///
/// <para>
/// 这一层唯一的「难」点是 <c>Revision</c>：面板端拿它判断「要不要重新渲染」，
/// 所以它必须**严格单调且不重复**，并发发布下也不能出两个相同的版本号 ——
/// 否则刷新会静默丢失。这条由 <see cref="Concurrent_publishes_never_repeat_a_revision"/> 钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class PanelCanvasStoreTests
{
    [TestMethod]
    public void First_publish_starts_at_revision_one()
    {
        var store = new InMemoryPanelCanvasStore();

        var snapshot = store.Publish("main", "<p>hi</p>", "标题");

        Assert.AreEqual("main", snapshot.PanelId);
        Assert.AreEqual("<p>hi</p>", snapshot.Content);
        Assert.AreEqual("标题", snapshot.Title);
        Assert.AreEqual(1L, snapshot.Revision);
    }

    [TestMethod]
    public void Republishing_replaces_content_and_increments_revision()
    {
        var store = new InMemoryPanelCanvasStore();

        store.Publish("main", "v1");
        var second = store.Publish("main", "v2", "新标题");

        Assert.AreEqual(2L, second.Revision);
        Assert.AreEqual("v2", second.Content);
        Assert.AreEqual("新标题", second.Title);
        Assert.AreEqual("v2", store.Get("main")!.Content);
    }

    [TestMethod]
    public void Missing_canvas_returns_null_rather_than_empty_content()
    {
        var store = new InMemoryPanelCanvasStore();

        Assert.IsNull(store.Get("nope"));
        // 空 id 不算「一块空画布」，也不能抛异常影响调用方。
        Assert.IsNull(store.Get("   "));
    }

    [TestMethod]
    public void Panel_id_is_case_insensitive_and_keeps_first_spelling()
    {
        var store = new InMemoryPanelCanvasStore();

        store.Publish("Main", "v1");
        var second = store.Publish("MAIN", "v2");

        // 同一块画布：版本号必须接着走，不能因为大小写不同另起一块。
        Assert.AreEqual(2L, second.Revision);
        // 返回体沿用首次书写形式，避免同一个 id 在响应里来回变。
        Assert.AreEqual("Main", second.PanelId);
        Assert.AreEqual("v2", store.Get("main")!.Content);
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public void Blank_title_is_normalized_to_null()
    {
        var store = new InMemoryPanelCanvasStore();

        Assert.IsNull(store.Publish("a", "x", "   ").Title);
        Assert.IsNull(store.Publish("b", "x", "").Title);
        Assert.AreEqual("t", store.Publish("c", "x", "  t  ").Title);
    }

    [TestMethod]
    public void Panel_id_shape_is_guarded()
    {
        var store = new InMemoryPanelCanvasStore();

        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("   ", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("main/evil", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("main evil", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Publish("画布", "x"));
        Assert.ThrowsExactly<ArgumentException>(
            () => store.Publish(new string('a', PanelCanvasLimits.MaxPanelIdLength + 1), "x"));
    }

    [TestMethod]
    public void Content_and_title_limits_are_enforced()
    {
        var store = new InMemoryPanelCanvasStore();

        // 越界必须是**明确拒绝**，不静默截断：截断会让画布显示残缺内容而无从察觉。
        Assert.ThrowsExactly<ArgumentException>(
            () => store.Publish("main", new string('a', PanelCanvasLimits.MaxContentChars + 1)));
        Assert.ThrowsExactly<ArgumentException>(
            () => store.Publish("main", "x", new string('t', PanelCanvasLimits.MaxTitleLength + 1)));

        // 边界值本身必须能通过（差一位就翻面的断言才有效）。
        Assert.AreEqual(1L, store.Publish("edge", new string('a', PanelCanvasLimits.MaxContentChars)).Revision);
    }

    [TestMethod]
    public void Null_content_is_rejected()
    {
        var store = new InMemoryPanelCanvasStore();

        Assert.ThrowsExactly<ArgumentNullException>(() => store.Publish("main", null!));
    }

    [TestMethod]
    public void Remove_reports_whether_the_canvas_existed()
    {
        var store = new InMemoryPanelCanvasStore();
        store.Publish("a", "x");

        Assert.IsTrue(store.Remove("A"));
        Assert.IsNull(store.Get("a"));
        Assert.IsFalse(store.Remove("a"));
        Assert.IsFalse(store.Remove("   "));
    }

    [TestMethod]
    public void List_is_ordered_by_panel_id_and_holds_no_duplicates()
    {
        var store = new InMemoryPanelCanvasStore();

        store.Publish("zeta", "x");
        store.Publish("alpha", "x");
        store.Publish("Alpha", "x"); // 大小写归一 ⇒ 覆盖同一块

        var ids = store.List().Select(snapshot => snapshot.PanelId).ToArray();

        CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, ids);
    }

    [TestMethod]
    public void Concurrent_publishes_never_repeat_a_revision()
    {
        var store = new InMemoryPanelCanvasStore();
        const int publishes = 200;
        var revisions = new long[publishes];

        Parallel.For(0, publishes, index =>
        {
            revisions[index] = store.Publish("main", index.ToString()).Revision;
        });

        // 不重复：任何一个重复都会让面板漏掉一次刷新。
        Assert.AreEqual(publishes, revisions.Distinct().Count());
        // 且严格覆盖 1..N，说明没有跳号也没有从别的起点开始。
        Assert.AreEqual(1L, revisions.Min());
        Assert.AreEqual((long)publishes, revisions.Max());
    }
}
