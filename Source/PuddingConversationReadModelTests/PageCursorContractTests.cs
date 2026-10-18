#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// 历史/过程分页游标合同（Plan §5.2、§5.3；冻结校验顺序见 <see cref="PageCursorContract"/> XML 文档）。
/// </summary>
[TestClass]
public sealed class PageCursorContractTests
{
    private const string Conversation = "conv-1";
    private const string OtherConversation = "conv-2";
    private static readonly DateTimeOffset BeforeCreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);

    // ============================================================ 历史游标

    private static HistoryPageCursor History(
        string conversationId = Conversation,
        string beforeMessageId = "msg-9",
        int sortVersion = ConversationReadModelLimits.HistoryOrderVersion,
        int limit = 20)
        => new(conversationId, BeforeCreatedAt, beforeMessageId, sortVersion, limit);

    [TestMethod]
    public void ValidateHistory_LegalCursor_Passes()
    {
        var failure = PageCursorContract.ValidateHistory(History(), Conversation, ConversationReadModelLimits.HistoryOrderVersion);

        Assert.AreEqual(PageCursorFailure.None, failure);
    }

    [TestMethod]
    public void ValidateHistory_NullCursor_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(
            () => PageCursorContract.ValidateHistory(null!, Conversation, ConversationReadModelLimits.HistoryOrderVersion));

    [TestMethod]
    public void ValidateHistory_EmptyConversationId_Fails()
    {
        Assert.AreEqual(
            PageCursorFailure.EmptyConversationId,
            PageCursorContract.ValidateHistory(History(conversationId: ""), Conversation, 1));

        Assert.AreEqual(
            PageCursorFailure.EmptyConversationId,
            PageCursorContract.ValidateHistory(History(), "", 1));
    }

    [TestMethod]
    public void ValidateHistory_ConversationMismatch_Fails()
        => Assert.AreEqual(
            PageCursorFailure.ConversationMismatch,
            PageCursorContract.ValidateHistory(History(conversationId: OtherConversation), Conversation, 1));

    [TestMethod]
    public void ValidateHistory_EmptyMessageIdentity_Fails()
    {
        Assert.AreEqual(
            PageCursorFailure.EmptyMessageIdentity,
            PageCursorContract.ValidateHistory(History(beforeMessageId: ""), Conversation, 1));

        Assert.AreEqual(
            PageCursorFailure.EmptyMessageIdentity,
            PageCursorContract.ValidateHistory(History(beforeMessageId: "   "), Conversation, 1));
    }

    [TestMethod]
    public void ValidateHistory_SortVersionMismatch_Fails()
        => Assert.AreEqual(
            PageCursorFailure.SortVersionMismatch,
            PageCursorContract.ValidateHistory(History(sortVersion: 2), Conversation, 1));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(51)]
    public void ValidateHistory_InvalidLimit_Fails(int limit)
        => Assert.AreEqual(
            PageCursorFailure.InvalidLimit,
            PageCursorContract.ValidateHistory(History(limit: limit), Conversation, ConversationReadModelLimits.HistoryOrderVersion));

    [TestMethod]
    public void ValidateHistory_InvalidLimit_UsesConfiguredMaximum()
    {
        var limits = ConversationReadModelLimits.Default with { MaxOpenMessageLimit = 10 };

        Assert.AreEqual(
            PageCursorFailure.InvalidLimit,
            PageCursorContract.ValidateHistory(History(limit: 11), Conversation, ConversationReadModelLimits.HistoryOrderVersion, limits));

        Assert.AreEqual(
            PageCursorFailure.None,
            PageCursorContract.ValidateHistory(History(limit: 10), Conversation, ConversationReadModelLimits.HistoryOrderVersion, limits));
    }

    [TestMethod]
    public void ValidateHistory_FailurePrecedence_FollowsFrozenOrder()
    {
        // 空会话 id 先于身份/排序版本错误。
        Assert.AreEqual(
            PageCursorFailure.EmptyConversationId,
            PageCursorContract.ValidateHistory(History(conversationId: "", beforeMessageId: "", sortVersion: 9, limit: 0), Conversation, 1));

        // 会话不一致先于身份/排序版本错误。
        Assert.AreEqual(
            PageCursorFailure.ConversationMismatch,
            PageCursorContract.ValidateHistory(History(conversationId: OtherConversation, beforeMessageId: "", sortVersion: 9), Conversation, 1));

        // 空消息身份先于排序版本错误。
        Assert.AreEqual(
            PageCursorFailure.EmptyMessageIdentity,
            PageCursorContract.ValidateHistory(History(beforeMessageId: "", sortVersion: 9), Conversation, 1));

        // 排序版本错误先于非法 limit。
        Assert.AreEqual(
            PageCursorFailure.SortVersionMismatch,
            PageCursorContract.ValidateHistory(History(sortVersion: 9, limit: 0), Conversation, 1));
    }

    // ============================================================ 过程游标

    private static ProcessPageCursor Process(
        string conversationId = Conversation,
        string? turnId = "turn-1",
        long beforeSequence = 10,
        long throughSequence = 20,
        int limit = 100)
        => new(conversationId, turnId, beforeSequence, throughSequence, limit);

    [TestMethod]
    public void ValidateProcess_LegalCursor_Passes()
    {
        var failure = PageCursorContract.ValidateProcess(Process(), Conversation, "turn-1", eventHeadSequence: 100);

        Assert.AreEqual(PageCursorFailure.None, failure);
    }

    [TestMethod]
    public void ValidateProcess_NullCursor_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(
            () => PageCursorContract.ValidateProcess(null!, Conversation, null, 100));

    [TestMethod]
    public void ValidateProcess_EmptyConversationId_Fails()
    {
        Assert.AreEqual(
            PageCursorFailure.EmptyConversationId,
            PageCursorContract.ValidateProcess(Process(conversationId: ""), Conversation, "turn-1", 100));

        Assert.AreEqual(
            PageCursorFailure.EmptyConversationId,
            PageCursorContract.ValidateProcess(Process(), "", "turn-1", 100));
    }

    [TestMethod]
    public void ValidateProcess_ConversationMismatch_Fails()
        => Assert.AreEqual(
            PageCursorFailure.ConversationMismatch,
            PageCursorContract.ValidateProcess(Process(conversationId: OtherConversation), Conversation, "turn-1", 100));

    [TestMethod]
    public void ValidateProcess_TurnMismatch_Fails()
    {
        Assert.AreEqual(
            PageCursorFailure.TurnMismatch,
            PageCursorContract.ValidateProcess(Process(turnId: "turn-2"), Conversation, "turn-1", 100));

        Assert.AreEqual(
            PageCursorFailure.TurnMismatch,
            PageCursorContract.ValidateProcess(Process(turnId: null), Conversation, "turn-1", 100));
    }

    [TestMethod]
    public void ValidateProcess_EmptyTurnId_MeansCrossTurnQuery()
    {
        Assert.AreEqual(
            PageCursorFailure.None,
            PageCursorContract.ValidateProcess(Process(turnId: null), Conversation, expectedTurnId: null, eventHeadSequence: 100));

        Assert.AreEqual(
            PageCursorFailure.None,
            PageCursorContract.ValidateProcess(Process(turnId: "turn-1"), Conversation, expectedTurnId: null, eventHeadSequence: 100));
    }

    [TestMethod]
    public void ValidateProcess_NegativeSequences_Fail()
    {
        Assert.AreEqual(
            PageCursorFailure.NegativeSequence,
            PageCursorContract.ValidateProcess(Process(beforeSequence: -1), Conversation, "turn-1", 100));

        Assert.AreEqual(
            PageCursorFailure.NegativeSequence,
            PageCursorContract.ValidateProcess(Process(beforeSequence: 0, throughSequence: -2), Conversation, "turn-1", 100));
    }

    [TestMethod]
    public void ValidateProcess_BeforeBeyondThroughPlusOne_Fails()
        => Assert.AreEqual(
            PageCursorFailure.SequenceOutOfRange,
            PageCursorContract.ValidateProcess(Process(beforeSequence: 10, throughSequence: 8), Conversation, "turn-1", 100));

    [TestMethod]
    public void ValidateProcess_BeforeEqualsThroughPlusOne_Passes()
        => Assert.AreEqual(
            PageCursorFailure.None,
            PageCursorContract.ValidateProcess(Process(beforeSequence: 10, throughSequence: 9), Conversation, "turn-1", 100));

    [TestMethod]
    public void ValidateProcess_ThroughAheadOfEventHead_Fails()
        => Assert.AreEqual(
            PageCursorFailure.SequenceOutOfRange,
            PageCursorContract.ValidateProcess(Process(beforeSequence: 101, throughSequence: 101), Conversation, "turn-1", eventHeadSequence: 100));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(201)]
    public void ValidateProcess_InvalidLimit_Fails(int limit)
        => Assert.AreEqual(
            PageCursorFailure.InvalidLimit,
            PageCursorContract.ValidateProcess(Process(limit: limit), Conversation, "turn-1", 100));

    [TestMethod]
    public void ValidateProcess_InvalidLimit_UsesConfiguredMaximum()
    {
        var limits = ConversationReadModelLimits.Default with { MaxDetailPageItems = 10 };

        Assert.AreEqual(
            PageCursorFailure.InvalidLimit,
            PageCursorContract.ValidateProcess(Process(limit: 11), Conversation, "turn-1", 100, limits));

        Assert.AreEqual(
            PageCursorFailure.None,
            PageCursorContract.ValidateProcess(Process(limit: 10), Conversation, "turn-1", 100, limits));
    }

    [TestMethod]
    public void ValidateProcess_FailurePrecedence_FollowsFrozenOrder()
    {
        // 会话不一致先于 Turn 不一致与序列错误。
        Assert.AreEqual(
            PageCursorFailure.ConversationMismatch,
            PageCursorContract.ValidateProcess(
                Process(conversationId: OtherConversation, turnId: "turn-2", beforeSequence: -1, limit: 0),
                Conversation, "turn-1", 100));

        // Turn 不一致先于负序列。
        Assert.AreEqual(
            PageCursorFailure.TurnMismatch,
            PageCursorContract.ValidateProcess(Process(turnId: "turn-2", beforeSequence: -1), Conversation, "turn-1", 100));

        // 负序列先于越界。
        Assert.AreEqual(
            PageCursorFailure.NegativeSequence,
            PageCursorContract.ValidateProcess(Process(beforeSequence: -1, throughSequence: -5), Conversation, "turn-1", 100));

        // 越界先于非法 limit。
        Assert.AreEqual(
            PageCursorFailure.SequenceOutOfRange,
            PageCursorContract.ValidateProcess(Process(beforeSequence: 10, throughSequence: 8, limit: 0), Conversation, "turn-1", 100));
    }

    [TestMethod]
    public void PageCursorFailure_FrozenEnumValues()
    {
        Assert.AreEqual(0, (int)PageCursorFailure.None);
        Assert.AreEqual(1, (int)PageCursorFailure.EmptyConversationId);
        Assert.AreEqual(2, (int)PageCursorFailure.ConversationMismatch);
        Assert.AreEqual(3, (int)PageCursorFailure.EmptyMessageIdentity);
        Assert.AreEqual(4, (int)PageCursorFailure.SortVersionMismatch);
        Assert.AreEqual(5, (int)PageCursorFailure.TurnMismatch);
        Assert.AreEqual(6, (int)PageCursorFailure.NegativeSequence);
        Assert.AreEqual(7, (int)PageCursorFailure.SequenceOutOfRange);
        Assert.AreEqual(8, (int)PageCursorFailure.InvalidLimit);
    }
}
