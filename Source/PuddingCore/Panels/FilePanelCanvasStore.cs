namespace PuddingCore.Panels;

using System.Text.Json;

/// <summary>
/// 落盘实现：每块画布一个 JSON 文件，Core 重启后内容仍在。
///
/// <para>
/// 为什么需要它：进程内实现重启即清空。对「24 小时长程助手」来说这很刺眼 ——
/// Agent 昨晚推的图，今天打开应用就没了，而且没有任何迹象说明「曾经有过」。
/// 画布既然被定义成「可寻址、可重放的资源」，就该活过进程重启。
/// </para>
/// <para>
/// 一致性策略：**先写盘、后改内存**。反过来的话，写盘失败时用户已经看到新内容，
/// 而磁盘仍是旧的；重启后画布会莫名回退到上一版，且无从解释。
/// 删除同理：文件删不掉就不改内存（宁可失败，也不要「内存没了、磁盘还有」）。
/// </para>
/// <para>
/// 单个损坏文件不会让整块画布不可用：加载时跳过并计入 <see cref="SkippedFileCount"/>，
/// 其余照常可用。坏文件保留在磁盘上，便于事后排查而不是被静默清理。
/// </para>
/// </summary>
public sealed class FilePanelCanvasStore : IPanelCanvasStore
{
    private const string FilePrefix = "panel-";
    private const string FileSuffix = ".json";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _root;
    private readonly Dictionary<string, PanelCanvasSnapshot> _canvases =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    /// <param name="root">画布目录（不存在会被创建）。</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> 为空。</exception>
    public FilePanelCanvasStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("root is required.", nameof(root));
        }

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        LoadExisting();
    }

    /// <summary>
    /// 加载时被跳过的文件数（损坏的 JSON、缺字段、读不了）。只用于诊断与测试，
    /// 不参与任何业务判断 —— 它**不是**健康指标，只是「有东西没读进来」的计数。
    /// </summary>
    public int SkippedFileCount { get; private set; }

    public PanelCanvasSnapshot Publish(
        string panelId,
        string content,
        string? title = null,
        DateTimeOffset? now = null)
    {
        var id = PanelCanvasGuard.RequirePanelId(panelId);
        PanelCanvasGuard.RequireContent(content);
        var normalizedTitle = PanelCanvasGuard.NormalizeTitle(title);

        lock (_gate)
        {
            var exists = _canvases.TryGetValue(id, out var existing);
            var canonicalId = exists ? existing!.PanelId : id;
            var snapshot = new PanelCanvasSnapshot(
                canonicalId,
                content,
                normalizedTitle,
                exists ? existing!.Revision + 1 : 1,
                now ?? DateTimeOffset.UtcNow);

            WriteSnapshot(canonicalId, snapshot);
            _canvases[id] = snapshot;
            return snapshot;
        }
    }

    public PanelCanvasSnapshot? Get(string panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId))
        {
            return null;
        }

        lock (_gate)
        {
            return _canvases.TryGetValue(panelId.Trim(), out var snapshot) ? snapshot : null;
        }
    }

    public IReadOnlyList<PanelCanvasSnapshot> List()
    {
        lock (_gate)
        {
            return _canvases.Values
                .OrderBy(snapshot => snapshot.PanelId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool Remove(string panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_canvases.TryGetValue(panelId.Trim(), out var existing))
            {
                return false;
            }

            var path = PathFor(existing.PanelId);
            if (File.Exists(path))
            {
                // 删不掉就让异常冒出去：此时内存尚未改动，状态仍与磁盘一致。
                File.Delete(path);
            }

            _canvases.Remove(panelId.Trim());
            return true;
        }
    }

    /// <summary>
    /// 原子写：先写 <c>.tmp</c> 再覆盖目标。直接写目标文件时，进程被杀会留下半截 JSON，
    /// 下一个启动周期就会把它当作损坏文件跳过 —— 内容等于丢了。
    /// </summary>
    private void WriteSnapshot(string panelId, PanelCanvasSnapshot snapshot)
    {
        var path = PathFor(panelId);
        var temp = path + TempSuffix;
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// panelId → 文件名。
    ///
    /// 安全性：id 规则允许 <c>.</c>，所以 <c>..</c> 能通过校验；直接拿它当文件名会指向父目录
    /// （变异实验证实：去掉前后缀后 <c>Dot_dot_id_cannot_escape_the_root</c> 立即变红）。
    /// 后缀 <c>.json</c> 已足以让 <c>..</c> 退化成普通文件名；前缀再加一层，同时也让
    /// 「哪些文件是本 store 的」能用 glob 精确识别 —— 加载时不会误碰同目录的其他 JSON。
    /// </summary>
    private string PathFor(string panelId) => Path.Combine(_root, FilePrefix + panelId + FileSuffix);

    private void LoadExisting()
    {
        foreach (var file in Directory.EnumerateFiles(_root, FilePrefix + "*" + FileSuffix))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<PanelCanvasSnapshot>(
                    File.ReadAllText(file), JsonOptions);

                // 缺字段/空内容的快照直接跳过：把 null 塞进字典会让 Get 返回一个看似有效的对象。
                if (snapshot is null
                    || string.IsNullOrWhiteSpace(snapshot.PanelId)
                    || snapshot.Content is null
                    || snapshot.Revision <= 0)
                {
                    SkippedFileCount++;
                    continue;
                }

                _canvases[snapshot.PanelId] = snapshot;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                SkippedFileCount++;
            }
        }
    }
}
