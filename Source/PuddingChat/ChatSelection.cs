namespace PuddingChat;

/// <summary>UI-owned selection epoch, per-role drafts and retry identity. No execution state machine.</summary>
public sealed class ChatSelection
{
    private readonly Dictionary<RoleKey, string> _drafts = [];
    private readonly Dictionary<RoleKey, PendingSend> _pending = [];
    public RoleKey? Role { get; private set; }
    public long Generation { get; private set; }
    public Conversation? Conversation { get; private set; }
    public string Draft { get => Role is { } role ? _drafts.GetValueOrDefault(role, "") : "";
        set { if (Role is { } role) _drafts[role] = value; } }
    public PendingSend? Pending => Role is { } role ? _pending.GetValueOrDefault(role) : null;
    public void Select(RoleKey? role) { Role = role; Generation++; Conversation = null; }
    public bool Apply(long generation, Conversation conversation)
    {
        if (generation != Generation || Role != new RoleKey(conversation.WorkspaceId, conversation.AgentId)) return false;
        // Session rotation may reset the cursor; identity must be checked before ordering.
        if (Conversation is { } old && old.MainSessionId == conversation.MainSessionId && old.EventCursor > conversation.EventCursor) return false;
        Conversation = conversation; return true;
    }
    public PendingSend Prepare(string session)
    {
        var role = Role ?? throw new InvalidOperationException("先选择角色。");
        if (_pending.TryGetValue(role, out var retry)) return retry;
        if (string.IsNullOrWhiteSpace(Draft)) throw new InvalidOperationException("请输入消息。");
        var send = PendingSend.Create(role, session, Draft);
        _pending.Add(role, send); return send;
    }
    public void Accept(PendingSend send)
    {
        if (!_pending.TryGetValue(send.Role, out var pending) || pending.ClientRequestId != send.ClientRequestId) return;
        _pending.Remove(send.Role);
        if (_drafts.GetValueOrDefault(send.Role) == send.Text) _drafts[send.Role] = "";
    }
    public static ProcessItem[] Ordered(IEnumerable<ProcessItem> items) =>
        items.DistinctBy(item => item.Id).OrderBy(item => item.Sequence).ToArray();
    public static string? ActiveTurn(Conversation? conversation) => conversation?.ActiveRun?.OutputSnapshot.Window?.TurnId
        ?? conversation?.ActiveRun?.OutputSnapshot.ProcessItems.LastOrDefault(item => !string.IsNullOrEmpty(item.TurnId))?.TurnId;
}
