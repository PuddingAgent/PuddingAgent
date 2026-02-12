using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Text;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndex.Infrastructure.Search;

/// <summary>
/// 基于 Lucene.NET 的全文索引搜索引擎。
/// 每个目录对应一个独立的 Lucene 索引目录。
/// 使用 jieba 分词（JiebaAnalyzer）对内容做索引和搜索。
/// </summary>
public sealed class LuceneSearchEngine : IFullTextSearchEngine, IDisposable
{
    private static readonly LuceneVersion MatchVersion = LuceneVersion.LUCENE_48;

    /// <summary>距上次全量重建超过此间隔 → 强制全量重建（清理已删除文件的僵尸文档）。</summary>
    private static readonly TimeSpan FullRebuildInterval = TimeSpan.FromHours(24);

    private readonly FullTextIndexOptions _options;
    private readonly Analyzer _analyzer;
    private readonly IReadOnlyDictionary<string, IFileContentExtractor> _extractors;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);

    // IndexSearcher 缓存：每个索引目录持有一个 DirectoryReader，搜索时检查是否需要刷新
    private readonly ConcurrentDictionary<string, DirectoryReader> _readerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IndexSearcher> _searcherCache = new(StringComparer.OrdinalIgnoreCase);

    public LuceneSearchEngine(FullTextIndexOptions options)
        : this(options, new JiebaAnalyzer(), new List<IFileContentExtractor> { new PlainTextExtractor() })
    {
    }

    public LuceneSearchEngine(
        FullTextIndexOptions options,
        Analyzer analyzer,
        IEnumerable<IFileContentExtractor> extractors)
    {
        _options = options;
        _analyzer = analyzer;
        _extractors = extractors
            .SelectMany(e => e.SupportedExtensions.Select(ext => (ext, e)))
            .ToDictionary(x => x.ext, x => x.e, StringComparer.OrdinalIgnoreCase);
    }

    // ── 公共接口 ────────────────────────────────────────────────────────

    public bool HasIndex(string directoryPath)
    {
        var indexDir = GetIndexDirectoryPath(directoryPath);
        return Directory.Exists(indexDir) && IndexHasDocuments(indexDir);
    }

    public async Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDirectoryPath(directoryPath);

        if (!HasIndex(directoryPath))
        {
            return new FullTextSearchResult(false, [],
                $"Directory '{directoryPath}' is not indexed.", 0, sw.ElapsedMilliseconds);
        }

        // 解析扩展名过滤集合
        HashSet<string>? extSet = null;
        if (!string.IsNullOrWhiteSpace(fileExtensionFilter))
        {
            extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in fileExtensionFilter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var e = ext.StartsWith('.') ? ext.Trim() : $".{ext.Trim()}";
                extSet.Add(e);
            }
        }

        // 标准化子目录过滤前缀
        var subDirPrefix = !string.IsNullOrWhiteSpace(subDirectoryFilter)
            ? subDirectoryFilter.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar
            : null;

        // Fetch more results than needed so we can post-filter and still fill maxResults
        var fetchCount = extSet != null || subDirPrefix != null ? maxResults * 5 : maxResults;

        try
        {
            var searcher = GetOrRefreshSearcher(indexDir);
            var parser = new MultiFieldQueryParser(MatchVersion,
                new[] { "content", "file_name" }, _analyzer);
            var luceneQuery = parser.Parse(query);

            var hits = searcher.Search(luceneQuery, fetchCount);
            var matches = new List<FullTextSearchMatch>();
            var filteredCount = 0;

            foreach (var hit in hits.ScoreDocs)
            {
                ct.ThrowIfCancellationRequested();
                var doc = searcher.Doc(hit.Doc);
                var path = doc.Get("path");
                var storedLine = doc.Get("line_number");
                var text = doc.Get("line_text");

                if (path == null) continue;

                // 扩展名过滤
                if (extSet != null)
                {
                    var ext = Path.GetExtension(path);
                    if (!extSet.Contains(ext)) continue;
                }

                // 子目录过滤（基于索引根目录的相对路径）
                if (subDirPrefix != null)
                {
                    var relativePath = Path.GetRelativePath(directoryPath, path);
                    if (!relativePath.StartsWith(subDirPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                filteredCount++;
                if (matches.Count >= maxResults) break;

                _ = int.TryParse(storedLine, out var lineNumber);
                matches.Add(new FullTextSearchMatch(path, lineNumber, text ?? string.Empty));
            }

            return new FullTextSearchResult(true, matches, null, filteredCount, sw.ElapsedMilliseconds);
        }
        catch (ParseException ex)
        {
            return new FullTextSearchResult(false, [],
                $"Invalid search query: {ex.Message}", 0, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FullTextSearchResult(false, [],
                $"Search failed: {ex.Message}", 0, sw.ElapsedMilliseconds);
        }
    }

    public async Task<FullTextIndexResult> BuildIndexAsync(
        string directoryPath,
        string? filePatterns = null,
        CancellationToken ct = default)
    {
        // 同目录同一时间只有一个索引构建任务，避免 Lucene write.lock 冲突
        var lockKey = GetIndexDirectoryPath(directoryPath);
        var mutex = _indexLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        if (!await mutex.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new FullTextIndexResult(false, 0, 0, 0,
                "Index build skipped: another build is already in progress for this directory.");
        }

        try
        {
            return await BuildIndexInternalAsync(directoryPath, filePatterns, ct);
        }
        finally
        {
            mutex.Release();
        }
    }

    // ── 增量索引元数据 ────────────────────────────────────────────────

    /// <summary>.last_indexed 文件内容：记录上次索引时间戳和 patterns 哈希。</summary>
    private sealed record LastIndexedStamp(DateTime Timestamp, string PatternHash);

    private static string GetLastIndexedFilePath(string indexDir) =>
        Path.Combine(indexDir, ".last_indexed");

    private static string HashPatterns(string? filePatterns)
    {
        var input = filePatterns ?? "(default)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private async Task<LastIndexedStamp?> ReadLastIndexedAsync(string indexDir, CancellationToken ct)
    {
        var path = GetLastIndexedFilePath(indexDir);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var t = doc.RootElement.GetProperty("t").GetDateTime();
            var p = doc.RootElement.GetProperty("p").GetString() ?? "";
            return new LastIndexedStamp(t, p);
        }
        catch
        {
            return null; // 损坏的 .last_indexed → 视为无
        }
    }

    private async Task WriteLastIndexedAsync(string indexDir, string patternHash, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { t = DateTime.UtcNow, p = patternHash });
        await File.WriteAllTextAsync(GetLastIndexedFilePath(indexDir), json, ct);
    }

    // ── 索引构建核心 ────────────────────────────────────────────────

    private async Task<FullTextIndexResult> BuildIndexInternalAsync(
        string directoryPath, string? filePatterns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDirectoryPath(directoryPath);
        var patterns = filePatterns?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = patterns is { Length: > 0 }
            ? patterns.Select(p => $"*{p}").ToArray()
            : _options.PlainTextExtensions
                .Concat(_options.ParsedExtensions)
                .Select(ext => $"*{ext}")
                .ToArray();
        var patternHash = HashPatterns(filePatterns);

        // ── 决定构建模式：增量还是全量 ──
        bool incremental = false;
        DateTime lastIndexedAt = DateTime.MinValue;
        bool hasExistingIndex = Directory.Exists(indexDir) && IndexHasDocuments(indexDir);

        if (hasExistingIndex)
        {
            var stamp = await ReadLastIndexedAsync(indexDir, ct);
            if (stamp != null)
            {
                var age = DateTime.UtcNow - stamp.Timestamp;
                if (age < FullRebuildInterval && stamp.PatternHash == patternHash)
                {
                    incremental = true;
                    lastIndexedAt = stamp.Timestamp;
                }
            }
        }

        // ── 扫描文件系统 ──
        var allFiles = new List<(string Path, DateTime LastWrite, long Size)>();
        foreach (var filePattern in filter)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directoryPath, filePattern, SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (!_options.IsIndexableExtension(ext)) continue;
                    if (_options.IsExcludedPath(file, directoryPath)) continue;

                    var fi = new FileInfo(file);
                    if (fi.Length > _options.MaxFileSizeBytes || fi.Length == 0) continue;

                    allFiles.Add((file, fi.LastWriteTimeUtc, fi.Length));
                }
            }
            catch (DirectoryNotFoundException) { /* skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        // 增量模式下筛选变更文件
        var changedFiles = incremental
            ? allFiles.Where(f => f.LastWrite > lastIndexedAt).ToList()
            : allFiles;

        // 变更比例 > 50% → 回退到全量重建
        if (incremental && allFiles.Count > 0 && changedFiles.Count > allFiles.Count * 0.5)
        {
            incremental = false;
            changedFiles = allFiles;
        }

        // ── 准备索引目录 ──
        if (!incremental)
        {
            RemoveIndex(directoryPath);
        }
        Directory.CreateDirectory(indexDir);

        var openMode = incremental ? OpenMode.CREATE_OR_APPEND : OpenMode.CREATE;

        try
        {
            using var dir = FSDirectory.Open(indexDir);
            var config = new IndexWriterConfig(MatchVersion, _analyzer)
            {
                OpenMode = openMode,
                RAMBufferSizeMB = 48,
            };

            // write.lock 重试：最多等 5 秒（多进程竞争场景）
            IndexWriter writer;
            var retries = 0;
            const int maxRetries = 10;
            while (true)
            {
                try
                {
                    writer = new IndexWriter(dir, config);
                    break;
                }
                catch (LockObtainFailedException)
                {
                    retries++;
                    if (retries >= maxRetries)
                        throw;
                    Thread.Sleep(500);
                }
            }

            using (writer)
            {
                var indexedCount = 0;
                var totalBytes = 0L;

                foreach (var (filePath, _, size) in changedFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // 增量模式：先删除该文件的旧文档，再添加新文档（避免重复）
                        if (incremental)
                        {
                            writer.DeleteDocuments(new Term("path", filePath));
                        }

                        var ext = Path.GetExtension(filePath);
                        var content = await ExtractContentAsync(filePath, ext, ct);
                        if (string.IsNullOrWhiteSpace(content))
                            continue;

                        AddDocument(writer, filePath, content);
                        indexedCount++;
                        totalBytes += size;
                    }
                    catch (UnauthorizedAccessException) { /* skip */ }
                    catch (IOException) { /* skip */ }
                }

                writer.Commit();

                // 更新 .last_indexed 时间戳
                await WriteLastIndexedAsync(indexDir, patternHash, ct);

                return new FullTextIndexResult(true, indexedCount, totalBytes, sw.ElapsedMilliseconds, null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!incremental)
                RemoveIndex(directoryPath);
            return new FullTextIndexResult(false, 0, 0, sw.ElapsedMilliseconds, "Index build cancelled.");
        }
        catch (Exception ex)
        {
            return new FullTextIndexResult(false, 0, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public bool RemoveIndex(string directoryPath)
    {
        var indexDir = GetIndexDirectoryPath(directoryPath);
        if (!Directory.Exists(indexDir))
            return true;

        try
        {
            Directory.Delete(indexDir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var reader in _readerCache.Values)
        {
            try { reader.Dispose(); } catch { /* ignore */ }
        }
        _readerCache.Clear();
        _searcherCache.Clear();
        _analyzer.Dispose();
    }

    // ── IndexSearcher 缓存（单例化避免每次搜索新建）───────────────────────

    /// <summary>
    /// 获取或刷新缓存的 IndexSearcher。如果索引已变更（写入后），
    /// 通过 DirectoryReader.OpenIfChanged 自动刷新，无需重建。
    /// 参考 Lucene 最佳实践：单例 IndexSearcher 通过 OpenIfChanged 保持新鲜度。
    /// </summary>
    private IndexSearcher GetOrRefreshSearcher(string indexDir)
    {
        if (_readerCache.TryGetValue(indexDir, out var existingReader))
        {
            // 尝试以 NewReader 形式获取变更后的 Reader（不阻塞，不重建）
            var newReader = DirectoryReader.OpenIfChanged(existingReader);
            if (newReader != null)
            {
                // 索引已更新 → 替换旧 Reader 和 Searcher
                var newSearcher = new IndexSearcher(newReader);
                _readerCache[indexDir] = newReader;
                _searcherCache[indexDir] = newSearcher;
                existingReader.Dispose();
                return newSearcher;
            }
        }
        else
        {
            // 首次访问 → 新建 Reader 并缓存
            var dir = FSDirectory.Open(indexDir);
            var reader = DirectoryReader.Open(dir);
            var searcher = new IndexSearcher(reader);
            _readerCache[indexDir] = reader;
            _searcherCache[indexDir] = searcher;
        }

        return _searcherCache[indexDir];
    }

    private string GetIndexDirectoryPath(string directoryPath)
    {
        var normalized = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 使用目录路径的哈希作为索引子目录名
        var hash = Math.Abs(normalized.GetHashCode(StringComparison.OrdinalIgnoreCase)).ToString("x8");
        return Path.Combine(_options.IndexRootDirectory, hash);
    }

    private async Task<string> ExtractContentAsync(string filePath, string extension, CancellationToken ct)
    {
        // 纯文本文件 — 直接读取
        if (_options.PlainTextExtensions.Contains(extension))
        {
            return await File.ReadAllTextAsync(filePath, ct);
        }

        // 解析型文件 — 通过提取器
        if (_extractors.TryGetValue(extension, out var extractor))
        {
            return await extractor.ExtractAsync(filePath, ct);
        }

        return string.Empty;
    }

    /// <summary>
    /// 按行存储文档：每行一个 Lucene Document。
    /// content 字段用于全文检索，file_name 带 Boost 提高文件名命中权重。
    /// 每行附带 line_number（1-based）和 line_text 原文。
    /// </summary>
    private static void AddDocument(IndexWriter writer, string filePath, string content)
    {
        var fileName = Path.GetFileName(filePath);
        var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var docs = new List<Document>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // 短行（≤80 字符、非纯符号）可能是标题/摘要，提升权重
            var contentBoost = line.Length <= 80 && !line.All(c => !char.IsLetterOrDigit(c))
                ? 1.5f : 1.0f;

            var contentField = new TextField("content", line, Field.Store.NO)
            {
                Boost = contentBoost,
            };

            // file_name 字段带 Boost=2.0：文件名命中权重是正文的 2 倍
            var fileNameField = new TextField("file_name", fileName, Field.Store.NO)
            {
                Boost = 2.0f,
            };

            var doc = new Document
            {
                new StringField("path", filePath, Field.Store.YES),
                contentField,
                fileNameField,
                new StoredField("line_number", i + 1),
                new StoredField("line_text", line),
            };

            docs.Add(doc);
        }

        if (docs.Count > 0)
            writer.AddDocuments(docs);
    }

    private static bool IndexHasDocuments(string indexDir)
    {
        try
        {
            using var dir = FSDirectory.Open(indexDir);
            if (!DirectoryReader.IndexExists(dir))
                return false;

            using var reader = DirectoryReader.Open(dir);
            return reader.NumDocs > 0;
        }
        catch
        {
            return false;
        }
    }

}
