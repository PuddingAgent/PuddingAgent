using System.Threading.Channels;

namespace PuddingCode.Services;

/// <summary>
/// JSONL 日志条目——对应一条聊天消息的简化快照，用于异步落盘为 JSONL 格式。
/// </summary>
public class JsonlEntry
{
    public string Type { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public string? UsageJson { get; set; }
    public string? ThinkingJson { get; set; }
    public string? AgentId { get; set; }
    public string BranchType { get; set; } = "MAIN";
    public long CreatedAt { get; set; }
}

/// <summary>
/// JSONL Session 写入器——将聊天消息异步序列化为 JSONL 文件，按 Session 分文件。
/// </summary>
public class JsonlSessionWriter : IAsyncDisposable
{
    private readonly Channel<JsonlEntry> _channel = Channel.CreateBounded<JsonlEntry>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    /// <summary>将条目加入写入队列。</summary>
    public void Enqueue(string sessionId, JsonlEntry entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    /// <summary>优雅关闭写入通道。</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await Task.CompletedTask;
    }
}

/// <summary>
/// JSONL Session 读取器——从 JSONL 文件读取历史会话消息。
/// </summary>
public class JsonlSessionReader
{
    /// <summary>从 JSONL 文件异步读取指定 Session 的全部历史消息条目。</summary>
    public Task<IReadOnlyList<JsonlEntry>> ReadSessionAsync(string sessionId, CancellationToken ct = default)
    {
        // 当前为占位实现，后续对接 JSONL 文件存储。
        return Task.FromResult<IReadOnlyList<JsonlEntry>>(Array.Empty<JsonlEntry>());
    }
}
