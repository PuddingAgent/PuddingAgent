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
