namespace PuddingChat;

/// <summary>UI-owned selection epoch, per-role drafts and retry identity. No execution state machine.</summary>
public sealed class ChatSelection
{
    private readonly Dictionary<RoleKey, string> _drafts = [];
    private readonly Dictionary<RoleKey, PendingSend> _pending = [];
    private readonly Dictionary<RoleKey, List<AttachedImage>> _images = [];
    private readonly Dictionary<RoleKey, List<TextFileContext>> _files = [];
    public IReadOnlyList<TextFileContext> Files => Role is { } role ? FilesFor(role) : [];
    public IReadOnlyList<TextFileContext> FilesFor(RoleKey role) => _files.TryGetValue(role, out var files) ? files.ToArray() : [];
    public void AddFiles(RoleKey role, IReadOnlyList<TextFileContext> files)
    {
        var merged = FilesFor(role).Concat(files).ToArray();
        TextFileContexts.Validate(merged);
        TextFileContexts.Compose(_drafts.GetValueOrDefault(role, ""), merged);
        _files[role] = merged.ToList();
    }
    public void RemoveFile(string id)
    { if (Role is { } role && _files.TryGetValue(role, out var files)) files.RemoveAll(f => f.Id == id); }
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
    private bool _historyExpanded;
    public string Draft { get => Role is { } role ? _drafts.GetValueOrDefault(role, "") : "";
        set { if (Role is { } role) _drafts[role] = value; } }
    public PendingSend? Pending => Role is { } role ? _pending.GetValueOrDefault(role) : null;
    public void Select(RoleKey? role) { Role = role; Generation++; Conversation = null; _historyExpanded = false; }
    public void Clear() { Select(null); _drafts.Clear(); _pending.Clear(); _images.Clear(); _files.Clear(); }
    public bool Apply(long generation, Conversation conversation)
    {
        if (generation != Generation || Role != new RoleKey(conversation.WorkspaceId, conversation.AgentId)) return false;
        // Session rotation may reset the cursor; identity must be checked before ordering.
        if (Conversation is { } old && old.MainSessionId == conversation.MainSessionId && old.EventCursor > conversation.EventCursor) return false;
        if (Conversation is { } previous && previous.MainSessionId == conversation.MainSessionId && _historyExpanded)
        {
            var known = previous.Messages.Select(m => m.CanonicalMessageId ?? m.MessageId).ToHashSet(StringComparer.Ordinal);
            var overlaps = conversation.Messages.Any(m => known.Contains(m.CanonicalMessageId ?? m.MessageId));
            // A burst larger than the latest page can leave a gap. Keep a cursor that can fill it.
            conversation = conversation with { Messages = MergeMessages(previous.Messages, conversation.Messages),
                OlderCursor = overlaps || conversation.Messages.Length == 0 ? previous.OlderCursor : conversation.OlderCursor };
        }
        else _historyExpanded = false;
        Conversation = conversation; return true;
    }
    public bool PrependHistory(long generation, HistoryPage page)
    {
        if (generation != Generation || Conversation is not { } current || current.MainSessionId != page.MainSessionId
            || current.OlderCursor != page.Before || (page.Next is not null && page.Next.CompareTo(page.Before) >= 0)) return false;
        Conversation = current with { Messages = MergeMessages(page.Messages, current.Messages), OlderCursor = page.Next };
        _historyExpanded = true; return true;
    }
    private static ChatMessage[] MergeMessages(IEnumerable<ChatMessage> older, IEnumerable<ChatMessage> current)
        => older.Concat(current).GroupBy(m => m.CanonicalMessageId ?? m.MessageId, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(m => m.CreatedAt).ToArray();
    public PendingSend Prepare(string session, string? capturedDraft = null, IReadOnlyList<AttachedImage>? capturedImages = null, IReadOnlyList<TextFileContext>? capturedFiles = null)
    {
        var role = Role ?? throw new InvalidOperationException("先选择角色。");
        if (_pending.TryGetValue(role, out var retry)) return retry;
        var text = capturedDraft ?? Draft;
        var images = capturedImages ?? Images;
        var files = capturedFiles ?? Files;
        TextFileContexts.Validate(files);
        TextFileContexts.Compose(text, files);
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0 && files.Count == 0) throw new InvalidOperationException("请输入消息或添加附件。");
        var send = PendingSend.Create(role, session, text, images, files);
        _pending.Add(role, send); return send;
    }
    public void Accept(PendingSend send)
    {
        if (!_pending.TryGetValue(send.Role, out var pending) || pending.ClientRequestId != send.ClientRequestId) return;
        _pending.Remove(send.Role);
        if (_files.TryGetValue(send.Role, out var files))
        {
            var acceptedFiles = (send.Files ?? []).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            files.RemoveAll(f => acceptedFiles.Contains(f.Id));
        }
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
