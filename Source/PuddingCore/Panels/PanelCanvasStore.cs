namespace PuddingCore.Panels;

/// <summary>
/// 面板画布的形态上限。
///
/// <para>
/// 与线缆契约 <c>DesktopPanelRenderRequest</c> 保持同值，但**职责不同**：契约层负责
/// 「拒绝越界请求并给出可读原因」，这里只做最后一道**防污染守卫** —— 避免一个坏 id 或
/// 超大内容长期占住进程内存。真正的规则真源仍是契约层，本类不重新定义策略。
/// </para>
/// </summary>
public static class PanelCanvasLimits
{
    /// <summary>与 <c>ToolTabIdentity.Panel</c> / 契约的字母数字点横线规则一致。</summary>
    public const int MaxPanelIdLength = 64;

    /// <summary>内容上限（字符数）。Agent 推送的是「现算现给」的媒体资源，不是大文件。</summary>
    public const int MaxContentChars = 256 * 1024;

    public const int MaxTitleLength = 200;
}

/// <summary>
/// 一块画布在某一时刻的**不可变内容快照**。
///
/// <para>
/// <see cref="Revision"/> 由存储单调递增（首次发布为 1）。面板端只需比较 revision 即可判断
/// 「内容是否变过」，不必比较全文 —— 这是增量刷新与轮询拉取的前提。
/// </para>
/// </summary>
/// <param name="PanelId">画布身份（保留首次发布时的书写形式）。</param>
/// <param name="Content">Agent 提供的自包含 HTML 片段或完整文档。</param>
/// <param name="Title">画布标题；空白归一为 <c>null</c>。</param>
/// <param name="Revision">单调递增的版本号，从 1 开始。</param>
/// <param name="UpdatedAt">发布时刻（UTC）。</param>
public sealed record PanelCanvasSnapshot(
    string PanelId,
    string Content,
    string? Title,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 面板内容的**单一真源**。
///
/// <para>
/// 为什么内容归 Core 而不是 Desktop：Agent 生成的东西应该<b>可寻址、可重放</b>。
/// 放在 Desktop 的内存里，重启即丢、且只有能力通道这一条路能写；放在 Core，
/// 面板变成一个「指向内容的视图」，传输方式可以换（HTTP 拉取 / 能力通道推送），
/// 而内容本身不受影响。
/// </para>
/// </summary>
public interface IPanelCanvasStore
{
    /// <summary>发布（新建或整幅替换）一块画布，返回新快照。revision 递增。</summary>
    /// <exception cref="ArgumentException"><paramref name="panelId"/> 形态非法或内容越界。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> 为 null。</exception>
    PanelCanvasSnapshot Publish(
        string panelId,
        string content,
        string? title = null,
        DateTimeOffset? now = null);

    /// <summary>按 id 取快照；不存在返回 null（大小写不敏感）。</summary>
    PanelCanvasSnapshot? Get(string panelId);

    /// <summary>列出所有画布，按 <see cref="PanelCanvasSnapshot.PanelId"/> 序数序排列。</summary>
    IReadOnlyList<PanelCanvasSnapshot> List();

    /// <summary>移除一块画布；返回是否真的存在。</summary>
    bool Remove(string panelId);
}

/// <summary>
/// 进程内实现。
///
/// <para>
/// **只活在 Core 进程内**：Core 重启后内容清空。这是刻意的第一版取舍 ——
/// 落盘会引入「文件在哪、多大、何时清理」的完整生命周期问题，而当前要解决的是
/// 「Agent 能不能方便地控制画布」，不是持久化。等有人真的需要「关掉应用再打开还在」时，
/// 换一个实现即可，接口不用动。
/// </para>
/// <para>
/// 线程安全：所有读写在一把锁内完成，保证 <see cref="PanelCanvasSnapshot.Revision"/> 严格单调
/// 且不重复 —— 面板端会拿它做「是否重新渲染」的判据，重复的 revision 会让刷新丢失。
/// </para>
/// </summary>
public sealed class InMemoryPanelCanvasStore : IPanelCanvasStore
{
    private readonly Dictionary<string, PanelCanvasSnapshot> _canvases =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

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
            // 同一块画布沿用**首次发布时的书写形式**，避免 "Main"/"main" 在返回体里来回跳。
            var exists = _canvases.TryGetValue(id, out var existing);
            var canonicalId = exists ? existing!.PanelId : id;
            var snapshot = new PanelCanvasSnapshot(
                canonicalId,
                content,
                normalizedTitle,
                exists ? existing!.Revision + 1 : 1,
                now ?? DateTimeOffset.UtcNow);

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
            return _canvases.Remove(panelId.Trim());
        }
    }

}

/// <summary>
/// 画布 id / 内容 / 标题的**共享校验**。
///
/// <para>
/// 进程内与落盘两个实现必须用同一套规则：否则「内存里能发布的 id，落盘时失败」会变成
/// 只在重启后才暴露的缺陷；反之「落盘能写、内存拒绝」更糟——内容会进到运行中的画布。
/// </para>
/// </summary>
internal static class PanelCanvasGuard
{
    /// <summary>
    /// 防污染守卫：只接受字母、数字与 <c>. _ -</c>。
    /// 这条规则与线缆契约同一个来源（画布 id 最终也是 Desktop 的 tab 身份），
    /// 在这里再挡一次是因为坏 id 会永久占住字典键。
    /// </summary>
    public static string RequirePanelId(string panelId)
    {
        // 不用 ArgumentException.ThrowIfNullOrWhiteSpace：它对 null/空串抛 ArgumentNullException、
        // 对纯空白抛 ArgumentException —— 同一个「调用方给错了 id」裂成两种类型，
        // 上层（HTTP 400）就必须知道这个差别。这里统一成 ArgumentException。
        if (string.IsNullOrWhiteSpace(panelId))
        {
            throw new ArgumentException("panelId is required.", nameof(panelId));
        }

        var trimmed = panelId.Trim();
        if (trimmed.Length > PanelCanvasLimits.MaxPanelIdLength)
        {
            throw new ArgumentException(
                $"panelId exceeds {PanelCanvasLimits.MaxPanelIdLength} characters.",
                nameof(panelId));
        }

        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
            {
                continue;
            }

            throw new ArgumentException(
                $"panelId contains an unsupported character: '{ch}'. Allowed: letters, digits, '.', '_', '-'.",
                nameof(panelId));
        }

        return trimmed;
    }

    /// <summary>内容非 null 且不超上限；返回原值便于链式调用。</summary>
    public static string RequireContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length > PanelCanvasLimits.MaxContentChars)
        {
            throw new ArgumentException(
                $"content exceeds {PanelCanvasLimits.MaxContentChars} characters.",
                nameof(content));
        }

        return content;
    }

    /// <summary>标题空白归一为 <c>null</c>（而不是空串），超长拒绝。</summary>
    public static string? NormalizeTitle(string? title)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (normalized is { Length: > PanelCanvasLimits.MaxTitleLength })
        {
            throw new ArgumentException(
                $"title exceeds {PanelCanvasLimits.MaxTitleLength} characters.",
                nameof(title));
        }

        return normalized;
    }
}
