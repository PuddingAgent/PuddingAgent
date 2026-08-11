using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>用户偏好写入结果。</summary>
public sealed record PreferenceWriteResult(
    string Key,
    string Value,
    string BookId,
    string ChapterId,
    bool Updated);

/// <summary>
/// 用户偏好管理服务——统一用户偏好的存取边界。
///
/// 存储：记忆图书馆中的「用户偏好」Book（EnsureDefaultBooksAsync 已内置该默认书）。
/// 每条偏好一个 Chapter，Content 格式为 "key: value"，按 key 幂等 upsert（同一 key 重复写入更新原章节）。
/// 检索：按 Book Title 精确读取（不依赖 FTS 或 Embedding，冷启动零外部依赖），
/// 无「用户偏好」Book 时回退到 SmartSearchAsync（复用 FTS5 + TagTree 检索）。
///
/// 设计原则：复用 IMemoryLibrary / IMemoryLibraryConvenience，不重复建设存储或检索设施；
/// 向量化由既有 EmbeddingGenerationHook 异步回填，Prefetch 本身不阻塞等待 Embedding。
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>偏好 Book 的标准标题（与 EnsureDefaultBooksAsync 内置书名一致）。</summary>
    const string PreferenceBookTitle = "用户偏好";

    /// <summary>
    /// 从记忆库加载用户偏好，返回格式化文本块（含 "--- LAYER: USER-PREFERENCES ---" 标记）；
    /// 无偏好或 workspace 缺失时返回 null。
    /// </summary>
    Task<string?> LoadPreferencesAsync(
        string? workspaceId,
        int maxItems = 20,
        CancellationToken ct = default);

    /// <summary>
    /// 存储一条用户偏好（upsert 语义：同一 key 覆盖旧值，不同 key 追加）。
    /// </summary>
    Task<PreferenceWriteResult> SavePreferenceAsync(
        string workspaceId,
        string key,
        string value,
        string? sourceSessionId = null,
        string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>删除一条用户偏好（按 key 精确匹配）。返回是否删除成功。</summary>
    Task<bool> DeletePreferenceAsync(
        string workspaceId,
        string key,
        CancellationToken ct = default);
}

/// <summary>用户偏好管理服务实现。见 <see cref="IUserPreferenceService"/> 注释。</summary>
public sealed class UserPreferenceService : IUserPreferenceService
{
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly IMemoryLibraryConvenience _libraryConvenience;
    private readonly ILogger<UserPreferenceService> _logger;

    public UserPreferenceService(
        IMemoryLibrary memoryLibrary,
        IMemoryLibraryConvenience libraryConvenience,
        ILogger<UserPreferenceService> logger)
    {
        _memoryLibrary = memoryLibrary;
        _libraryConvenience = libraryConvenience;
        _logger = logger;
    }

    public async Task<string?> LoadPreferencesAsync(
        string? workspaceId,
        int maxItems = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        try
        {
            // ── 主路径：精确读取「用户偏好」Book（零外部依赖，确定性最高）──
            var book = await FindPreferenceBookAsync(workspaceId, ct);
            if (book is not null)
            {
                var chapters = await _memoryLibrary.ListChaptersAsync(book.BookId, ct);
                var active = chapters
                    .Where(c => string.Equals(c.Status, "active", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.ChapterOrder)
                    .ThenByDescending(c => c.UpdatedAt)
                    .Take(maxItems)
                    .ToList();

                if (active.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("--- LAYER: USER-PREFERENCES ---");
                    sb.AppendLine("[USER PREFERENCES]");
                    foreach (var chapter in active)
                    {
                        var line = FormatPreferenceLine(chapter.Title, chapter.Content);
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine(line);
                    }
                    return sb.ToString();
                }
            }

            // ── 回退路径：FTS5 + TagTree 检索（覆盖 save_memory type=preference 等历史条目）──
            var fallback = await _libraryConvenience.SmartSearchAsync(
                $"{IUserPreferenceService.PreferenceBookTitle} preference 偏好",
                topK: Math.Max(1, maxItems),
                ct);
            if (fallback.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("--- LAYER: USER-PREFERENCES ---");
                sb.AppendLine("[USER PREFERENCES]");
                foreach (var r in fallback.Take(maxItems))
                {
                    var snippet = r.Snippet?.Trim();
                    if (string.IsNullOrWhiteSpace(snippet))
                        snippet = r.ChapterTitle ?? r.BookTitle;
                    sb.AppendLine($"- **{r.BookTitle}**: {snippet}");
                }
                return sb.ToString();
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[UserPreference] Load failed workspace={Workspace}",
                workspaceId);
            return null;
        }
    }

    public async Task<PreferenceWriteResult> SavePreferenceAsync(
        string workspaceId,
        string key,
        string value,
        string? sourceSessionId = null,
        string? agentInstanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("value is required.", nameof(value));

        var book = await GetOrCreatePreferenceBookAsync(workspaceId, ct);
        var content = $"{key}: {value}";

        var chapters = await _memoryLibrary.ListChaptersAsync(book.BookId, ct);
        var existing = chapters.FirstOrDefault(c =>
            string.Equals(c.Status, "active", StringComparison.OrdinalIgnoreCase)
            && ContentMatchesKey(c.Content, key));

        if (existing is not null)
        {
            // 同一 key 幂等更新：复用原 Chapter，避免重复堆积
            var updated = await _memoryLibrary.UpdateChapterContentAsync(existing.ChapterId, content, ct);
            _logger.LogInformation(
                "[UserPreference] Updated key={Key} book={Book} chapter={Chapter}",
                key, book.Title, updated.ChapterId);
            return new PreferenceWriteResult(key, value, book.BookId, updated.ChapterId, Updated: true);
        }

        var order = chapters.Count > 0 ? chapters.Max(c => c.ChapterOrder) + 1 : 0;
        var chapter = await _memoryLibrary.AddChapterAsync(
            book.BookId,
            IUserPreferenceService.PreferenceBookTitle,
            content,
            order,
            sourceSessionId,
            agentInstanceId,
            ct);
        await _memoryLibrary.UpdateChapterImportanceAsync(chapter.ChapterId, 0.9, ct);

        _logger.LogInformation(
            "[UserPreference] Saved key={Key} book={Book} chapter={Chapter}",
            key, book.Title, chapter.ChapterId);
        return new PreferenceWriteResult(key, value, book.BookId, chapter.ChapterId, Updated: false);
    }

    public async Task<bool> DeletePreferenceAsync(
        string workspaceId,
        string key,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(key))
            return false;

        var book = await FindPreferenceBookAsync(workspaceId, ct);
        if (book is null)
            return false;

        var chapters = await _memoryLibrary.ListChaptersAsync(book.BookId, ct);
        var existing = chapters.FirstOrDefault(c =>
            string.Equals(c.Status, "active", StringComparison.OrdinalIgnoreCase)
            && ContentMatchesKey(c.Content, key));
        if (existing is null)
            return false;

        await _memoryLibrary.DeleteChapterAsync(existing.ChapterId, ct);
        _logger.LogInformation("[UserPreference] Deleted key={Key} book={Book}", key, book.Title);
        return true;
    }

    // ── 私有辅助 ─────────────────────────────────────────────────────

    private async Task<BookRecord?> FindPreferenceBookAsync(string workspaceId, CancellationToken ct)
    {
        var libraries = await _memoryLibrary.ListLibrariesAsync(workspaceId, ct);
        if (libraries.Count == 0)
            return null;

        foreach (var lib in libraries)
        {
            var book = await _memoryLibrary.FindBookByTitleAsync(
                lib.LibraryId, IUserPreferenceService.PreferenceBookTitle, ct);
            if (book is not null
                && string.Equals(book.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return book;
            }
        }
        return null;
    }

    private async Task<BookRecord> GetOrCreatePreferenceBookAsync(string workspaceId, CancellationToken ct)
    {
        var existing = await FindPreferenceBookAsync(workspaceId, ct);
        if (existing is not null)
            return existing;

        var libraries = await _memoryLibrary.ListLibrariesAsync(workspaceId, ct);
        if (libraries.Count == 0)
        {
            var lib = await _memoryLibrary.CreateLibraryAsync(
                workspaceId, "默认图书馆", null, ct);
            libraries = [lib];
        }

        var book = await _memoryLibrary.CreateBookAsync(
            libraries[0].LibraryId,
            IUserPreferenceService.PreferenceBookTitle,
            "偏好、习惯、风格、沟通方式",
            ["偏好"],
            ct);
        _logger.LogInformation(
            "[UserPreference] Created preference book={Book} workspace={Workspace}",
            book.BookId, workspaceId);
        return book;
    }

    /// <summary>判断 Chapter Content 是否对应指定 key（"key: value" 格式，冒号后可能有空格）。</summary>
    private static bool ContentMatchesKey(string? content, string key)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(key))
            return false;

        // 容忍 "key: value" / "key：value"（全角冒号）两种写法
        foreach (var separator in new[] { ": ", "：", ":", "：" })
        {
            var prefix = key + separator;
            if (content.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return string.Equals(content.Trim(), key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>将 Chapter 格式化为 "**key**: value" 行；解析失败时降级为原始内容。</summary>
    private static string FormatPreferenceLine(string? chapterTitle, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        foreach (var separator in new[] { ": ", "：", ":", "：" })
        {
            var idx = content.IndexOf(separator, StringComparison.Ordinal);
            if (idx > 0)
            {
                var key = content[..idx].Trim();
                var value = content[(idx + separator.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    return $"- **{key}**: {value}";
            }
        }

        // 非 "key: value" 格式（如历史 free-form 偏好）→ 使用 Chapter Title 作为键
        if (!string.IsNullOrWhiteSpace(chapterTitle))
            return $"- **{chapterTitle}**: {content.Trim()}";
        return $"- {content.Trim()}";
    }
}
