using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
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
        CancellationToken ct = default,
        FullTextSearchScope? scope = null)
    {
        var sw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        if (scope?.FilePaths is { Count: 0 })
            return new FullTextSearchResult(true, [], null, 0, sw.ElapsedMilliseconds);
        if (scope?.FilePaths is { Count: > 512 })
            return new FullTextSearchResult(false, [], "Search scope exceeds 512 files; narrow the scope.", 0, sw.ElapsedMilliseconds);
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
            Query luceneQuery = parser.Parse(scope?.LiteralQuery == true ? QueryParserBase.Escape(query) : query);
            if (scope?.FilePaths is { } paths)
            {
                var files = new BooleanQuery { MinimumNumberShouldMatch = 1 };
                foreach (var path in paths.Distinct(StringComparer.Ordinal))
                    files.Add(new TermQuery(new Term("path", path)), Occur.SHOULD);
                luceneQuery = new BooleanQuery
                {
                    { luceneQuery, Occur.MUST },
                    { files, Occur.MUST },
                };
            }
            ct.ThrowIfCancellationRequested();

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

            return new FullTextSearchResult(true, matches, null,
                scope is not null && extSet is null && subDirPrefix is null ? hits.TotalHits : filteredCount,
                sw.ElapsedMilliseconds);
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

    private async Task WriteLastIndexedAsync(string indexDir, string patternHash, DateTime timestamp, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { t = timestamp, p = patternHash });
        await File.WriteAllTextAsync(GetLastIndexedFilePath(indexDir), json, ct);
    }

    // ── 索引构建核心 ────────────────────────────────────────────────

    /// <summary>
    /// 单次遍历目录树，遇到噪声目录立即剪枝（不再进入），并把枚举异常局部化。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要剪枝</b>：<see cref="FullTextIndexOptions.IsExcludedPath"/> 只在文件枚举<b>之后</b>过滤，
    /// 因此被排除的目录仍会被完整走一遍 —— 本仓库根 scope 里 <c>.pudding</c>（2 万+ 可索引文件）、
    /// <c>.tmp-build</c>、<c>.pnpm-store</c>、<c>.tmp-test-out</c> 正是最大的枚举成本来源。
    /// </para>
    /// <para>
    /// <b>行为等价性</b>：剪枝只提前终止递归，不改变任何本来会被索引的文件 —— 落在被排除目录下的路径
    /// 本来就会被 <see cref="FullTextIndexOptions.IsExcludedPath"/> 拒绝。剪枝判据用的是<b>目录名</b>，
    /// 与后者按<b>相对扫描根的路径段</b>匹配的语义一致（扫描根本身的名字不参与判定，
    /// 因此把工作区建在 <c>Temp</c> 之类目录下不会被整棵树误排除）。
    /// </para>
    /// <para>
    /// <b>保持与旧实现一致的遍历范围</b>：不跳过重解析点（旧实现用 <c>SearchOption.AllDirectories</c> 同样会跟随），
    /// 因此经由目录联接可抵达的文件依旧会被枚举；这条既有风险未被本刀放大也未被修掉。
    /// </para>
    /// </remarks>
    private IEnumerable<string> EnumerateFilesPruned(string rootDirectory, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return file;
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                ct.ThrowIfCancellationRequested();

                // ── 剪枝：噪声目录不再进入（这是本刀的全部性能收益来源）──
                if (_options.ExcludedDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                    continue;

                pending.Push(subDirectory);
            }
        }
    }

    /// <summary>
    /// 文件名是否命中调用方给出的 glob 模式。
    /// </summary>
    /// <remarks>
    /// 保持既有语义：调用方传 <c>".cs"</c> 或 <c>"*.cs"</c>，引擎一律按 <c>"*.cs"</c> 解释
    /// （旧实现是 <c>$"*{p}"</c> + <c>Directory.EnumerateFiles</c>）。
    /// 通配符匹配交给 <see cref="FileSystemName.MatchesSimpleExpression"/>，不自行实现 glob。
    /// </remarks>
    private static bool MatchesAnyPattern(string filePath, string[] patterns)
    {
        var fileName = Path.GetFileName(filePath);

        foreach (var pattern in patterns)
        {
            var glob = pattern[0] == '*' ? pattern : "*" + pattern;
            if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                return true;
        }

        return false;
    }

    private async Task<FullTextIndexResult> BuildIndexInternalAsync(
        string directoryPath, string? filePatterns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDirectoryPath(directoryPath);
        var patterns = filePatterns?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var patternHash = HashPatterns(filePatterns);

        // ── 决定构建模式：增量还是全量 ──
        // 扫描开始时间：作为 .last_indexed 时间戳与变更比较基准，
        // 构建期间被修改的文件满足 LastWrite >= 扫描时间，下次增量能正确捕获
        var scanTimestamp = DateTime.UtcNow;
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
        // U4-6（ADR-089）：单次遍历（"*"）。旧实现把白名单里每个扩展名各当成一个 glob 交给
        // Directory.EnumerateFiles，同一棵树被完整遍历 77 次（PlainTextExtensions 76 项 + ParsedExtensions）：
        // 实测 Source/ scope 77 × 6.6 s ≈ 462 s（索引耗时几乎全在重复遍历上）、根 scope 77 × 195 s ≈ 4.2 h
        // —— 探针据此判定「根 scope 索引不可行」。扩展名白名单与调用方 glob 模式都在枚举后按文件过滤，
        // 因此接受的文件集合与旧实现完全相同；真正的成本削减来自 EnumerateFilesPruned 的目录剪枝。
        var allFiles = new List<(string Path, DateTime LastWrite, long Size)>();
        foreach (var _ in new[] { "*" })
        {
            try
            {
                foreach (var file in EnumerateFilesPruned(directoryPath, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (!_options.IsIndexableExtension(ext)) continue;
                    if (patterns is { Length: > 0 } && !MatchesAnyPattern(file, patterns)) continue;
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
        // 用 >= 而非 >：文件系统时间戳同 tick 内的更新（测试与快速连续编辑场景）不能漏检；
        // 增量路径 delete-then-add 保证重复处理幂等
        var changedFiles = incremental
            ? allFiles.Where(f => f.LastWrite >= lastIndexedAt).ToList()
            : allFiles;

        // 变更比例 > 50% → 回退到全量重建
        if (incremental && allFiles.Count > 0 && changedFiles.Count > allFiles.Count * 0.5)
        {
            incremental = false;
            changedFiles = allFiles;
        }

        // 增量模式：磁盘上已删除的文件必须同步从索引移除，否则过期文档永久残留
        List<string>? stalePaths = null;
        if (incremental)
        {
            try
            {
                var currentPaths = new HashSet<string>(allFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                using (var probeDir = FSDirectory.Open(indexDir))
                using (var probeReader = DirectoryReader.Open(probeDir))
                {
                    stalePaths = new List<string>();
                    foreach (var leaf in probeReader.Leaves)
                    {
                        var pathTerms = ((AtomicReader)leaf.Reader).GetTerms("path");
                        if (pathTerms is null) continue;
                        var termsEnum = pathTerms.GetEnumerator();
                        while (termsEnum.MoveNext())
                        {
                            var indexedPath = termsEnum.Term.Utf8ToString();
                            if (!currentPaths.Contains(indexedPath))
                                stalePaths.Add(indexedPath);
                        }
                    }
                }
            }
            catch
            {
                stalePaths = null; // 陈旧路径读取失败不阻塞本次构建，由下次全量重建兜底
            }
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
                // 增量模式：先清除已删除文件的过期文档
                if (stalePaths is { Count: > 0 })
                {
                    foreach (var stalePath in stalePaths)
                        writer.DeleteDocuments(new Term("path", stalePath));
                }

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

                // 索引变更后显式失效该目录的缓存 Reader/Searcher：
                // 全量重建路径（RemoveIndex + CREATE）后目录已被替换，
                // OpenIfChanged 未必可靠感知，陈旧 Reader 会继续看到过期文档
                if (_readerCache.TryRemove(indexDir, out var staleReader))
                {
                    _searcherCache.TryRemove(indexDir, out _);
                    try { staleReader.Dispose(); } catch { /* ignore */ }
                }

                // 更新 .last_indexed 时间戳（记录扫描开始时间而非构建完成时间）
                await WriteLastIndexedAsync(indexDir, patternHash, scanTimestamp, ct);

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

    internal string GetIndexDirectoryPath(string directoryPath)
    {
        var normalized = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        // 持久化标识必须跨进程稳定；String.GetHashCode 会随 Core 重启变化。
        // 保持此引擎现有的 Windows 路径大小写语义，缓存无需兼容旧随机目录。
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
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

    // ── U4-1a：按预分块文档建索引（只新增入口；既有 BuildIndexAsync 一行未改）─────────────

    /// <summary>
    /// 用「预分块文档」构建（重建）<paramref name="directoryPath"/> 的索引，而不是按目录边遍历。
    /// <para>
    /// 为何需要这个入口：outline 优先分块策略的语料来自语言 outliner 的符号级小块，它们不存在于磁盘目录结构里，
    /// 因此无法用 directory-walk 的 <see cref="BuildIndexAsync"/> 表达。
    /// </para>
    /// <para>
    /// <b>与既有行为的关系</b>：这是一条新增路径，既有 <see cref="BuildIndexAsync"/> 的代码与行为未变；写入的文档沿用同一
    /// 索引布局（<c>path</c> / <c>content</c> / <c>file_name</c> + <c>line_number</c> / <c>line_text</c>），
    /// 所以 <see cref="SearchAsync"/>、<see cref="HasIndex"/>、<see cref="RemoveIndex"/> 全部原样复用（搜索路径不读取新字段）。
    /// </para>
    /// <para>每次都是全量重建：调用方一次性给出整个语料，做增量只会与 <c>.last_indexed</c> 协议相互干扰。</para>
    /// </summary>
    public async Task<ChunkIndexResult> BuildChunkIndexAsync(
        string directoryPath,
        IReadOnlyList<IndexChunkDocument> documents,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("directory path is required", nameof(directoryPath));

        ArgumentNullException.ThrowIfNull(documents);

        var indexDir = GetIndexDirectoryPath(directoryPath);
        var mutex = _indexLocks.GetOrAdd(indexDir, _ => new SemaphoreSlim(1, 1));
        if (!await mutex.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new ChunkIndexResult(false, 0, 0, 0, 0,
                "Chunk index build skipped: another build is already in progress for this directory.");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            RemoveIndex(directoryPath);
            Directory.CreateDirectory(indexDir);

            var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var documentCount = 0;
            long textChars = 0;

            using (var dir = FSDirectory.Open(indexDir))
            {
                var config = new IndexWriterConfig(MatchVersion, _analyzer)
                {
                    OpenMode = OpenMode.CREATE,
                    RAMBufferSizeMB = 48,
                };

                // write.lock 重试：与 BuildIndexInternalAsync 相同的量级（多进程竞争场景）
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
                    foreach (var document in documents)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (string.IsNullOrWhiteSpace(document.Path) || string.IsNullOrWhiteSpace(document.Text))
                            continue;

                        writer.AddDocument(CreateChunkDocument(document));
                        sourceFiles.Add(document.Path);
                        textChars += document.Text.Length;
                        documentCount++;
                    }

                    writer.Commit();
                }
            }

            return new ChunkIndexResult(true, documentCount, sourceFiles.Count, textChars, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            RemoveIndex(directoryPath);
            return new ChunkIndexResult(false, 0, 0, 0, sw.ElapsedMilliseconds, "Chunk index build cancelled.");
        }
        catch (Exception ex)
        {
            return new ChunkIndexResult(false, 0, 0, 0, sw.ElapsedMilliseconds, ex.Message);
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>
    /// 把一个分块文档映射成 Lucene 文档。字段与按行文档同构，差别只有两处：
    /// ① content 的 boost 由块优先级给定（不再使用「短行加权」这类启发式，否则启发式会与优先级静默竞争）；
    /// ② 额外存 chunk_kind / line_end，供报告审计（搜索路径不读这两个字段）。
    /// </summary>
    private static Document CreateChunkDocument(IndexChunkDocument chunk)
    {
        var fileName = Path.GetFileName(chunk.Path);

        var contentField = new TextField("content", chunk.Text, Field.Store.NO)
        {
            Boost = chunk.Boost,
        };

        var fileNameField = new TextField("file_name", fileName, Field.Store.NO)
        {
            Boost = 2.0f,
        };

        return new Document
        {
            new StringField("path", chunk.Path, Field.Store.YES),
            contentField,
            fileNameField,
            new StoredField("line_number", chunk.StartLine),
            new StoredField("line_end", chunk.EndLine),
            new StoredField("line_text", chunk.Text),
            new StoredField("chunk_kind", chunk.Kind),
        };
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
