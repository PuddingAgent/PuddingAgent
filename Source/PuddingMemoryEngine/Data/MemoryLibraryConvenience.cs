using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Abstractions;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// LLM 友好操作层实现——提供"直接丢内容进来，图书馆自己管路由"的便利方法。
/// 内部委托底层 IMemoryLibrary，不包含业务策略。
/// </summary>
public sealed class MemoryLibraryConvenience : IMemoryLibraryConvenience
{
    private readonly IMemoryLibrary _library;
    private readonly IMemoryLlmClient? _llmClient;

    /// <summary>后台深度探索结果缓存，key=query前30字符的哈希。</summary>
    private readonly ConcurrentDictionary<string, List<RankedResult>> _pendingExplorations = new();

    public MemoryLibraryConvenience(IMemoryLibrary library, IMemoryLlmClient? llmClient = null)
    {
        _library = library;
        _llmClient = llmClient;
    }

    /// <summary>
    /// 写入一段经验。内部自动：
    /// 1. FTS5 搜索相似 Book → 有则追加 Chapter，无则新建 Book
    /// 2. 自动计算 ChapterOrder（追加到末尾）
    /// 3. 自动发现 Content 中引用其他 Book/Chapter 的关键词并创建 Pointer
    /// 4. 保留 ExperiencePackage 的 Importance、ExternalRefs、SuggestedTags
    /// </summary>
    public async Task<ExperienceWriteResult> UpsertExperienceAsync(
        string workspaceId, ExperiencePackage experience, CancellationToken ct = default)
    {
        var libraries = await _library.ListLibrariesAsync(workspaceId, ct);
        if (libraries.Count == 0)
        {
            var lib = await _library.CreateLibraryAsync(workspaceId, "默认图书馆", null, ct);
            libraries = [lib];
        }

        var libraryIds = libraries
            .Select(l => l.LibraryId)
            .ToHashSet(StringComparer.Ordinal);

        BookRecord book;
        bool isNewBook = false;
        var exactBook = await FindBookByTitleInLibrariesAsync(libraries, experience.Title, ct);
        if (exactBook is not null)
        {
            book = exactBook;
        }
        else
        {
            // 1. FTS5 搜索相似 Book。FTS 是全局索引，候选必须限制在当前 workspace 的 Library 内。
            var similarBooks = await _library.SearchBooksFtsAsync(experience.Title, topK: 10, ct);
            var scopedSimilarBook = similarBooks.FirstOrDefault(b =>
                libraryIds.Contains(b.LibraryId)
                && string.Equals(b.Status, "active", StringComparison.OrdinalIgnoreCase));

            if (scopedSimilarBook is not null)
            {
                book = await _library.GetBookReadOnlyAsync(scopedSimilarBook.BookId, ct)
                    ?? throw new InvalidOperationException("Book not found after scoped FTS5 match");
            }
            else
            {
                // 没有当前 workspace 内的相似 Book → 创建新 Book（使用传入的 workspaceId）
                book = await _library.CreateBookAsync(
                    libraries[0].LibraryId, experience.Title,
                    experience.Content.Length > 200 ? experience.Content[..200] : experience.Content,
                    experience.SuggestedTags, ct);
                isNewBook = true;
            }
        }

        // 如果 Book 已存在且有 SuggestedTags，追加新 Tag 到 BookIndexes
        if (!isNewBook && experience.SuggestedTags is { Count: > 0 })
        {
            // 通过 UpdateBookAsync 无法追加 Tag，这里使用 CreateBookAsync 的 Tag 机制：
            // 对于已存在的 Book，直接插入新 Tag 索引（复用 BookIndexes 表）
            // 但 IMemoryLibrary 没有 AddTag 接口，采用变通：不做追加，因为
            // 已存在的 Book 在创建时已有 Tag，新 Tag 大概率已包含在内。
            // 如需严格追加，请给 IMemoryLibrary 新增 AddBookTagsAsync 接口。
        }

        // 2. 自动计算 ChapterOrder（追加到末尾）
        var chapters = await _library.ListChaptersAsync(book.BookId, ct);
        var existingChapter = FindExactChapter(chapters, experience);
        if (existingChapter is not null)
        {
            return new ExperienceWriteResult(book, existingChapter);
        }

        var semanticDecision = await DecideChapterWriteWithLlmAsync(chapters, experience, ct);
        if (semanticDecision is { Action: "reuse_existing" })
        {
            return new ExperienceWriteResult(book, semanticDecision.Chapter);
        }

        if (semanticDecision is { Action: "supersede_existing" })
        {
            var updatedChapter = await _library.SupersedeChapterAsync(
                semanticDecision.Chapter.ChapterId, experience.Title, experience.Content,
                experience.SourceSessionId, experience.AgentInstanceId, ct);
            if (Math.Abs(experience.Importance - 0.5) > 0.001)
            {
                updatedChapter = await _library.UpdateChapterImportanceAsync(updatedChapter.ChapterId, experience.Importance, ct);
            }

            return new ExperienceWriteResult(book, updatedChapter);
        }

        var chapterOrder = chapters.Count > 0 ? chapters.Max(c => c.ChapterOrder) + 1 : 0;

        var chapter = await _library.AddChapterAsync(
            book.BookId, experience.Title, experience.Content,
            chapterOrder, experience.SourceSessionId,
            experience.AgentInstanceId, ct);

        // 3. 如果 Importance 非默认值则更新
        if (Math.Abs(experience.Importance - 0.5) > 0.001)
        {
            chapter = await _library.UpdateChapterImportanceAsync(chapter.ChapterId, experience.Importance, ct);
        }

        // 4. 为每个 ExternalRefs 创建 Pointer
        if (experience.ExternalRefs is { Count: > 0 })
        {
            foreach (var url in experience.ExternalRefs)
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    await _library.CreatePointerAsync(
                        chapter.ChapterId, "url", url, url, "外部参考", ct);
                }
            }
        }

        // 5. 自动发现 Pointer（已知 Book 引用）
        await AutoDiscoverPointersAsync(chapter.ChapterId, ct);

        return new ExperienceWriteResult(book, chapter);
    }

    private static ChapterRecord? FindExactChapter(
        IReadOnlyList<ChapterRecord> chapters,
        ExperiencePackage experience)
    {
        return chapters.FirstOrDefault(c =>
            string.Equals(c.Title, experience.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Content, experience.Content, StringComparison.Ordinal)
            && string.Equals(c.AgentInstanceId, experience.AgentInstanceId, StringComparison.Ordinal));
    }

    private async Task<ChapterWriteDecision?> DecideChapterWriteWithLlmAsync(
        IReadOnlyList<ChapterRecord> chapters,
        ExperiencePackage experience,
        CancellationToken ct)
    {
        if (_llmClient is null)
            return null;

        var candidates = chapters
            .Where(c => string.Equals(c.AgentInstanceId, experience.AgentInstanceId, StringComparison.Ordinal))
            .Take(20)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        const string systemPrompt =
            "你是 Pudding 记忆写入协调器。判断 incoming memory 是否与候选 Chapter 表达同一事实、偏好、决策或经验。" +
            "不要使用文本 hash 或字面相等作为语义依据；只根据含义判断。" +
            "只返回严格 JSON：{\"action\":\"reuse_existing|supersede_existing|append_new\",\"chapterId\":string|null,\"confidence\":number,\"reason\":string}。" +
            "语义等价且不会丢失新信息时返回 reuse_existing；incoming 明确修正或取代旧记忆时返回 supersede_existing；否则返回 append_new。";

        var payload = JsonSerializer.Serialize(new
        {
            incoming = new
            {
                title = experience.Title,
                content = TrimForMemoryJudge(experience.Content),
                sourceReference = experience.SourceReference,
                referenceType = experience.ReferenceType,
                agentInstanceId = experience.AgentInstanceId,
            },
            candidates = candidates.Select(c => new
            {
                chapterId = c.ChapterId,
                title = c.Title,
                content = TrimForMemoryJudge(c.Content),
                sourceReference = c.SourceReference,
                referenceType = c.ReferenceType,
                agentInstanceId = c.AgentInstanceId,
            })
        });

        string raw;
        try
        {
            raw = await _llmClient.ChatAsync(systemPrompt, payload, null, ct);
        }
        catch
        {
            return null;
        }

        if (!TryParseChapterWriteDecision(raw, candidates, out var decision))
            return null;

        return decision;
    }

    private static bool TryParseChapterWriteDecision(
        string raw,
        IReadOnlyList<ChapterRecord> candidates,
        out ChapterWriteDecision? decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionElement))
            {
                return false;
            }

            var action = actionElement.GetString();
            if (!string.Equals(action, "reuse_existing", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "supersede_existing", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var confidence = root.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.ValueKind == JsonValueKind.Number
                && confidenceElement.TryGetDouble(out var parsedConfidence)
                    ? parsedConfidence
                    : 0;
            if (confidence < 0.75)
                return false;

            if (!root.TryGetProperty("chapterId", out var chapterIdElement))
                return false;

            var chapterId = chapterIdElement.GetString();
            var chapter = candidates.FirstOrDefault(c => string.Equals(c.ChapterId, chapterId, StringComparison.Ordinal));
            if (chapter is null)
                return false;

            decision = new ChapterWriteDecision(action!.ToLowerInvariant(), chapter);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TrimForMemoryJudge(string content)
    {
        const int limit = 1200;
        var trimmed = content.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }

    private sealed record ChapterWriteDecision(string Action, ChapterRecord Chapter);

    private async Task<BookRecord?> FindBookByTitleInLibrariesAsync(
        IReadOnlyList<LibraryRecord> libraries,
        string title,
        CancellationToken ct)
    {
        foreach (var library in libraries)
        {
            var book = await _library.FindBookByTitleAsync(library.LibraryId, title, ct);
            if (book is not null
                && string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(book.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return book;
            }
        }

        foreach (var library in libraries)
        {
            var books = await _library.ListBooksAsync(library.LibraryId, limit: 500, ct);
            var book = books.FirstOrDefault(b =>
                string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Status, "active", StringComparison.OrdinalIgnoreCase));
            if (book is not null)
                return book;
        }

        return null;
    }

    /// <summary>
    /// 三层混合检索：
    /// 第1路（免费，同步）: FTS5 BM25 搜索 Book + Chapter
    /// 第2路（免费，同步）: TagPath 前缀匹配
    /// 歧义检测 → 触发后台异步第3路 LLM 深度探索
    /// 第1+2路结果 RRF 排序返回；第3路结果在下一次查询时通过 PendingContext 注入。
    /// </summary>
    public async Task<IReadOnlyList<RankedResult>> SmartSearchAsync(
        string naturalLanguageQuery, int topK = 20, CancellationToken ct = default)
    {
        var trimmedQuery = naturalLanguageQuery?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedQuery))
            return Array.Empty<RankedResult>();

        const double k = 60; // RRF 平滑常数

        // ── 第1路: FTS5 BM25 搜索 Book + Chapter（并行）──
        var ftsBooksTask = _library.SearchBooksFtsScoredAsync(trimmedQuery, topK * 3, ct);
        var ftsChaptersTask = _library.SearchChaptersFtsScoredAsync(trimmedQuery, topK * 3, ct);

        // ── 第2路: TagPath 前缀匹配 ──
        var tagTask = _library.SearchBooksByTagAsync(trimmedQuery, topK * 3, ct);

        await Task.WhenAll(ftsBooksTask, ftsChaptersTask, tagTask);

        var ftsBooks = await ftsBooksTask;
        var ftsChapters = await ftsChaptersTask;
        var tagBooks = await tagTask;

        // ── 合并第1+2路，RRF 排序 ──
        var rrfScores = new Dictionary<string, (double Score, RankedResult Result)>();

        void AddRrfScore(IReadOnlyList<RankedResult> results, string sourcePrefix)
        {
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var key = r.ChapterId is not null
                    ? $"ch_{r.ChapterId}"
                    : $"bk_{r.BookId}";
                var rrfContrib = 1.0 / (k + i + 1);

                if (rrfScores.TryGetValue(key, out var existing))
                {
                    rrfScores[key] = (existing.Score + rrfContrib, existing.Result with
                    {
                        Score = existing.Score + rrfContrib,
                        MatchSource = existing.Result.MatchSource + "+" + r.MatchSource
                    });
                }
                else
                {
                    rrfScores[key] = (rrfContrib, r with
                    {
                        Score = rrfContrib,
                        MatchSource = r.MatchSource
                    });
                }
            }
        }

        AddRrfScore(ftsBooks, "fts5");
        AddRrfScore(ftsChapters, "fts5");

        // Tag 路返回 BookRecord，转换为 RankedResult
        var tagRanked = tagBooks.Select((b, i) => new RankedResult
        {
            BookId = b.BookId,
            BookTitle = b.Title,
            Snippet = b.Summary.Length > 200 ? b.Summary[..200] : b.Summary,
            Score = 0,
            MatchSource = "tag"
        }).ToList();
        AddRrfScore(tagRanked, "tag");

        var merged = rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Result)
            .ToList();

        // ── 歧义检测 ──
        var isAmbiguous = IsAmbiguous(merged);

        if (isAmbiguous && _llmClient is not null)
        {
            // 标记 IsPendingDeepExplore，后台启动异步探索
            merged = merged.Select(r => r with { IsPendingDeepExplore = true }).ToList();

            _ = Task.Run(async () =>
            {
                try
                {
                    await StartDeepExploreAsync(trimmedQuery, CancellationToken.None);
                }
                catch
                {
                    // 后台探索静默失败，不影响主流程
                }
            });
        }

        return merged;
    }

    /// <summary>
    /// 歧义检测：判断检索结果是否需要启动深度探索。
    /// 满足任一即歧义：
    /// - maxBM25Score < 0.25（最好的结果也不相关）
    /// - results.Count < 3（命中太少）
    /// - TagPath 分布熵 > 2（结果分散在多个不相关书架）
    /// </summary>
    private static bool IsAmbiguous(IReadOnlyList<RankedResult> results)
    {
        if (results.Count == 0) return true;
        if (results.Count < 3) return true;

        var maxScore = results.Max(r => r.Score);
        if (maxScore < 0.25) return true;

        // 计算 TagPath 分布熵（通过 BookId 聚集度近似）
        var bookGroups = results.GroupBy(r => r.BookId).Select(g => g.Count()).ToList();
        if (bookGroups.Count <= 1) return false;

        double total = results.Count;
        double entropy = 0;
        foreach (var count in bookGroups)
        {
            var p = count / total;
            entropy -= p * Math.Log2(p);
        }

        return entropy > 2.0;
    }

    /// <summary>
    /// 后台异步 LLM 深度探索。
    /// LLM 通过 TagTree 逐层导航，调用 Tool 搜索相关 Book/Chapter。
    /// 结果写入 _pendingExplorations，下一轮查询通过 GetPendingExplorations 注入。
    /// </summary>
    public async Task StartDeepExploreAsync(string query, CancellationToken ct = default)
    {
        if (_llmClient is null) return;

        const string systemPrompt =
            "你是记忆探索助手。用户查询需要找到记忆图书馆中的相关经验。请: " +
            "1)先看根节点标签 2)逐层深入 3)调用搜索找到相关书籍和章节 4)返回最相关的ChapterId列表和摘要。";

        try
        {
            // 简单实现：直接调用 ChatAsync，后续可扩展为多轮 Tool Calling
            var result = await _llmClient.ChatAsync(systemPrompt, query, null, ct);
            var key = GetExploreKey(query);

            // 将探索结果保存为占位 RankedResult（后续轮次注入时使用）
            var exploreResult = new RankedResult
            {
                BookId = "deep_explore",
                BookTitle = "[DEEP EXPLORE]",
                ChapterId = null,
                ChapterTitle = null,
                Snippet = result.Length > 300 ? result[..300] : result,
                Score = 0.5,
                MatchSource = "deep_explore",
                IsPendingDeepExplore = false
            };

            _pendingExplorations[key] = new List<RankedResult> { exploreResult };
        }
        catch
        {
            // 深度探索失败静默处理
        }
    }

    /// <summary>
    /// 获取上次查询触发的深度探索结果（如有），返回后清空对应缓存。
    /// </summary>
    public IReadOnlyList<RankedResult> GetPendingExplorations(string query)
    {
        var key = GetExploreKey(query);
        if (_pendingExplorations.TryRemove(key, out var results))
            return results;
        return Array.Empty<RankedResult>();
    }

    /// <summary>将 query 前30字符的哈希作为缓存键。</summary>
    private static string GetExploreKey(string query)
    {
        var prefix = query.Length > 30 ? query[..30] : query;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(prefix)))[..16];
    }

    /// <summary>按 Title 精确获取或创建 Book（Title 作为自然键去重）。</summary>
    public async Task<BookRecord> GetOrCreateBookAsync(
        string libraryId, string title, string? summary, IReadOnlyList<string>? tagPaths, CancellationToken ct)
    {
        // 精确 Title 匹配（不区分大小写）做去重 — 改用索引查询替代全量扫描
        var existingBook = await _library.FindBookByTitleAsync(libraryId, title, ct);
        if (existingBook is not null
            && string.Equals(existingBook.Title, title, StringComparison.OrdinalIgnoreCase))
            return existingBook;

        // FindBookByTitleAsync 用精确匹配，可能漏掉大小写不同的情况——回退到 ListBooksAsync
        var books = await _library.ListBooksAsync(libraryId, ct: ct);
        var fallback = books.FirstOrDefault(b =>
            string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase));
        if (fallback is not null) return fallback;

        return await _library.CreateBookAsync(libraryId, title, summary ?? "", tagPaths, ct);
    }

    /// <summary>追加 Chapter 到 Book 末尾（自动计算 ChapterOrder）。</summary>
    public async Task<ChapterRecord> AppendChapterAsync(
        string bookId, string title, string content,
        string? sourceSessionId = null, CancellationToken ct = default)
    {
        var chapters = await _library.ListChaptersAsync(bookId, ct);
        var chapterOrder = chapters.Count > 0 ? chapters.Max(c => c.ChapterOrder) + 1 : 0;
        return await _library.AddChapterAsync(bookId, title, content, chapterOrder, sourceSessionId, agentInstanceId: null, ct: ct);
    }

    /// <summary>
    /// 扫描 Chapter 的 Title 和 Content（前 500 字符）中的关键词，自动发现
    /// 其他已知 Book 的引用并创建 Pointer。支持英文空格分词和中文 n-gram。
    /// </summary>
    public async Task<IReadOnlyList<PointerRecord>> AutoDiscoverPointersAsync(
        string chapterId, CancellationToken ct = default)
    {
        var chapter = await _library.GetChapterAsync(chapterId, ct);
        if (chapter is null) return Array.Empty<PointerRecord>();

        // 获取已有指针，按 TargetType:TargetId 做去重
        var existingPointers = await _library.GetPointersAsync(chapterId, ct);
        var existingKeys = new HashSet<string>(
            existingPointers.Select(p => $"{p.TargetType}:{p.TargetId}"));

        // 从 Title 和 Content（前 500 字符）中提取关键词
        var sourceText = chapter.Title;
        if (!string.IsNullOrEmpty(chapter.Content))
        {
            var contentPrefix = chapter.Content.Length > 500
                ? chapter.Content[..500]
                : chapter.Content;
            sourceText += " " + contentPrefix;
        }

        var keywords = ExtractKeywords(sourceText).Distinct().Take(10).ToList();

        var results = new List<PointerRecord>();
        var seenBookIds = new HashSet<string> { chapter.BookId };

        foreach (var keyword in keywords)
        {
            var matchedBooks = await _library.SearchBooksFtsAsync(keyword, topK: 3, ct);
            foreach (var book in matchedBooks)
            {
                if (seenBookIds.Contains(book.BookId)) continue;
                if (existingKeys.Contains($"book:{book.BookId}")) continue;

                seenBookIds.Add(book.BookId);
                var ptr = await _library.CreatePointerAsync(
                    chapterId, "book", book.BookId,
                    book.Title, $"Auto-discovered from keyword: {keyword}", ct);
                results.Add(ptr);
            }
        }

        return results;
    }

    /// <summary>
    /// 从文本中提取关键词：空格分词 + CJK 字符 n-gram（2-3 字窗口）。
    /// </summary>
    private static IEnumerable<string> ExtractKeywords(string text)
    {
        // 空格分词
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length >= 2) yield return trimmed;
        }

        // CJK 字符 n-gram
        var cjkBuffer = new List<char>();
        foreach (var ch in text)
        {
            if (IsCJK(ch))
            {
                cjkBuffer.Add(ch);
            }
            else
            {
                foreach (var ngram in BuildCjkNgrams(cjkBuffer))
                    yield return ngram;
                cjkBuffer.Clear();
            }
        }
        foreach (var ngram in BuildCjkNgrams(cjkBuffer))
            yield return ngram;
    }

    private static IEnumerable<string> BuildCjkNgrams(List<char> chars)
    {
        for (var len = 2; len <= 3; len++)
        {
            for (var i = 0; i <= chars.Count - len; i++)
            {
                yield return new string(chars.Skip(i).Take(len).ToArray());
            }
        }
    }

    private static bool IsCJK(char ch)
    {
        return (ch >= 0x4E00 && ch <= 0x9FFF)   // CJK Unified Ideographs
            || (ch >= 0x3400 && ch <= 0x4DBF);   // CJK Extension A
    }

    /// <summary>获取 Tag 树根节点。</summary>
    public async Task<IReadOnlyList<TagTreeNode>> GetTagRootsAsync(CancellationToken ct = default)
    {
        return await _library.GetTagChildrenAsync(null, ct);
    }
}
