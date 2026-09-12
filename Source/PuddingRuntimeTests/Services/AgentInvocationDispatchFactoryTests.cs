using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// A01-slice-1：父级接续身份 —— 禁止凭空创建 <c>msg-*</c> 会话。
/// </summary>
/// <remarks>
/// 不变式 I1：<c>source=subagent</c> / <c>intent=subagent_result</c> 的续行必须接续父会话身份。
/// 覆盖设计文档 <c>temp/a01-slice1-design.md</c> §4 的反例 A-E 及解析优先级细节。
/// </remarks>
[TestClass]
public sealed class AgentInvocationDispatchFactoryTests
{
    private const string WorkspaceId = "ws-a01";
    private const string AgentId = "agent-a01";
    private const string MainSessionId = "main-session-a01";
    private const string ParentSession = "conv-parent";
    private const string ChildEventSession = "msg-child-1";

    // ── 反例 A：periodic recovery 形态（有持久父身份，事件 session 为空）──
    [TestMethod]
    public async Task StreamDispatch_PersistedParentSessionWithoutEventSession_ResolvesParentAndNeverForgesMsgSession()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-parent-only",
            eventSessionId: null,
            metadata: SubAgentMetadata(("parent_session", ParentSession))));

        Assert.IsTrue(dispatch.UsesStreamDispatch, "source=subagent must use the stream dispatch path.");
        Assert.AreEqual(ParentSession, dispatch.Request.SessionId);
        Assert.IsFalse(
            dispatch.Request.SessionId.StartsWith("msg-", StringComparison.Ordinal),
            "The parent conversation identity must never be replaced by a forged msg-* session.");
        Assert.AreNotEqual("msg-m-parent-only", dispatch.Request.SessionId);
    }

    // ── 反例 B：事件 session 是子会话，持久父身份优先 ──
    [TestMethod]
    public async Task StreamDispatch_ChildEventSessionWithPersistedParent_ParentIdentityWins()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-child-event",
            eventSessionId: ChildEventSession,
            metadata: SubAgentMetadata(("parent_session", ParentSession))));

        Assert.IsTrue(dispatch.UsesStreamDispatch);
        Assert.AreEqual(ParentSession, dispatch.Request.SessionId);
        Assert.AreNotEqual(ChildEventSession, dispatch.Request.SessionId);
    }

    // ── 反例 C：无父身份 + 无事件 session + stream dispatch → 主会话（不是 msg-{MessageId}）──
    [TestMethod]
    public async Task StreamDispatch_NoParentIdentityAndNoEventSession_FallsBackToMainSession()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-no-parent",
            eventSessionId: null,
            metadata: SubAgentMetadata()));

        Assert.IsTrue(dispatch.UsesStreamDispatch);
        Assert.AreEqual(MainSessionId, dispatch.Request.SessionId);
        Assert.AreNotEqual("msg-m-no-parent", dispatch.Request.SessionId);
        Assert.IsFalse(dispatch.Request.SessionId.StartsWith("msg-", StringComparison.Ordinal));
    }

    // ── 反例 D：非 stream dispatch（普通消息）→ 始终主会话，忽略事件 session 与父身份 metadata ──
    [TestMethod]
    public async Task NonStreamDispatch_IgnoresEventSessionAndMetadata_KeepsMainSession()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-ordinary",
            eventSessionId: ChildEventSession,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parent_session"] = ParentSession,
                ["conversation_id"] = ParentSession,
            }));

        Assert.IsFalse(dispatch.UsesStreamDispatch, "Ordinary messages must keep the buffered path.");
        Assert.AreEqual(MainSessionId, dispatch.Request.SessionId);
    }

    // ── 反例 E：无父身份 + 无事件 session + 无主会话 → 抛异常（绝不静默造会话）──
    [TestMethod]
    public async Task StreamDispatch_NoParentIdentityAndNoMainSession_ThrowsInsteadOfForgingSession()
    {
        var factory = CreateFactory(mainSessionId: null);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            factory.CreateForWorkspaceAgentAsync(CreateInvocation(
                messageId: "m-orphan",
                eventSessionId: null,
                metadata: SubAgentMetadata())));

        StringAssert.Contains(error.Message, "does not have a bound main session");
    }

    // ── 显式参数 ParentConversationId 优先级最高（高于 metadata 与事件 session）──
    [TestMethod]
    public async Task ExplicitParentConversationIdParameter_WinsOverMetadataAndEventSession()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-explicit-param",
            eventSessionId: ChildEventSession,
            parentConversationId: "conv-explicit",
            metadata: SubAgentMetadata(
                ("parent_conversation_id", "conv-metadata"),
                ("parent_session", ParentSession))));

        Assert.AreEqual("conv-explicit", dispatch.Request.SessionId);
    }

    // ── metadata 键优先级：parent_conversation_id > parent_session_id > parent_session > conversation_id ──
    [TestMethod]
    public async Task MetadataParentKeys_ResolveInDocumentedPriorityOrder()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-priority",
            eventSessionId: null,
            metadata: SubAgentMetadata(
                ("conversation_id", "conv-lowest"),
                ("parent_session", "conv-session"),
                ("parent_session_id", "conv-session-id"),
                ("parent_conversation_id", "conv-conversation-id"))));

        Assert.AreEqual("conv-conversation-id", dispatch.Request.SessionId);
    }

    [TestMethod]
    public async Task MetadataParentSession_UsedWhenHigherPriorityKeysAbsent()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-priority-2",
            eventSessionId: null,
            metadata: SubAgentMetadata(
                ("conversation_id", "conv-lowest"),
                ("parent_session", "conv-session"))));

        Assert.AreEqual("conv-session", dispatch.Request.SessionId);
    }

    // ── metadata 键大小写不敏感 ──
    [TestMethod]
    public async Task MetadataParentSessionKey_IsCaseInsensitive()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-case-insensitive",
            eventSessionId: null,
            metadata: SubAgentMetadata(("Parent_Session", ParentSession))));

        Assert.AreEqual(ParentSession, dispatch.Request.SessionId);
    }

    // ── 无父身份但有事件 session → 回退事件 session（第 3 优先级，且不伪造 msg-*）──
    [TestMethod]
    public async Task StreamDispatch_NoParentIdentity_UsesEventSessionBeforeMainSession()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-event-only",
            eventSessionId: "conv-event",
            metadata: SubAgentMetadata()));

        Assert.AreEqual("conv-event", dispatch.Request.SessionId);
    }

    // ── intent=subagent_result 同样触发 stream dispatch 与父身份解析 ──
    [TestMethod]
    public async Task StreamDispatch_TriggeredBySubagentResultIntent_AlsoResolvesParentIdentity()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intent"] = "subagent_result",
            ["parent_session"] = ParentSession,
        };

        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-intent",
            eventSessionId: null,
            metadata: metadata));

        Assert.IsTrue(dispatch.UsesStreamDispatch);
        Assert.AreEqual(ParentSession, dispatch.Request.SessionId);
    }

    // ── MessageId 与 MessageText 原样透传（不因会话解析而改变）──
    [TestMethod]
    public async Task ResolvedDispatch_PreservesMessageIdentity()
    {
        var dispatch = await CreateFactory().CreateForWorkspaceAgentAsync(CreateInvocation(
            messageId: "m-passthrough",
            eventSessionId: null,
            metadata: SubAgentMetadata(("parent_session", ParentSession))));

        Assert.AreEqual("m-passthrough", dispatch.Request.MessageId);
        Assert.AreEqual("sub-agent result payload", dispatch.Request.MessageText);
        Assert.AreEqual(AgentId, dispatch.Request.AgentInstanceId);
        Assert.AreEqual(WorkspaceId, dispatch.Request.WorkspaceId);
    }

    // ── helpers ──

    private static AgentInvocationDispatchFactory CreateFactory(string? mainSessionId = MainSessionId)
        => new(
            new StubAgentRuntimeProfileResolver(new AgentRuntimeProfile
            {
                WorkspaceId = WorkspaceId,
                AgentId = AgentId,
                DisplayName = "A01 Test Agent",
                MainSessionId = mainSessionId,
                SourceTemplateId = "workspace-task-agent",
                PreferredProviderId = "provider-test",
                PreferredModelId = "model-test",
            }),
            NullLogger<AgentInvocationDispatchFactory>.Instance);

    private static WorkspaceAgentInvocation CreateInvocation(
        string messageId,
        string? eventSessionId,
        IReadOnlyDictionary<string, string>? metadata,
        string? parentConversationId = null)
        => new()
        {
            WorkspaceId = WorkspaceId,
            AgentId = AgentId,
            MessageId = messageId,
            MessageText = "sub-agent result payload",
            EventSessionId = eventSessionId,
            ParentConversationId = parentConversationId,
            Metadata = metadata,
        };

    private static Dictionary<string, string> SubAgentMetadata(params (string Key, string Value)[] entries)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "subagent",
        };

        foreach (var (key, value) in entries)
            metadata[key] = value;

        return metadata;
    }

    private sealed class StubAgentRuntimeProfileResolver(AgentRuntimeProfile profile) : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
            => Task.FromResult(profile);
    }
}
