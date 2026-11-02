using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 观察失效判定（设计 §7.1）。
/// <para>
/// 这一组测试真正防的不是"算错时间"，而是**把已经失效的观察当成还有效**：
/// 在旧坐标上点击不会报错，只会点歪，而且窗口一移就再也复现不了。
/// </para>
/// </summary>
[TestClass]
public sealed class ComputerObservationRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = Now.AddMinutes(5);

    private static ComputerObservation Make(
        string id, long display = 1, long geometry = 1, long content = 1,
        DateTimeOffset? expires = null, string windowId = "w1")
    {
        var window = new ComputerWindowIdentity(new ComputerWindowId(windowId), 100, 200, 1);
        var epoch = new ComputerExecutionEpoch("d", 1, "worker", 0);
        return new ComputerObservation(
            new ComputerObservationId(id), epoch, window,
            display, geometry, content,
            Now, expires ?? Future);
    }

    [TestMethod]
    public void Fresh_observation_is_valid()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1"));

        Assert.IsTrue(registry.IsValid(new ComputerObservationId("o1"), 1, 1, 1, Now));
    }

    [TestMethod]
    public void Unknown_observation_is_never_valid()
    {
        var registry = new ComputerObservationRegistry();
        Assert.IsFalse(registry.IsValid(new ComputerObservationId("nope"), 1, 1, 1, Now),
            "没登记过的观察一律不可信，不能默认放行");
    }

    [TestMethod]
    public void Expiry_invalidates_even_when_revisions_are_unchanged()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1", expires: Now.AddSeconds(30)));

        Assert.IsTrue(registry.IsValid(new ComputerObservationId("o1"), 1, 1, 1, Now));
        Assert.IsFalse(registry.IsValid(new ComputerObservationId("o1"), 1, 1, 1, Now.AddSeconds(31)),
            "到期后即使四个事实都没变也必须失效");
    }

    [TestMethod]
    public void Window_geometry_change_invalidates_immediately()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1", geometry: 1));

        Assert.IsFalse(registry.IsValid(new ComputerObservationId("o1"), 1, 2, 1, Now),
            "窗口一移动坐标就偏了，而这时可能才过去几百毫秒");
    }

    [TestMethod]
    public void Display_layout_change_invalidates_immediately()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1", display: 1));

        Assert.IsFalse(registry.IsValid(new ComputerObservationId("o1"), 2, 1, 1, Now),
            "插拔显示器/改缩放会让整块屏幕的坐标系改变");
    }

    [TestMethod]
    public void Content_change_invalidates_conservatively()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1", content: 1));

        Assert.IsFalse(registry.IsValid(new ComputerObservationId("o1"), 1, 1, 2, Now),
            "内容变了，元素 ref 指向的控件可能已不存在；宁可多截一次图");
    }

    [TestMethod]
    public void Write_action_invalidates_only_that_window()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("a1", windowId: "w1"));
        registry.Register(Make("a2", windowId: "w1"));
        registry.Register(Make("b1", windowId: "w2"));

        var removed = registry.InvalidateWindow(new ComputerWindowId("w1"));

        Assert.AreEqual(2, removed);
        Assert.IsFalse(registry.IsValid(new ComputerObservationId("a1"), 1, 1, 1, Now));
        Assert.IsFalse(registry.IsValid(new ComputerObservationId("a2"), 1, 1, 1, Now));
        Assert.IsTrue(registry.IsValid(new ComputerObservationId("b1"), 1, 1, 1, Now),
            "别的窗口的观察不该被牵连");
    }

    [TestMethod]
    public void Registry_is_bounded_and_drops_the_oldest()
    {
        var registry = new ComputerObservationRegistry();
        for (var i = 0; i < ComputerObservationRegistry.Capacity + 10; i++)
            registry.Register(Make("o" + i));

        Assert.AreEqual(ComputerObservationRegistry.Capacity, registry.Count,
            "登记表记录的是「刚刚发生过什么」，不该无限增长");
        Assert.IsFalse(registry.IsValid(new ComputerObservationId("o0"), 1, 1, 1, Now),
            "最旧的应当已被淘汰");
        Assert.IsTrue(registry.IsValid(
            new ComputerObservationId("o" + (ComputerObservationRegistry.Capacity + 9)), 1, 1, 1, Now));
    }

    [TestMethod]
    public void Re_registering_same_id_updates_in_place()
    {
        var registry = new ComputerObservationRegistry();
        registry.Register(Make("o1", content: 1));
        registry.Register(Make("o1", content: 2));

        Assert.AreEqual(1, registry.Count, "同一 id 重复登记不应各占一个槽位");
        Assert.IsTrue(registry.IsValid(new ComputerObservationId("o1"), 1, 1, 2, Now),
            "应当用最新一次登记的事实来判定");
    }
}
