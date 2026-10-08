using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// operationId 账本（设计方案 §4.3）：同一次内部请求/重传的驱动执行次数 ≤ 1；
/// 同 ID 不同请求拒绝；过期重传返回 receipt_expired；在途项不因容量被淘汰。
/// </summary>
[TestClass]
public sealed class BrowserOperationLedgerTests
{
    private const string Caller = "agent-1";

    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(7);

    private static BrowserOperationLedger Ledger(
        TestTimeProvider clock,
        int maxEntries = 1024,
        TimeSpan? retention = null) =>
        new(
            new BrowserOperationLedgerOptions
            {
                MaxEntries = maxEntries,
                TerminalRetention = retention ?? TimeSpan.FromMinutes(5),
            },
            clock);

    [TestMethod]
    public void First_Admission_Must_Execute()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();

        var admission = ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");

        Assert.AreEqual(BrowserLedgerAdmissionKind.Accepted, admission.Kind);
        Assert.IsTrue(admission.MustExecute);
        Assert.IsFalse(admission.MustNotExecute);
        Assert.AreEqual(1, ledger.Count);
        Assert.AreEqual(1, ledger.RunningCount);
    }

    [TestMethod]
    public void Retransmission_While_Running_Must_Not_Execute_Again()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();
        ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");

        var retransmission = ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");

        Assert.AreEqual(BrowserLedgerAdmissionKind.AlreadyRunning, retransmission.Kind);
        Assert.IsTrue(retransmission.MustNotExecute);
        Assert.AreEqual(1, ledger.Count, "a retransmission must not create a second ledger entry");
    }

    [TestMethod]
    public void Terminal_Receipt_Is_Replayed_Without_Re_Execution()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();
        var admission = ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");
        var receipt = B0Fixture.Receipt(operationId: operationId);
        Assert.IsTrue(ledger.TryComplete(admission.Entry!, receipt));

        var replay = ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");

        Assert.AreEqual(BrowserLedgerAdmissionKind.Replay, replay.Kind);
        Assert.IsTrue(replay.MustNotExecute, "the driver must run at most once per operation id");
        Assert.AreSame(receipt, replay.Entry!.Receipt);
        Assert.IsNotNull(replay.Decision);
        Assert.IsFalse(replay.Decision!.IsSafeToRetry);
        Assert.AreEqual(1, ledger.TerminalCount);
    }

    [TestMethod]
    public void Same_Operation_Id_With_A_Different_Request_Is_Refused()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();
        ledger.Admit(Caller, Generation, operationId, "fill:#kw:Pudding");

        var conflict = ledger.Admit(Caller, Generation, operationId, "click:#submit");

        Assert.AreEqual(BrowserLedgerAdmissionKind.Conflict, conflict.Kind);
        Assert.IsTrue(conflict.MustNotExecute);
        Assert.AreEqual(BrowserRetryDisposition.OperationConflict, conflict.Decision!.Disposition);
        Assert.IsTrue(conflict.Decision.MayHaveSideEffects);
    }

    [TestMethod]
    public void Expired_Terminal_Receipt_Reports_Receipt_Expired_Instead_Of_A_New_Action()
    {
        var clock = new TestTimeProvider(B0Fixture.Now);
        var ledger = Ledger(clock);
        var operationId = OperationId.NewId();
        var admission = ledger.Admit(Caller, Generation, operationId, "click:#submit");
        ledger.TryComplete(admission.Entry!, B0Fixture.Receipt(operationId: operationId));

        clock.Advance(TimeSpan.FromMinutes(6));

        var late = ledger.Admit(Caller, Generation, operationId, "click:#submit");

        Assert.AreEqual(BrowserLedgerAdmissionKind.ReceiptExpired, late.Kind);
        Assert.IsTrue(late.MustNotExecute, "an expired receipt must not be re-executed as a new action");
        Assert.AreEqual(BrowserRetryDisposition.ReceiptExpired, late.Decision!.Disposition);
        Assert.AreEqual("receipt_expired", late.Decision.WireDisposition);
    }

    [TestMethod]
    public void Capacity_Exhaustion_Refuses_New_Writes_And_Never_Evicts_In_Flight_Work()
    {
        var clock = new TestTimeProvider(B0Fixture.Now);
        var ledger = Ledger(clock, maxEntries: 2);

        Assert.IsTrue(ledger.Admit(Caller, Generation, OperationId.NewId(), "op-a").MustExecute);
        Assert.IsTrue(ledger.Admit(Caller, Generation, OperationId.NewId(), "op-b").MustExecute);

        var refused = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-c");

        Assert.AreEqual(BrowserLedgerAdmissionKind.CapacityExhausted, refused.Kind);
        Assert.IsTrue(refused.MustNotExecute);
        Assert.AreEqual(BrowserRetryDisposition.CapacityExhausted, refused.Decision!.Disposition);
        Assert.AreEqual(2, ledger.RunningCount, "in-flight operations must survive capacity pressure");

        // 在途项也不被「清理过期」误删。
        Assert.AreEqual(0, ledger.Prune());
        Assert.AreEqual(2, ledger.RunningCount);
    }

    [TestMethod]
    public void Expired_Tombstones_Are_Reclaimed_So_Capacity_Recovers()
    {
        var clock = new TestTimeProvider(B0Fixture.Now);
        var ledger = Ledger(clock, maxEntries: 2);

        var first = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-a");
        var second = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-b");
        ledger.TryComplete(first.Entry!, B0Fixture.Receipt(operationId: first.Entry!.OperationId));
        ledger.TryComplete(second.Entry!, B0Fixture.Receipt(operationId: second.Entry!.OperationId));

        clock.Advance(TimeSpan.FromMinutes(6));

        var third = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-c");

        Assert.AreEqual(BrowserLedgerAdmissionKind.Accepted, third.Kind);
        Assert.AreEqual(1, ledger.Count, "expired terminal tombstones are reclaimed under capacity pressure");
    }

    [TestMethod]
    public void Prune_Removes_Only_Expired_Terminal_Entries()
    {
        var clock = new TestTimeProvider(B0Fixture.Now);
        var ledger = Ledger(clock, maxEntries: 8);

        var completed = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-done");
        ledger.TryComplete(completed.Entry!, B0Fixture.Receipt(operationId: completed.Entry!.OperationId));
        ledger.Admit(Caller, Generation, OperationId.NewId(), "op-running");

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.AreEqual(1, ledger.Prune());
        Assert.AreEqual(1, ledger.Count);
        Assert.AreEqual(1, ledger.RunningCount);
    }

    [TestMethod]
    public void Abandoned_Operation_Can_Be_Admitted_Again_As_New()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();
        var admission = ledger.Admit(Caller, Generation, operationId, "op-a");

        Assert.IsTrue(ledger.TryAbandon(admission.Entry!));
        Assert.AreEqual(0, ledger.Count);

        var retry = ledger.Admit(Caller, Generation, operationId, "op-a");
        Assert.AreEqual(BrowserLedgerAdmissionKind.Accepted, retry.Kind);
    }

    [TestMethod]
    public void Completing_A_Non_Running_Entry_Is_Refused()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var admission = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-a");
        ledger.TryComplete(admission.Entry!, B0Fixture.Receipt(operationId: admission.Entry!.OperationId));

        Assert.IsFalse(ledger.TryComplete(admission.Entry!, B0Fixture.Receipt(operationId: admission.Entry.OperationId)));
        Assert.IsFalse(ledger.TryAbandon(admission.Entry!));
    }

    [TestMethod]
    public void Completing_With_A_Receipt_From_Another_Operation_Is_Rejected()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var admission = ledger.Admit(Caller, Generation, OperationId.NewId(), "op-a");

        Assert.ThrowsExactly<ArgumentException>(
            () => ledger.TryComplete(admission.Entry!, B0Fixture.Receipt(operationId: OperationId.NewId())));
    }

    [TestMethod]
    public void Different_Callers_Or_Generations_Do_Not_Share_An_Operation_Id_Entry()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();

        var first = ledger.Admit("agent-1", Generation, operationId, "op-a");
        var otherCaller = ledger.Admit("agent-2", Generation, operationId, "op-a");
        var otherGeneration = ledger.Admit(
            "agent-1", ConnectionGeneration.Require(8), operationId, "op-a");

        Assert.IsTrue(first.MustExecute);
        Assert.IsTrue(otherCaller.MustExecute, "the ledger key includes the caller identity");
        Assert.IsTrue(otherGeneration.MustExecute, "the ledger key includes the connection generation");
        Assert.AreEqual(3, ledger.Count);
    }

    [TestMethod]
    public void TryGet_Exposes_The_Read_Only_Query_Entry_Point()
    {
        var ledger = Ledger(new TestTimeProvider(B0Fixture.Now));
        var operationId = OperationId.NewId();
        var admission = ledger.Admit(Caller, Generation, operationId, "op-a");

        var found = ledger.TryGet(Caller, Generation, operationId);

        Assert.IsNotNull(found);
        Assert.AreEqual(admission.Entry!.Key, found!.Key);
        Assert.IsTrue(found.IsRunning);
        Assert.IsNull(ledger.TryGet(Caller, Generation, OperationId.NewId()));
        Assert.IsNull(ledger.TryGet("agent-2", Generation, operationId));
    }

    [TestMethod]
    public void Options_Are_Validated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new BrowserOperationLedger(new BrowserOperationLedgerOptions { MaxEntries = 0 }));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new BrowserOperationLedger(new BrowserOperationLedgerOptions { TerminalRetention = TimeSpan.Zero }));
    }

    [TestMethod]
    public void Default_Options_Match_The_Design_Budget()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), BrowserOperationLedgerOptions.Default.TerminalRetention);
        Assert.AreEqual(1024, BrowserOperationLedgerOptions.Default.MaxEntries);
    }
}
