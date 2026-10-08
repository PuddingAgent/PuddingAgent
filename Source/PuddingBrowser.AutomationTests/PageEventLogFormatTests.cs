using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 页面事件日志（§8）：有界、有序、连续重复合并；不编造事实。
/// </summary>
[TestClass]
public sealed class PageEventLogTests
{
    [TestMethod]
    public void EmptyLogReportsNothing()
    {
        var log = new PageEventLog();

        Assert.IsTrue(log.IsEmpty);
        Assert.AreEqual(0, log.Entries.Count);
        Assert.AreEqual(0, log.TotalCount);
    }

    [TestMethod]
    public void EntriesKeepTheirOrder()
    {
        var log = new PageEventLog();
        log.Add("first");
        log.Add("second");

        CollectionAssert.AreEqual(
            new[] { "first", "second" }, log.Entries.Select(entry => entry.Summary).ToArray());
        Assert.AreEqual(2, log.TotalCount);
    }

    [TestMethod]
    public void ConsecutiveDuplicatesAreMergedInsteadOfFillingTheLog()
    {
        // 一次页面里弹出十个一样的权限询问：报十遍没有信息量，报 ×10 才有。
        var log = new PageEventLog();
        log.Add("permissionRequest kind=camera answered=deny");
        log.Add("permissionRequest kind=camera answered=deny");
        log.Add("permissionRequest kind=camera answered=deny");

        Assert.AreEqual(1, log.Entries.Count);
        Assert.AreEqual(3, log.Entries[0].Count);
        Assert.AreEqual(3, log.TotalCount);

        // 非连续的相同摘要**不**合并（中间发生过别的事，顺序就是信息）。
        log.Add("downloadRequest answered=cancel");
        log.Add("permissionRequest kind=camera answered=deny");
        Assert.AreEqual(3, log.Entries.Count);
    }

    [TestMethod]
    public void CapacityIsEnforced_AndTotalCountStillTellsTheTruth()
    {
        var log = new PageEventLog(capacity: 3);
        for (var index = 1; index <= 6; index++)
        {
            log.Add($"event-{index}");
        }

        Assert.AreEqual(3, log.Entries.Count);
        Assert.AreEqual("event-4", log.Entries[0].Summary);
        Assert.AreEqual("event-6", log.Entries[^1].Summary);
        // 被容量挤掉的不影响"总共发生过几次"。
        Assert.AreEqual(6, log.TotalCount);
    }

    [TestMethod]
    public void BlankSummariesAreIgnored()
    {
        var log = new PageEventLog();
        log.Add(null);
        log.Add("");
        log.Add("   ");

        Assert.IsTrue(log.IsEmpty);
        Assert.AreEqual(0, log.TotalCount, "空摘要不算发生过事件");
    }

    [TestMethod]
    public void CapacityMustBePositive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PageEventLog(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PageEventLog(-1));
    }
}
