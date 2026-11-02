using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 操作账本（设计 §7.2）。核心不变量：**同一个 operation 只有一种语义**，
/// 且**永不因为拥挤而丢掉未定结果**。
/// </summary>
[TestClass]
public sealed class ComputerOperationLedgerTests
{
    private static ComputerActionReceipt Receipt(string operationId, ComputerCompletion completion) =>
        new(operationId, ComputerExecution.Dispatched, ComputerVerification.Unverified,
            completion, true, false, "uia", null, null, [], null);

    [TestMethod]
    public void First_admit_is_accepted()
    {
        var ledger = new ComputerOperationLedger();
        var r = ledger.TryAdmit("op1", "fp-a");

        Assert.AreEqual(OperationAdmitStatus.Accepted, r.Status);
        Assert.IsTrue(r.CanExecute);
        Assert.IsNull(r.Existing, "首次准入还没有回执");
    }

    [TestMethod]
    public void Same_operation_and_fingerprint_is_deduplicated()
    {
        var ledger = new ComputerOperationLedger();
        ledger.TryAdmit("op1", "fp-a");

        var second = ledger.TryAdmit("op1", "fp-a");

        Assert.AreEqual(OperationAdmitStatus.AlreadyKnown, second.Status,
            "同一 operation + 同一指纹 = 同一次重试，不能当成新操作再执行一遍");
        Assert.IsFalse(second.CanExecute);
    }

    [TestMethod]
    public void Same_operation_with_different_fingerprint_is_a_conflict()
    {
        var ledger = new ComputerOperationLedger();
        ledger.TryAdmit("op1", "fp-a");

        var conflict = ledger.TryAdmit("op1", "fp-b");

        Assert.AreEqual(OperationAdmitStatus.Conflict, conflict.Status,
            "同一 operation 换了参数语义，必须冲突而不是静默覆盖");
        Assert.IsFalse(conflict.CanExecute);
    }

    [TestMethod]
    public void Conflict_does_not_overwrite_the_original_fingerprint()
    {
        var ledger = new ComputerOperationLedger();
        ledger.TryAdmit("op1", "fp-a");
        ledger.TryAdmit("op1", "fp-b");   // 冲突，不得覆盖

        // 原指纹仍然认得出来
        Assert.AreEqual(OperationAdmitStatus.AlreadyKnown, ledger.TryAdmit("op1", "fp-a").Status);
        // 新指纹仍然冲突
        Assert.AreEqual(OperationAdmitStatus.Conflict, ledger.TryAdmit("op1", "fp-b").Status);
    }

    [TestMethod]
    public void Completed_receipt_is_returned_on_repeat()
    {
        var ledger = new ComputerOperationLedger();
        ledger.TryAdmit("op1", "fp-a");
        var receipt = Receipt("op1", ComputerCompletion.Unknown);
        Assert.IsTrue(ledger.Complete("op1", receipt));

        var again = ledger.TryAdmit("op1", "fp-a");

        Assert.AreEqual(OperationAdmitStatus.AlreadyKnown, again.Status);
        Assert.IsNotNull(again.Existing, "已完成的操作应当把原回执带回去，而不是让调用方再猜一次");
        Assert.AreEqual(ComputerCompletion.Unknown, again.Existing!.Completion);
    }

    [TestMethod]
    public void Unknown_outcome_survives_a_full_ledger()
    {
        var ledger = new ComputerOperationLedger();
        ledger.TryAdmit("op-unknown", "fp");
        ledger.Complete("op-unknown", Receipt("op-unknown", ComputerCompletion.Unknown));

        // 把账本填满
        for (var i = 0; i < ComputerOperationLedger.Capacity - 1; i++)
            ledger.TryAdmit("fill-" + i, "fp");

        Assert.AreEqual(ComputerOperationLedger.Capacity, ledger.Count);
        Assert.AreEqual(OperationAdmitStatus.Full, ledger.TryAdmit("new-op", "fp").Status,
            "账本满时拒绝新写入，而不是驱逐旧记录");

        // 关键：unknown 那条仍在，且仍能把回执带回来
        var again = ledger.TryAdmit("op-unknown", "fp");
        Assert.AreEqual(OperationAdmitStatus.AlreadyKnown, again.Status);
        Assert.AreEqual(ComputerCompletion.Unknown, again.Existing!.Completion,
            "unknown 一旦被驱逐，调用方就再也查不到它成没成");
    }

    [TestMethod]
    public void Completing_an_unadmitted_operation_is_rejected()
    {
        var ledger = new ComputerOperationLedger();
        Assert.IsFalse(ledger.Complete("nope", Receipt("nope", ComputerCompletion.Verified)),
            "没准入过的操作不允许凭空写入终态");
    }

    [TestMethod]
    public void Blank_arguments_are_rejected()
    {
        var ledger = new ComputerOperationLedger();
        Assert.ThrowsExactly<ArgumentException>(() => ledger.TryAdmit("", "fp"));
        Assert.ThrowsExactly<ArgumentException>(() => ledger.TryAdmit("op", " "));
    }
}
