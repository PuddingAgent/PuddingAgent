using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 父子页面写租约的账本（设计方案 §9 / 门禁 G10）：
/// 同页写租约父子不得并存、只读可并行、结束/超时归还、接管与撤权令父子**同时**失效。
/// </summary>
[TestClass]
public sealed class SubAgentDelegationLeaseTests
{
    private const string Page = "page-1";

    [TestMethod]
    public void ParentAndChildCannotHoldTheSamePageWriteLease()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);
        Assert.AreEqual("parent-1", ledger.WriteHolderOf(Page));

        // §9：父子不能并发持有同页写租约 —— 父在写时委派必须先暂停/释放。
        var denied = ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a");
        Assert.IsFalse(denied.Granted);
        StringAssert.Contains(denied.Reason, "parent");
        StringAssert.Contains(denied.Reason, "pause writing");
        Assert.AreEqual("parent-1", ledger.WriteHolderOf(Page));

        // 父释放后子可以取得。
        ledger.ReleaseWrite(Page, "parent-1");
        Assert.IsTrue(ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a").Granted);
        Assert.AreEqual("child:a", ledger.WriteHolderOf(Page));
    }

    [TestMethod]
    public void TwoChildrenCannotWriteTheSamePageConcurrently()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a").Granted);

        var denied = ledger.TryAcquireWrite(Page, "child:b", BrowserLeaseRole.Child, "delegation-b");
        Assert.IsFalse(denied.Granted);
        StringAssert.Contains(denied.Reason, "child");

        // 不同页互不影响（写入按页串行，不是全局串行）。
        Assert.IsTrue(ledger.TryAcquireWrite("page-2", "child:b", BrowserLeaseRole.Child, "delegation-b").Granted);
    }

    [TestMethod]
    public void WritesArePerPageAndIdempotentForTheSameHolder()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);
        Assert.IsTrue(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);

        // 别人的释放动不了我的租约。
        ledger.ReleaseWrite(Page, "someone-else");
        Assert.AreEqual("parent-1", ledger.WriteHolderOf(Page));
    }

    [TestMethod]
    public void ReadObservationsRunInParallel()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireRead(Page, "parent-1").Granted);
        Assert.IsTrue(ledger.TryAcquireRead(Page, "child:a").Granted);
        Assert.IsTrue(ledger.TryAcquireRead(Page, "child:b").Granted);

        Assert.AreEqual(3, ledger.ReaderCountOf(Page));

        ledger.ReleaseRead(Page, "child:a");
        Assert.AreEqual(2, ledger.ReaderCountOf(Page));
    }

    [TestMethod]
    public void EndingOrTimingOutADelegationReturnsTheLeaseToTheParent()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a");
        ledger.TryAcquireRead(Page, "child:delegation-a");

        // 委派结束/超时：子代理的写与读都收回，父可以重新写。
        ledger.EndDelegation("delegation-a");

        Assert.IsNull(ledger.WriteHolderOf(Page));
        Assert.AreEqual(0, ledger.ReaderCountOf(Page));
        Assert.IsTrue(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);
    }

    [TestMethod]
    public void EndingAnUnknownDelegationChangesNothing()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent);
        ledger.EndDelegation("delegation-that-never-existed");

        // 不认识的委派不得顺手清掉父代理的租约。
        Assert.AreEqual("parent-1", ledger.WriteHolderOf(Page));
    }

    [TestMethod]
    public void TakeoverOrRevocationInvalidatesBothParentAndChild()
    {
        // §9：接管和撤权使父子**同时**失效 —— 不是只失效子代理。
        var ledger = new BrowserDelegationLeaseLedger();

        ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a");
        ledger.TryAcquireRead(Page, "child:a");
        ledger.TryAcquireRead(Page, "parent-1");

        ledger.RevokePage(Page);

        Assert.IsTrue(ledger.IsRevoked(Page));
        Assert.IsNull(ledger.WriteHolderOf(Page));
        Assert.AreEqual(0, ledger.ReaderCountOf(Page));

        // 撤权之后不再授予新租约（父子都一样）。
        Assert.IsFalse(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);
        Assert.IsFalse(ledger.TryAcquireWrite(Page, "child:b", BrowserLeaseRole.Child, "delegation-b").Granted);
        Assert.IsFalse(ledger.TryAcquireRead(Page, "parent-1").Granted);

        // 重新授权（新 grant）后才恢复。
        ledger.RegrantPage(Page);
        Assert.IsTrue(ledger.TryAcquireWrite(Page, "parent-1", BrowserLeaseRole.Parent).Granted);
    }

    [TestMethod]
    public void RevokingOnePageDoesNotAffectAnother()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        ledger.TryAcquireWrite(Page, "child:a", BrowserLeaseRole.Child, "delegation-a");
        ledger.TryAcquireWrite("page-2", "child:a", BrowserLeaseRole.Child, "delegation-a");

        ledger.RevokePage(Page);

        Assert.IsTrue(ledger.IsRevoked(Page));
        Assert.IsFalse(ledger.IsRevoked("page-2"));
        Assert.AreEqual("child:a", ledger.WriteHolderOf("page-2"));
    }

    [TestMethod]
    public void ADelegationHoldingTwoPagesReturnsBothOnEnd()
    {
        // 取红用例（round-99 修的真实缺陷）：子代理可能在**第二个标签页**继续写，于是同一委派会在两页上持租约。
        // 早先"委派→页"是**单值**映射，第二页会覆盖第一页 ⇒ EndDelegation 只归还最后一页，
        // **第一页的子代理写租约永久泄漏**，父代理再也拿不回写权。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite("page-a", "child:1", BrowserLeaseRole.Child, "delegation-1").Granted);
        Assert.IsTrue(ledger.TryAcquireWrite("page-b", "child:1", BrowserLeaseRole.Child, "delegation-1").Granted);
        Assert.AreEqual("child:1", ledger.WriteHolderOf("page-a"));
        Assert.AreEqual("child:1", ledger.WriteHolderOf("page-b"));

        ledger.EndDelegation("delegation-1");

        Assert.IsNull(ledger.WriteHolderOf("page-a"), "第一页的租约必须一并归还（泄漏会让父代理永远写不进去）");
        Assert.IsNull(ledger.WriteHolderOf("page-b"));

        // 父代理在两页上都能重新写（这才是"归还"的可观察后果）。
        Assert.IsTrue(ledger.TryAcquireWrite("page-a", "parent", BrowserLeaseRole.Parent).Granted);
        Assert.IsTrue(ledger.TryAcquireWrite("page-b", "parent", BrowserLeaseRole.Parent).Granted);
    }

    [TestMethod]
    public void EndingOneDelegationDoesNotReleaseAnotherDelegationsPages()
    {
        // 多页归还必须**按委派**精确回收：不能把别的委派的页也一起放掉。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite("page-a", "child:1", BrowserLeaseRole.Child, "delegation-1").Granted);
        Assert.IsTrue(ledger.TryAcquireWrite("page-b", "child:2", BrowserLeaseRole.Child, "delegation-2").Granted);

        ledger.EndDelegation("delegation-1");

        Assert.IsNull(ledger.WriteHolderOf("page-a"));
        Assert.AreEqual("child:2", ledger.WriteHolderOf("page-b"), "另一个委派的租约不该被牵连释放");
    }

    [TestMethod]
    public void RevokingOnePageKeepsTheOtherPageOfTheSameDelegation()
    {
        // 撤权是**按页**的：子代理在另一页上的租约不受影响；此后 EndDelegation 仍应归还那一页。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireWrite("page-a", "child:1", BrowserLeaseRole.Child, "delegation-1").Granted);
        Assert.IsTrue(ledger.TryAcquireWrite("page-b", "child:1", BrowserLeaseRole.Child, "delegation-1").Granted);

        ledger.RevokePage("page-a");

        Assert.IsTrue(ledger.IsRevoked("page-a"));
        Assert.AreEqual("child:1", ledger.WriteHolderOf("page-b"), "撤权 page-a 不该动 page-b");

        ledger.EndDelegation("delegation-1");

        Assert.IsNull(ledger.WriteHolderOf("page-b"), "被撤权的那一页摘掉后，同一委派的另一页仍要归还");
    }

    [TestMethod]
    public void AChildThatOnlyObservedIsAlsoClearedWhenTheDelegationEnds()
    {
        // 取红用例（round-100 修的第二个真实缺陷）：子代理完全可以**只拿只读观察**、从不申请写租约。
        // 早先只读页不登记到委派上，且回收只按 `child:{delegationId}` 前缀扫 —— 但只要调用方用了别的
        // 观察者 id（或压根没登记页），这条观察就**永久留在账本里**（"委派结束即不再授权"被违反）。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireRead("page-a", "reader-1", "delegation-1").Granted);
        Assert.IsTrue(ledger.TryAcquireRead("page-b", "reader-2", "delegation-1").Granted);
        Assert.AreEqual(1, ledger.ReaderCountOf("page-a"));
        Assert.AreEqual(1, ledger.ReaderCountOf("page-b"));

        ledger.EndDelegation("delegation-1");

        Assert.AreEqual(0, ledger.ReaderCountOf("page-a"), "委派结束必须回收它登记的只读观察");
        Assert.AreEqual(0, ledger.ReaderCountOf("page-b"));
    }

    [TestMethod]
    public void EndingADelegationKeepsOtherHoldersObservationsOnTheSamePage()
    {
        // 回收只读观察必须**精确**：同一页上别人的观察不能被牵连（否则父代理/其它委派会被无故打断）。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireRead("page-a", "parent", null).Granted);
        Assert.IsTrue(ledger.TryAcquireRead("page-a", "reader-1", "delegation-1").Granted);
        Assert.IsTrue(ledger.TryAcquireRead("page-a", "reader-9", "delegation-9").Granted);
        Assert.AreEqual(3, ledger.ReaderCountOf("page-a"));

        ledger.EndDelegation("delegation-1");

        Assert.AreEqual(2, ledger.ReaderCountOf("page-a"), "父代理与另一个委派的观察都必须留下");
        Assert.IsTrue(ledger.TryAcquireRead("page-a", "parent", null).Granted, "父代理仍可继续观察");
    }

    [TestMethod]
    public void EndingADelegationThatNeverRegisteredAnythingChangesNothing()
    {
        // 防御：用委派 id 申请只读时不登记（delegationId=null）不该产生"幽灵登记"。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireRead("page-a", "parent").Granted);
        ledger.EndDelegation("delegation-never-used");

        Assert.AreEqual(1, ledger.ReaderCountOf("page-a"));
    }

    [TestMethod]
    public void BlankIdentifiersAreRefusedInsteadOfGuessed()
    {
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.ThrowsExactly<ArgumentException>(() => ledger.TryAcquireWrite(" ", "h", BrowserLeaseRole.Parent));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.TryAcquireWrite(Page, "", BrowserLeaseRole.Parent));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.TryAcquireRead(Page, "  "));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.RevokePage(""));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.EndDelegation(" "));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.RegrantPage(" "));
    }
}
