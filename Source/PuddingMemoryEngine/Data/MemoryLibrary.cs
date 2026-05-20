using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆图书馆底层实现——提供精确的 CRUD 与检索能力。
/// 使用 EF Core + 原生 SQL（FTS5）操作 SQLite。
/// </summary>
public sealed class MemoryLibrary : IMemoryLibrary
{
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbContextFactory;

    public MemoryLibrary(IDbContextFactory<MemoryLibraryDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // ── Library 生命周期 ────────────────────────────────────────────────

    public async Task<LibraryRecord> CreateLibraryAsync(
        string workspaceId, string name, string? description, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new LibraryEntity
        {
            LibraryId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Libraries.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Libraries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.LibraryId == libraryId, ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<LibraryRecord>> ListLibrariesAsync(
        string workspaceId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Libraries.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .OrderByDescending(l => l.UpdatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    // ── Book 生命周期 ──────────────────────────────────────────────────

    public async Task<BookRecord> CreateBookAsync(
        string libraryId, string title, string summary,
        IReadOnlyList<string>? tagPaths = null, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bookId = Guid.NewGuid().ToString("N");
        var book = new BookEntity
        {
            BookId = bookId,
            LibraryId = libraryId,
            Title = title,
            Summary = summary,
            Status = "active",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Books.Add(book);

        if (tagPaths is { Count: > 0 })
        {
            foreach (var tagPath in tagPaths)
            {
                db.BookIndexes.Add(new BookIndexEntity
                {
                    BookId = bookId,
                    TagPath = tagPath,
                    Weight = 1,
                    CreatedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return ToRecord(book);
    }

    public async Task<BookRecord?> GetBookAsync(string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (entity is null) return null;

        // 更新 AccessCount 和 LastAccessedAt（用新 context 避免并发冲突）
        await using var writeDb = await _dbContextFactory.CreateDbContextAsync(ct);
        var tracked = await writeDb.Books.FindAsync(new object[] { bookId }, ct);
        if (tracked is not null)
        {
            tracked.AccessCount++;
            tracked.LastAccessedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await writeDb.SaveChangesAsync(ct);
        }
        return ToRecord(entity);
    }

    public async Task<BookRecord?> GetBookReadOnlyAsync(string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<BookRecord> UpdateBookAsync(
        string bookId, Func<BookRecord, BookRecord> updater, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct)
            ?? throw new InvalidOperationException($"Book not found: {bookId}");

        var record = ToRecord(entity);
        record = updater(record);

        entity.Title = record.Title;
        entity.Summary = record.Summary;
        entity.Status = record.Status;
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.Version++;

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<bool> ArchiveBookAsync(string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (entity is null) return false;
        entity.Status = "archived";
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BookRecord>> ListBooksAsync(
        string libraryId, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Books.AsNoTracking()
            .Where(b => b.LibraryId == libraryId)
            .OrderByDescending(b => b.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    // ── Chapter 生命周期 ───────────────────────────────────────────────

    public async Task<ChapterRecord> AddChapterAsync(
        string bookId, string title, string content,
        int chapterOrder = 0, string? sourceSessionId = null,
        CancellationToken ct = default)
    {
        return await AddChapterInternalAsync(bookId, title, content, chapterOrder,
            sourceSessionId, null, null, ct);
    }

    public async Task<ChapterRecord> AddChapterWithSourceAsync(
        string bookId, string title, string content,
        int chapterOrder = 0, string? sourceSessionId = null,
        string? sourceReference = null, string? referenceType = null,
        CancellationToken ct = default)
    {
        return await AddChapterInternalAsync(bookId, title, content, chapterOrder,
            sourceSessionId, sourceReference, referenceType, ct);
    }

    private async Task<ChapterRecord> AddChapterInternalAsync(
        string bookId, string title, string content,
        int chapterOrder, string? sourceSessionId,
        string? sourceReference, string? referenceType,
        CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new ChapterEntity
        {
            ChapterId = Guid.NewGuid().ToString("N"),
            BookId = bookId,
            Title = title,
            Content = content,
            ChapterOrder = chapterOrder,
            ContentType = "markdown",
            Importance = 0.5,
            SourceSessionId = sourceSessionId,
            SourceReference = sourceReference,
            ReferenceType = referenceType ?? "none",
            WordCount = content.Length,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Chapters.Add(entity);

        // 更新 Book 的 Version 和 UpdatedAt
        var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (book is not null)
        {
            book.Version++;
            book.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<ChapterRecord?> GetChapterAsync(string chapterId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Chapters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ChapterRecord> UpdateChapterContentAsync(
        string chapterId, string newContent, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Chapters.FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter not found: {chapterId}");

        entity.Content = newContent;
        entity.WordCount = newContent.Length;
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 更新对应 Book 的 Version
        var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == entity.BookId, ct);
        if (book is not null)
        {
            book.Version++;
            book.UpdatedAt = entity.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<ChapterRecord> UpdateChapterImportanceAsync(
        string chapterId, double importance, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Chapters.FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter not found: {chapterId}");
        entity.Importance = Math.Clamp(importance, 0, 1);
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<ChapterRecord>> ListChaptersAsync(
        string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.ChapterOrder)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    // ── Pointer ────────────────────────────────────────────────────────

    public async Task<PointerRecord> CreatePointerAsync(
        string chapterId, string targetType, string targetId,
        string? label = null, string? description = null, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = new PointerEntity
        {
            PointerId = Guid.NewGuid().ToString("N"),
            ChapterId = chapterId,
            TargetType = targetType,
            TargetId = targetId,
            TargetLabel = label,
            Description = description,
            Relevance = 5,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.Pointers.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<PointerRecord>> GetPointersAsync(
        string chapterId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Pointers.AsNoTracking()
            .Where(p => p.ChapterId == chapterId)
            .OrderByDescending(p => p.Relevance)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<PointerRecord>> ResolveBacklinksAsync(
        string targetType, string targetId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Pointers.AsNoTracking()
            .Where(p => p.TargetType == targetType && p.TargetId == targetId)
            .OrderByDescending(p => p.Relevance)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<BookRecord?> FindBookByTitleAsync(
        string libraryId, string title, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.LibraryId == libraryId && b.Title == title, ct);
        return entity is null ? null : ToRecord(entity);
    }

    // ── 删除操作 ───────────────────────────────────────────────────────

    public async Task<bool> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Libraries.FirstOrDefaultAsync(l => l.LibraryId == libraryId, ct);
        if (entity is null) return false;

        // 先级联删除子记录（Book → Chapter → Pointer），依赖 SQLite FK ON DELETE CASCADE
        var childBooks = await db.Books.Where(b => b.LibraryId == libraryId).ToListAsync(ct);
        foreach (var book in childBooks)
        {
            await DeleteBookCascadeAsync(db, book.BookId, ct);
        }

        db.Libraries.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteBookAsync(string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (entity is null) return false;

        await DeleteBookCascadeAsync(db, bookId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteChapterAsync(string chapterId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Chapters.FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct);
        if (entity is null) return false;

        // 级联删除 Pointers（SQLite FK ON DELETE CASCADE 会自动处理，EF Core 需显式触发）
        var pointers = await db.Pointers.Where(p => p.ChapterId == chapterId).ToListAsync(ct);
        db.Pointers.RemoveRange(pointers);
        db.Chapters.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeletePointerAsync(string pointerId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Pointers.FirstOrDefaultAsync(p => p.PointerId == pointerId, ct);
        if (entity is null) return false;

        db.Pointers.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── 检索 ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BookRecord>> SearchBooksFtsAsync(
        string query, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // FTS5 搜索返回 BookId + rank，再 Join 主表获取完整记录
        var sql = """
            SELECT b.* FROM Books_fts f
            JOIN Books b ON b.BookId = f.BookId
            WHERE Books_fts MATCH @query
            ORDER BY rank
            LIMIT @topK
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqliteParameter("@query", EscapeFts5Query(query)));
        cmd.Parameters.Add(new SqliteParameter("@topK", topK));

        var results = new List<BookRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BookRecord(
                reader.GetString(0),   // BookId
                reader.GetString(1),   // LibraryId
                reader.GetString(2),   // Title
                reader.GetString(3),   // Summary
                reader.GetString(4),   // Status
                reader.GetInt32(5),    // Version
                reader.GetInt32(6),    // AccessCount
                reader.IsDBNull(7) ? null : reader.GetInt64(7),  // LastAccessedAt
                reader.GetInt64(8),    // CreatedAt
                reader.GetInt64(9)));  // UpdatedAt（ordinal 10 是 ALTER TABLE 追加的 Embedding BLOB）
        }
        return results;
    }

    public async Task<IReadOnlyList<RankedResult>> SearchBooksFtsScoredAsync(
        string query, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // FTS5 BM25 搜索返回 Book 信息 + BM25 分数
        var sql = """
            SELECT b.BookId, b.Title, b.Summary, bm25(Books_fts) AS score
            FROM Books_fts f
            JOIN Books b ON b.BookId = f.BookId
            WHERE Books_fts MATCH @query
            ORDER BY score
            LIMIT @topK
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqliteParameter("@query", EscapeFts5Query(query)));
        cmd.Parameters.Add(new SqliteParameter("@topK", topK));

        var rawResults = new List<(RankedResult Result, double Bm25)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var bookId = reader.GetString(0);
            var title = reader.GetString(1);
            var summary = reader.GetString(2);
            var score = reader.GetDouble(3);
            rawResults.Add((new RankedResult
            {
                BookId = bookId,
                BookTitle = title,
                Snippet = summary.Length > 200 ? summary[..200] : summary,
                Score = score, // 临时存 BM25 原始值，后续归一化
                MatchSource = "fts5"
            }, score));
        }

        return NormalizeScores(rawResults);
    }

    public async Task<IReadOnlyList<ChapterRecord>> SearchChaptersFtsAsync(
        string query, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var sql = """
            SELECT c.* FROM Chapters_fts f
            JOIN Chapters c ON c.ChapterId = f.ChapterId
            WHERE Chapters_fts MATCH @query
            ORDER BY rank
            LIMIT @topK
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqliteParameter("@query", EscapeFts5Query(query)));
        cmd.Parameters.Add(new SqliteParameter("@topK", topK));

        var results = new List<ChapterRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ChapterRecord(
                reader.GetString(0),   // ChapterId
                reader.GetString(1),   // BookId
                reader.GetString(2),   // Title
                reader.GetInt32(3),    // ChapterOrder
                reader.GetString(4),   // Content
                reader.GetString(5),   // ContentType
                reader.GetDouble(6),   // Importance
                reader.IsDBNull(7) ? null : reader.GetString(7),  // SourceSessionId
                reader.GetInt32(8),    // WordCount
                reader.GetInt64(9),    // CreatedAt
                reader.GetInt64(10)));  // UpdatedAt（ordinal 11 是 ALTER TABLE 追加的 Embedding BLOB）
        }
        return results;
    }

    public async Task<IReadOnlyList<RankedResult>> SearchChaptersFtsScoredAsync(
        string query, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // FTS5 BM25 搜索返回 Chapter + 所属 Book Title + BM25 分数
        var sql = """
            SELECT c.ChapterId, c.BookId, c.Title, c.Content, b.Title AS BookTitle,
                   bm25(Chapters_fts) AS score
            FROM Chapters_fts f
            JOIN Chapters c ON c.ChapterId = f.ChapterId
            JOIN Books b ON b.BookId = c.BookId
            WHERE Chapters_fts MATCH @query
            ORDER BY score
            LIMIT @topK
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqliteParameter("@query", EscapeFts5Query(query)));
        cmd.Parameters.Add(new SqliteParameter("@topK", topK));

        var rawResults = new List<(RankedResult Result, double Bm25)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chapterId = reader.GetString(0);
            var bookId = reader.GetString(1);
            var chapterTitle = reader.GetString(2);
            var content = reader.GetString(3);
            var bookTitle = reader.GetString(4);
            var score = reader.GetDouble(5);
            var snippet = content.Length > 200 ? content[..200] : content;
            rawResults.Add((new RankedResult
            {
                BookId = bookId,
                BookTitle = bookTitle,
                ChapterId = chapterId,
                ChapterTitle = chapterTitle,
                Snippet = snippet,
                Score = score, // 临时存 BM25 原始值，后续归一化
                MatchSource = "fts5"
            }, score));
        }

        return NormalizeScores(rawResults);
    }

    public async Task<IReadOnlyList<BookRecord>> SearchBooksByTagAsync(
        string tagPrefix, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var bookIds = await db.BookIndexes.AsNoTracking()
            .Where(idx => idx.TagPath.StartsWith(tagPrefix))
            .OrderByDescending(idx => idx.Weight)
            .Select(idx => idx.BookId)
            .Distinct()
            .Take(topK)
            .ToListAsync(ct);

        if (bookIds.Count == 0) return Array.Empty<BookRecord>();

        var entities = await db.Books.AsNoTracking()
            .Where(b => bookIds.Contains(b.BookId) && b.Status == "active")
            .OrderByDescending(b => b.UpdatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<TagTreeNode>> GetTagChildrenAsync(
        string? parentTag = null, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var allTags = await db.BookIndexes.AsNoTracking()
            .Select(idx => idx.TagPath)
            .Distinct()
            .ToListAsync(ct);

        var prefix = string.IsNullOrEmpty(parentTag) ? "" : parentTag + "/";
        var childTags = allTags
            .Where(t => t.StartsWith(prefix))
            .Select(t =>
            {
                var remainder = t[prefix.Length..];
                var slashIndex = remainder.IndexOf('/');
                return slashIndex > 0 ? remainder[..slashIndex] : remainder;
            })
            .Distinct()
            .Select(child =>
            {
                var fullTag = string.IsNullOrEmpty(parentTag) ? child : $"{parentTag}/{child}";
                var count = allTags.Count(t => t.StartsWith(fullTag));
                var hasChildren = allTags.Any(t => t.StartsWith(fullTag + "/"));
                return new TagTreeNode(fullTag, child, count, hasChildren);
            })
            .OrderByDescending(n => n.Count)
            .ToList();

        return childTags;
    }

    // ── Phase 4: 向量检索 ─────────────────────────────────────────────

    /// <summary>
    /// 嵌入向量搜索章节——加载所有有 Embedding 的 Chapter，
    /// 计算余弦相似度，排序返回 topK。
    /// 时间复杂度 O(N*dim)，适用于 <10 万条目的本地场景。
    /// </summary>
    public async Task<IReadOnlyList<RankedResult>> SearchChaptersByVectorAsync(
        float[] queryEmbedding, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        // 加载所有有 Embedding 的 Chapter（含所属 Book Title）
        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.Embedding != null)
            .Select(c => new
            {
                c.ChapterId,
                c.BookId,
                c.Title,
                c.Content,
                c.Embedding,
                BookTitle = db.Books.Where(b => b.BookId == c.BookId).Select(b => b.Title).FirstOrDefault()
            })
            .ToListAsync(ct);

        if (chapters.Count == 0)
            return Array.Empty<RankedResult>();

        // 计算余弦相似度并排序
        var scored = chapters
            .Select(c => new
            {
                c.ChapterId,
                c.BookId,
                c.Title,
                c.Content,
                c.BookTitle,
                Score = VectorSimilarity.CosineSimilarity(
                    queryEmbedding,
                    VectorSimilarity.BytesToFloats(c.Embedding!))
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        return scored.Select(x => new RankedResult
        {
            BookId = x.BookId,
            BookTitle = x.BookTitle ?? "",
            ChapterId = x.ChapterId,
            ChapterTitle = x.Title,
            Snippet = x.Content.Length > 200 ? x.Content[..200] : x.Content,
            Score = Math.Round(x.Score, 4),
            MatchSource = "vector"
        }).ToList();
    }

    /// <summary>
    /// 融合检索——并行执行 FTS5、TagTree、Vector 三路检索，
    /// 使用 RRF (Reciprocal Rank Fusion) 合并排名，取 topK。
    /// queryEmbedding 为 null 时跳过向量检索路。
    /// </summary>
    public async Task<IReadOnlyList<RankedResult>> HybridSearchAsync(
        string query, float[]? queryEmbedding, int topK = 20, CancellationToken ct = default)
    {
        // 空查询不执行 FTS5（会导致语法错误），Tag 和 Vector 路仍可运行
        var trimmedQuery = query?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedQuery) && queryEmbedding is null)
            return Array.Empty<RankedResult>();

        const double k = 60; // RRF 平滑常数

        // 并行三路检索
        Task<IReadOnlyList<RankedResult>> ftsTask;
        if (!string.IsNullOrEmpty(trimmedQuery))
            ftsTask = SearchChaptersFtsScoredAsync(trimmedQuery, topK * 3, ct);
        else
            ftsTask = Task.FromResult<IReadOnlyList<RankedResult>>(Array.Empty<RankedResult>());

        Task<IReadOnlyList<BookRecord>> tagTask;
        if (!string.IsNullOrEmpty(trimmedQuery))
            tagTask = SearchBooksByTagAsync(trimmedQuery, topK * 3, ct);
        else
            tagTask = Task.FromResult<IReadOnlyList<BookRecord>>(Array.Empty<BookRecord>());

        Task<IReadOnlyList<RankedResult>>? vectorTask = null;
        if (queryEmbedding is not null)
            vectorTask = SearchChaptersByVectorAsync(queryEmbedding, topK * 3, ct);

        await Task.WhenAll(
            ftsTask,
            tagTask,
            vectorTask ?? Task.CompletedTask);

        var ftsResults = await ftsTask;
        var tagResults = await tagTask;
        var vectorResults = vectorTask is not null ? await vectorTask : Array.Empty<RankedResult>();

        // RRF 合并：score = Σ 1/(k + rank_i)
        // 使用 BookId 或 ChapterId 作为融合键
        var rrfScores = new Dictionary<string, (double Score, RankedResult Result)>();

        void AddRrfScore(IReadOnlyList<RankedResult> results, string sourcePrefix)
        {
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                // Chapter 级结果用 ChapterId，Book 级结果用 BookId
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

        AddRrfScore(ftsResults, "fts5");
        AddRrfScore(vectorResults, "vector");

        // Tag 路返回 BookRecord，转换为 RankedResult
        var tagRanked = tagResults.Select((b, i) => new RankedResult
        {
            BookId = b.BookId,
            BookTitle = b.Title,
            Snippet = b.Summary.Length > 200 ? b.Summary[..200] : b.Summary,
            Score = 0,
            MatchSource = "tag"
        }).ToList();
        AddRrfScore(tagRanked, "tag");

        return rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Result)
            .ToList();
    }

    /// <summary>更新章节的嵌入向量字节数组。</summary>
    public async Task<bool> UpdateChapterEmbeddingAsync(
        string chapterId, byte[] embedding, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = await db.Chapters.FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct);
        if (entity is null) return false;

        entity.Embedding = embedding;
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── 分支 ───────────────────────────────────────────────────────────

    public async Task<BranchRecord> BranchBookAsync(
        string bookId, string branchName, string? description, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new BranchEntity
        {
            BranchId = Guid.NewGuid().ToString("N"),
            BookId = bookId,
            BranchName = branchName,
            Description = description,
            IsDefault = false,
            CreatedAt = now
        };
        db.Branches.Add(entity);

        // 复制默认 Tag 索引：确保分支继承 Book 的所有 Tag 分类路径
        var existingTags = await db.BookIndexes.AsNoTracking()
            .Where(idx => idx.BookId == bookId)
            .Select(idx => idx.TagPath)
            .Distinct()
            .ToListAsync(ct);
        foreach (var tagPath in existingTags)
        {
            db.BookIndexes.Add(new BookIndexEntity
            {
                BookId = bookId,
                TagPath = tagPath,
                Weight = 1,
                CreatedAt = now
            });
        }

        // 分支创建视为一次 Book 版本变更
        var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (book is not null)
        {
            book.Version++;
            book.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<BranchRecord>> ListBranchesAsync(
        string bookId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Branches.AsNoTracking()
            .Where(b => b.BookId == bookId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<bool> MergeBranchAsync(
        string sourceBranchId, string targetBranchId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var source = await db.Branches.FirstOrDefaultAsync(b => b.BranchId == sourceBranchId, ct);
        if (source is null) return false;

        var target = await db.Branches.FirstOrDefaultAsync(b => b.BranchId == targetBranchId, ct);
        if (target is null) return false;

        // 验证两个分支属于同一 Book
        if (source.BookId != target.BookId) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 标记源分支已合并
        source.MergedInto = targetBranchId;

        // 合并后 Book 版本递增（分支共享 BookId，Chapters 天然共享无需迁移）
        var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == source.BookId, ct);
        if (book is not null)
        {
            book.Version++;
            book.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── ADR-028 Phase 1: Workspace scoped 检索 ─────────────────────────

    /// <summary>
    /// FTS5 全文搜索章节（workspace scoped）——仅返回当前 workspace 的结果。
    /// 通过 JOIN Books -> Libraries 过滤 workspaceId。
    /// </summary>
    public async Task<IReadOnlyList<RankedResult>> SearchChaptersFtsScopedAsync(
        string workspaceId, string query, int topK = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var sql = """
            SELECT c.ChapterId, c.BookId, c.Title, c.Content, b.Title AS BookTitle,
                   bm25(Chapters_fts) AS score
            FROM Chapters_fts f
            JOIN Chapters c ON c.ChapterId = f.ChapterId
            JOIN Books b ON b.BookId = c.BookId
            JOIN Libraries l ON l.LibraryId = b.LibraryId
            WHERE Chapters_fts MATCH @query AND l.WorkspaceId = @workspaceId
            ORDER BY score
            LIMIT @topK
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqliteParameter("@query", EscapeFts5Query(query)));
        cmd.Parameters.Add(new SqliteParameter("@workspaceId", workspaceId));
        cmd.Parameters.Add(new SqliteParameter("@topK", topK));

        var rawResults = new List<(RankedResult Result, double Bm25)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chapterId = reader.GetString(0);
            var bookId = reader.GetString(1);
            var chapterTitle = reader.GetString(2);
            var content = reader.GetString(3);
            var bookTitle = reader.GetString(4);
            var score = reader.GetDouble(5);
            var snippet = content.Length > 200 ? content[..200] : content;
            rawResults.Add((new RankedResult
            {
                BookId = bookId,
                BookTitle = bookTitle,
                ChapterId = chapterId,
                ChapterTitle = chapterTitle,
                Snippet = snippet,
                Score = score,
                MatchSource = "fts5"
            }, score));
        }

        return NormalizeScores(rawResults);
    }

    /// <summary>
    /// 列出 Workspace 下所有图书馆的书籍（scoped）。
    /// 通过 Libraries 表过滤 workspaceId。
    /// </summary>
    public async Task<IReadOnlyList<BookRecord>> ListBooksScopedAsync(
        string workspaceId, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.Books.AsNoTracking()
            .Join(db.Libraries, b => b.LibraryId, l => l.LibraryId, (b, l) => new { Book = b, Library = l })
            .Where(x => x.Library.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.Book.UpdatedAt)
            .Take(limit)
            .Select(x => x.Book)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    // ── ADR-028 Phase 2: SourceReference ───────────────────────────────

    public async Task<SourceReferenceRecord> AddSourceReferenceAsync(
        SourceReferenceCreateRequest request, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = new SourceReferenceEntity
        {
            SourceReferenceId = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            OwnerType = request.OwnerType,
            OwnerId = request.OwnerId,
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            TargetRange = request.TargetRange,
            Label = request.Label,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.SourceReferences.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<SourceReferenceRecord>> GetSourceReferencesAsync(
        string ownerType, string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.SourceReferences.AsNoTracking()
            .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    // ── ADR-028 Phase 3: TreeNode ──────────────────────────────────────

    public async Task<IReadOnlyList<TreeNodeRecord>> GetTreeChildrenAsync(
        string workspaceId, string libraryId, string? parentNodeId,
        CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entities = await db.MemoryTreeNodes.AsNoTracking()
            .Where(n => n.WorkspaceId == workspaceId
                     && n.LibraryId == libraryId
                     && n.ParentNodeId == parentNodeId
                     && n.Status == "active")
            .OrderBy(n => n.SortOrder)
            .ThenBy(n => n.Name)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<TreeNodeRecord> CreateTreeNodeAsync(
        string workspaceId, string libraryId, string? parentNodeId,
        string name, string? summary = null, string nodeType = "category",
        CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 计算 Path
        var parentPath = "/";
        if (!string.IsNullOrEmpty(parentNodeId))
        {
            var parent = await db.MemoryTreeNodes.AsNoTracking()
                .FirstOrDefaultAsync(n => n.NodeId == parentNodeId, ct);
            if (parent is not null) parentPath = parent.Path.EndsWith('/') ? parent.Path : parent.Path + "/";
        }
        var path = parentPath + name;

        var entity = new MemoryTreeNodeEntity
        {
            NodeId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            LibraryId = libraryId,
            ParentNodeId = parentNodeId,
            Path = path,
            Name = name,
            Summary = summary,
            NodeType = nodeType,
            Status = "active",
            SortOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MemoryTreeNodes.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    public async Task<BookTreeMountRecord> MountBookAsync(
        string bookId, string nodeId, int weight = 1,
        CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var entity = new BookTreeMountEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            BookId = bookId,
            NodeId = nodeId,
            Weight = weight,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.BookTreeMounts.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    /// <summary>
    /// 懒创建默认系统 Books。不覆盖已存在的 Book。
    /// 默认 books: 航海日志、用户档案、用户偏好、决策记录、经验教训、交接索引
    /// </summary>
    public async Task EnsureDefaultBooksAsync(
        string workspaceId, string libraryId,
        CancellationToken ct = default)
    {
        var defaults = new (string Title, string Summary)[]
        {
            ("航海日志", "重要事件和任务进展，不记录流水账"),
            ("用户档案", "稳定个人事实——姓名、角色、技能、背景"),
            ("用户偏好", "偏好、习惯、风格、沟通方式"),
            ("决策记录", "架构、产品、技术选型等关键决策"),
            ("经验教训", "故障、踩坑、复盘、最佳实践"),
            ("交接索引", "指向 memo、session、run archive 的轻量索引"),
        };

        foreach (var (title, summary) in defaults)
        {
            var existing = await FindBookByTitleAsync(libraryId, title, ct);
            if (existing is null)
                await CreateBookAsync(libraryId, title, summary, ct: ct);
        }
    }

    // ── 私有辅助 ───────────────────────────────────────────────────────

    /// <summary>级联删除 Book 及其所有 Chapter/Pointer/BookIndex。</summary>
    private async Task DeleteBookCascadeAsync(MemoryLibraryDbContext db, string bookId, CancellationToken ct)
    {
        // 先删除子记录（SQLite FK ON DELETE CASCADE 会自动处理，显式加载确保 EF Core 追踪）
        var chapters = await db.Chapters.Where(c => c.BookId == bookId).ToListAsync(ct);
        foreach (var ch in chapters)
        {
            var pointers = await db.Pointers.Where(p => p.ChapterId == ch.ChapterId).ToListAsync(ct);
            db.Pointers.RemoveRange(pointers);
        }
        db.Chapters.RemoveRange(chapters);

        var indexes = await db.BookIndexes.Where(i => i.BookId == bookId).ToListAsync(ct);
        db.BookIndexes.RemoveRange(indexes);

        var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        if (book is not null) db.Books.Remove(book);
    }

    /// <summary>将 BM25 原始分数组归一化到 0-1 范围。</summary>
    private static IReadOnlyList<RankedResult> NormalizeScores(
        List<(RankedResult Result, double Bm25)> rawResults)
    {
        if (rawResults.Count == 0) return Array.Empty<RankedResult>();

        var maxBm25 = rawResults.Max(r => r.Bm25);
        if (maxBm25 <= 0) maxBm25 = 1; // 防止除零

        return rawResults.Select(r => new RankedResult
        {
            BookId = r.Result.BookId,
            BookTitle = r.Result.BookTitle,
            ChapterId = r.Result.ChapterId,
            ChapterTitle = r.Result.ChapterTitle,
            Snippet = r.Result.Snippet,
            Score = Math.Round(r.Bm25 / maxBm25, 4),
            MatchSource = r.Result.MatchSource
        }).ToList();
    }

    // ── 静态映射方法 ───────────────────────────────────────────────────

    private static LibraryRecord ToRecord(LibraryEntity e) => new(
        e.LibraryId, e.WorkspaceId, e.Name, e.Description, e.CreatedAt, e.UpdatedAt);

    private static BookRecord ToRecord(BookEntity e) => new(
        e.BookId, e.LibraryId, e.Title, e.Summary, e.Status, e.Version,
        e.AccessCount, e.LastAccessedAt, e.CreatedAt, e.UpdatedAt);

    private static ChapterRecord ToRecord(ChapterEntity e) => new(
        e.ChapterId, e.BookId, e.Title, e.ChapterOrder, e.Content, e.ContentType,
        e.Importance, e.SourceSessionId, e.WordCount, e.CreatedAt, e.UpdatedAt,
        e.SourceReference, e.ReferenceType);

    private static PointerRecord ToRecord(PointerEntity e) => new(
        e.PointerId, e.ChapterId, e.TargetType, e.TargetId,
        e.TargetLabel, e.Description, e.Relevance, e.CreatedAt);

    private static BranchRecord ToRecord(BranchEntity e) => new(
        e.BranchId, e.BookId, e.BranchName, e.Description,
        e.CreatedBy, e.MergedInto, e.IsDefault, e.CreatedAt);

    private static SourceReferenceRecord ToRecord(SourceReferenceEntity e) => new(
        e.SourceReferenceId, e.WorkspaceId, e.OwnerType, e.OwnerId,
        e.TargetType, e.TargetId, e.TargetRange, e.Label, e.Description, e.CreatedAt);

    private static TreeNodeRecord ToRecord(MemoryTreeNodeEntity e) => new(
        e.NodeId, e.WorkspaceId, e.LibraryId, e.ParentNodeId,
        e.Path, e.Name, e.Summary, e.NodeType, e.Status,
        e.SortOrder, e.CreatedAt, e.UpdatedAt);

    private static BookTreeMountRecord ToRecord(BookTreeMountEntity e) => new(
        e.Id, e.BookId, e.NodeId, e.Weight, e.CreatedAt);

    /// <summary>
    /// 转义 FTS5 查询：先按空格分词，再对每个词做双引号短语包装，用空格连接。
    /// 避免把整个自然语言查询变成精确短语匹配。
    /// </summary>
    private static string EscapeFts5Query(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var escapedTokens = tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\"");
        return string.Join(" ", escapedTokens);
    }
}
