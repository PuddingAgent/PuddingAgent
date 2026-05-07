using System.Collections.Concurrent;
using System.Text;

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
/// JSONL Session 写入器——将聊天消息直接同步写入 JSONL 文件，按 Session 分文件。
/// 使用 ConcurrentDictionary 管理每个 Session 文件的写入锁，保证并发安全。
/// </summary>
public class JsonlSessionWriter : IAsyncDisposable
{
    private readonly string _baseDir;
    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
    private readonly ConcurrentDictionary<string, object> _fileLocks = new();

    /// <summary>创建写入器，指定 JSONL 文件存储目录。</summary>
    public JsonlSessionWriter(string baseDir = "data/jsonl")
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    /// <summary>将条目直接同步追加写入对应的 Session JSONL 文件。</summary>
    public void Enqueue(string sessionId, JsonlEntry entry)
    {
        var filePath = Path.Combine(_baseDir, $"{entry.SessionId}.jsonl");
        var line = System.Text.Json.JsonSerializer.Serialize(entry, _jsonOptions);
        var sessionLock = _fileLocks.GetOrAdd(entry.SessionId, _ => new object());
        lock (sessionLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    /// <summary>同步写入模式下无需刷新，空操作。</summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>释放资源。</summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// JSONL Session 读取器——从 JSONL 文件读取历史会话消息。
/// </summary>
public class JsonlSessionReader
{
    private readonly string _baseDir;
    private static readonly System.Text.Json.JsonSerializerOptions _readOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    /// <summary>创建读取器，指定 JSONL 文件存储目录。</summary>
    public JsonlSessionReader(string baseDir = "data/jsonl")
    {
        _baseDir = baseDir;
    }

    /// <summary>从 JSONL 文件异步读取指定 Session 的全部历史消息条目。</summary>
    public Task<IReadOnlyList<JsonlEntry>> ReadSessionAsync(string sessionId, CancellationToken ct = default)
    {
        // 尝试从文件读取，不存在则返回空
        var filePath = Path.Combine(_baseDir, $"{sessionId}.jsonl");
        if (!File.Exists(filePath))
            return Task.FromResult<IReadOnlyList<JsonlEntry>>(Array.Empty<JsonlEntry>());

        var entries = new List<JsonlEntry>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = System.Text.Json.JsonSerializer.Deserialize<JsonlEntry>(line, _readOptions);
                if (entry is not null) entries.Add(entry);
            }
            catch
            {
                // 跳过格式错误的行
            }
        }
        return Task.FromResult<IReadOnlyList<JsonlEntry>>(entries);
    }
}
