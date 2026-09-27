namespace PuddingChat;

/// <summary>UI-owned selection epoch, per-role drafts and retry identity. No execution state machine.</summary>
public sealed class ChatSelection
{
    private readonly Dictionary<RoleKey, string> _drafts = [];
    private readonly Dictionary<RoleKey, PendingSend> _pending = [];
    private readonly Dictionary<RoleKey, List<AttachedImage>> _images = [];
    public IReadOnlyList<AttachedImage> Images => Role is { } role ? ImagesFor(role) : [];
    public IReadOnlyList<AttachedImage> ImagesFor(RoleKey role) => _images.TryGetValue(role, out var images) ? images.ToArray() : [];
    public void AddImage(RoleKey role, AttachedImage image)
    {
        if (!_images.TryGetValue(role, out var images)) _images[role] = images = [];
        if (!images.Any(i => i.ArtifactId == image.ArtifactId)) images.Add(image);
    }
    public void RemoveImage(string artifactId)
    { if (Role is { } role && _images.TryGetValue(role, out var images)) images.RemoveAll(i => i.ArtifactId == artifactId); }
    public RoleKey? Role { get; private set; }
    public long Generation { get; private set; }
    public Conversation? Conversation { get; private set; }
    public string Draft { get => Role is { } role ? _drafts.GetValueOrDefault(role, "") : "";
        set { if (Role is { } role) _drafts[role] = value; } }
    public PendingSend? Pending => Role is { } role ? _pending.GetValueOrDefault(role) : null;
    public void Select(RoleKey? role) { Role = role; Generation++; Conversation = null; }
    public void Clear() { Select(null); _drafts.Clear(); _pending.Clear(); _images.Clear(); }
    public bool Apply(long generation, Conversation conversation)
    {
        if (generation != Generation || Role != new RoleKey(conversation.WorkspaceId, conversation.AgentId)) return false;
        // Session rotation may reset the cursor; identity must be checked before ordering.
        if (Conversation is { } old && old.MainSessionId == conversation.MainSessionId && old.EventCursor > conversation.EventCursor) return false;
        Conversation = conversation; return true;
    }
    public PendingSend Prepare(string session, string? capturedDraft = null, IReadOnlyList<AttachedImage>? capturedImages = null)
    {
        var role = Role ?? throw new InvalidOperationException("先选择角色。");
        if (_pending.TryGetValue(role, out var retry)) return retry;
        var text = capturedDraft ?? Draft;
        var images = capturedImages ?? Images;
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) throw new InvalidOperationException("请输入消息或添加图片。");
        var send = PendingSend.Create(role, session, text, images);
        _pending.Add(role, send); return send;
    }
    public void Accept(PendingSend send)
    {
        if (!_pending.TryGetValue(send.Role, out var pending) || pending.ClientRequestId != send.ClientRequestId) return;
        _pending.Remove(send.Role);
        if (_images.TryGetValue(send.Role, out var images))
        {
            var accepted = (send.Images ?? []).Select(i => i.ArtifactId).ToHashSet(StringComparer.Ordinal);
            images.RemoveAll(i => accepted.Contains(i.ArtifactId));
        }
        if (_drafts.GetValueOrDefault(send.Role) == send.Text) _drafts[send.Role] = "";
    }
    public void Reject(PendingSend send)
    {
        if (_pending.GetValueOrDefault(send.Role)?.ClientRequestId == send.ClientRequestId) _pending.Remove(send.Role);
    }
    public static ProcessItem[] Ordered(IEnumerable<ProcessItem> items) =>
        items.DistinctBy(item => item.Id).OrderBy(item => item.Sequence).ToArray();
    public static string? ActiveTurn(Conversation? conversation) => conversation?.ActiveRun?.OutputSnapshot.Window?.TurnId
        ?? conversation?.ActiveRun?.OutputSnapshot.ProcessItems.LastOrDefault(item => !string.IsNullOrEmpty(item.TurnId))?.TurnId;
}
