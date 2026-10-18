#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// 三种游标不得混用（Plan §4.2；冻结合同 <see cref="CursorContract"/> 的 XML 文档与枚举顺序）。
/// </summary>
[TestClass]
public sealed class CursorContractTests
{
    private const string Conversation = "conv-1";
    private const string OtherConversation = "conv-2";

    [TestMethod]
    public void Validate_LegalCursorTriple_Passes()
    {
        var failure = CursorContract.Validate(
            new EventHead(Conversation, 10),
            new SnapshotCursor(Conversation, 8),
            new LastAppliedSequence(Conversation, 5));

        Assert.AreEqual(CursorContractFailure.None, failure);
    }

    [TestMethod]
    public void Validate_WithoutLastApplied_Passes()
    {
        var failure = CursorContract.Validate(
            new EventHead(Conversation, 10),
            new SnapshotCursor(Conversation, 10));

        Assert.AreEqual(CursorContractFailure.None, failure);
    }

    [TestMethod]
    public void Validate_SnapshotAndAppliedAtHead_Passes()
    {
        var failure = CursorContract.Validate(
            new EventHead(Conversation, 5),
            new SnapshotCursor(Conversation, 5),
            new LastAppliedSequence(Conversation, 5));

        Assert.AreEqual(CursorContractFailure.None, failure);
    }

    [TestMethod]
    public void Validate_EmptyHeadConversationId_Fails()
        => Assert.AreEqual(
            CursorContractFailure.EmptyConversationId,
            CursorContract.Validate(new EventHead("", 5), new SnapshotCursor(Conversation, 5)));

    [TestMethod]
    public void Validate_EmptySnapshotConversationId_Fails()
        => Assert.AreEqual(
            CursorContractFailure.EmptyConversationId,
            CursorContract.Validate(new EventHead(Conversation, 5), new SnapshotCursor("", 5)));

    [TestMethod]
    public void Validate_EmptyLastAppliedConversationId_Fails()
        => Assert.AreEqual(
            CursorContractFailure.EmptyConversationId,
            CursorContract.Validate(new EventHead(Conversation, 5), new SnapshotCursor(Conversation, 5), new LastAppliedSequence("", 5)));

    [TestMethod]
    public void Validate_ConversationMismatch_Fails()
    {
        Assert.AreEqual(
            CursorContractFailure.ConversationMismatch,
            CursorContract.Validate(new EventHead(Conversation, 10), new SnapshotCursor(OtherConversation, 8)));

        Assert.AreEqual(
            CursorContractFailure.ConversationMismatch,
            CursorContract.Validate(
                new EventHead(Conversation, 10),
                new SnapshotCursor(Conversation, 8),
                new LastAppliedSequence(OtherConversation, 5)));
    }

    [TestMethod]
    public void Validate_NegativeSequences_Fail()
    {
        Assert.AreEqual(
            CursorContractFailure.NegativeSequence,
            CursorContract.Validate(new EventHead(Conversation, -1), new SnapshotCursor(Conversation, 0)));

        Assert.AreEqual(
            CursorContractFailure.NegativeSequence,
            CursorContract.Validate(new EventHead(Conversation, 10), new SnapshotCursor(Conversation, -1)));

        Assert.AreEqual(
            CursorContractFailure.NegativeSequence,
            CursorContract.Validate(
                new EventHead(Conversation, 10),
                new SnapshotCursor(Conversation, 5),
                new LastAppliedSequence(Conversation, -1)));
    }

    [TestMethod]
    public void Validate_SnapshotAheadOfHead_Fails()
        => Assert.AreEqual(
            CursorContractFailure.SnapshotAheadOfHead,
            CursorContract.Validate(new EventHead(Conversation, 5), new SnapshotCursor(Conversation, 6)));

    [TestMethod]
    public void Validate_AppliedAheadOfHead_Fails()
        => Assert.AreEqual(
            CursorContractFailure.AppliedAheadOfHead,
            CursorContract.Validate(
                new EventHead(Conversation, 5),
                new SnapshotCursor(Conversation, 4),
                new LastAppliedSequence(Conversation, 6)));

    [TestMethod]
    public void Validate_FailurePrecedence_FollowsFrozenOrder()
    {
        // 1) 空会话 id 最先于会话不一致/负序列。
        Assert.AreEqual(
            CursorContractFailure.EmptyConversationId,
            CursorContract.Validate(
                new EventHead(Conversation, -5),
                new SnapshotCursor("", -4),
                new LastAppliedSequence(OtherConversation, -3)));

        // 2) 会话不一致先于负序列。
        Assert.AreEqual(
            CursorContractFailure.ConversationMismatch,
            CursorContract.Validate(
                new EventHead(Conversation, -5),
                new SnapshotCursor(OtherConversation, -4)));

        // 3) 负序列先于 snapshot 超前。
        Assert.AreEqual(
            CursorContractFailure.NegativeSequence,
            CursorContract.Validate(new EventHead(Conversation, -5), new SnapshotCursor(Conversation, -2)));

        // 4) snapshot 超前先于 lastApplied 超前。
        Assert.AreEqual(
            CursorContractFailure.SnapshotAheadOfHead,
            CursorContract.Validate(
                new EventHead(Conversation, 5),
                new SnapshotCursor(Conversation, 6),
                new LastAppliedSequence(Conversation, 7)));
    }

    [TestMethod]
    public void CursorContractFailure_FrozenEnumValues()
    {
        Assert.AreEqual(0, (int)CursorContractFailure.None);
        Assert.AreEqual(1, (int)CursorContractFailure.EmptyConversationId);
        Assert.AreEqual(2, (int)CursorContractFailure.ConversationMismatch);
        Assert.AreEqual(3, (int)CursorContractFailure.NegativeSequence);
        Assert.AreEqual(4, (int)CursorContractFailure.SnapshotAheadOfHead);
        Assert.AreEqual(5, (int)CursorContractFailure.AppliedAheadOfHead);
    }

    [TestMethod]
    public void CursorValueTypes_AreReadonlyStructsWithExpectedShape()
    {
        var head = new EventHead(Conversation, 3);
        var snapshot = new SnapshotCursor(Conversation, 2);
        var applied = new LastAppliedSequence(Conversation, 1);

        Assert.AreEqual(Conversation, head.ConversationId);
        Assert.AreEqual(3L, head.Sequence);
        Assert.AreEqual(Conversation, snapshot.ConversationId);
        Assert.AreEqual(2L, snapshot.Sequence);
        Assert.AreEqual(Conversation, applied.ConversationId);
        Assert.AreEqual(1L, applied.Sequence);
    }
}
