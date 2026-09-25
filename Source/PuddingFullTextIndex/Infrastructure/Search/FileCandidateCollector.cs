using System.Security;

namespace PuddingFullTextIndex.Infrastructure.Search;

/// <summary>
/// 扫描期「单个文件能否进入索引候选」的唯一判定体 —— 从 <see cref="LuceneSearchEngine"/> 的扫描循环里
/// 抽出为纯函数，使「坏文件只跳过它自己」成为<b>可单测的结构性事实</b>，而不是靠调用点的 try 粒度来保证。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要抽出来（A22b）</b>：旧实现的逐文件判定体躺在一个 <c>try</c> 里，而两个
/// <c>catch</c>（<see cref="DirectoryNotFoundException"/> / <see cref="UnauthorizedAccessException"/>）
/// 挂在 <b>整轮枚举之外</b>：扫描途中任一文件抛这两类异常，被丢弃的是<b>整轮枚举的剩余部分</b>
/// （生产实测形态：4510 个文件里只收集到 99 个，另一轮 0 个），而构建仍返回 <c>Success=true</c>。
/// 判定体搬进本类后，「一个坏文件 ⇒ 少一个候选 + 一次计数」是结构性保证。
/// </para>
/// <para>
/// <b>异常契约</b>：
/// 文件系统类异常（<see cref="IOException"/>，含 <see cref="FileNotFoundException"/> /
/// <see cref="DirectoryNotFoundException"/> / <see cref="PathTooLongException"/>、
/// <see cref="UnauthorizedAccessException"/>、<see cref="SecurityException"/>）
/// 在本类内<b>就地吃掉</b>：返回 <c>false</c> + <see cref="CandidateSkipReasons.ErrorPrefix"/> 前缀的原因
/// （调用方据此计入跳过数），<b>绝不上抛</b>终止整轮枚举；
/// <see cref="OperationCanceledException"/> 则<b>原样外抛</b> —— 取消是控制流，不是「坏文件」。
/// </para>
/// <para>
/// <b>保持既有语义</b>：判定顺序（扩展名 → 调用方 pattern → 排除路径 → 空文件/超限文件）与接受集合
/// 与改动前逐字一致；本类只改变「异常落在哪个粒度上」。glob 匹配仍复用
/// <see cref="LuceneSearchEngine.MatchesAnyPattern"/>（ADR-089 U4-6 的单一实现），不在本类复刻。
/// </para>
/// </remarks>
internal static class FileCandidateCollector
{
    /// <summary>
    /// 判定单个文件是否可成为索引候选。
    /// </summary>
    /// <param name="file">文件绝对路径（沿用枚举器返回的原始形态，不做规范化）。</param>
    /// <param name="scanRoot">本次扫描根，用于 <see cref="FullTextIndexOptions.IsExcludedPath"/> 的相对路径判定。</param>
    /// <param name="options">索引选项（扩展名白名单 / 排除清单 / 体积上限）。</param>
    /// <param name="patterns">调用方 glob 模式；null 或空表示不过滤。</param>
    /// <param name="entry">命中时的候选条目（路径 / 最后写入时间 / 字节数）。</param>
    /// <param name="skipReason">未命中时的原因（策略原因常量，或 <see cref="CandidateSkipReasons.ErrorPrefix"/> 前缀的异常原因）；命中时为 null。</param>
    /// <returns>可索引 ⇒ true；被策略拒绝、或该文件本身抛文件系统异常 ⇒ false。</returns>
    internal static bool TryCollectCandidate(
        string file,
        string scanRoot,
        FullTextIndexOptions options,
        string[]? patterns,
        out CandidateEntry entry,
        out string? skipReason)
    {
        entry = default;
        skipReason = null;

        try
        {
            var ext = Path.GetExtension(file);
            if (!options.IsIndexableExtension(ext))
            {
                skipReason = CandidateSkipReasons.NotIndexableExtension;
                return false;
            }

            if (patterns is { Length: > 0 } && !LuceneSearchEngine.MatchesAnyPattern(file, patterns))
            {
                skipReason = CandidateSkipReasons.PatternMismatch;
                return false;
            }

            if (options.IsExcludedPath(file, scanRoot))
            {
                skipReason = CandidateSkipReasons.ExcludedPath;
                return false;
            }

            var fi = new FileInfo(file);
            var length = fi.Length; // 文件在枚举后消失 / 无权限读元数据 ⇒ 在此抛
            if (length == 0)
            {
                skipReason = CandidateSkipReasons.EmptyFile;
                return false;
            }

            if (length > options.MaxFileSizeBytes)
            {
                skipReason = CandidateSkipReasons.TooLarge;
                return false;
            }

            entry = new CandidateEntry(file, fi.LastWriteTimeUtc, length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 逐文件隔离：坏文件只影响它自己（跳过 + 计数），绝不终止整轮枚举
            skipReason = CandidateSkipReasons.ErrorPrefix + ex.GetType().Name + ": " + ex.Message;
            entry = default;
            return false;
        }
    }

    /// <summary>
    /// 带取消令牌的入口：引擎扫描循环用它，使取消检查落在本类<b>边界内</b>（且在吞异常的 catch 之外）。
    /// </summary>
    /// <remarks>
    /// 取消检查<b>必须</b>在「吞文件系统异常」的区间之外（本方法自身没有 catch，检查也在委派之前）：
    /// 一旦把它挪进那个区间，取消就会退化成「又一个坏文件」而被静默跳过 —— 这正是被 A4 锁死的红线。
    /// </remarks>
    internal static bool TryCollectCandidate(
        string file,
        string scanRoot,
        FullTextIndexOptions options,
        string[]? patterns,
        CancellationToken ct,
        out CandidateEntry entry,
        out string? skipReason)
    {
        ct.ThrowIfCancellationRequested();
        return TryCollectCandidate(file, scanRoot, options, patterns, out entry, out skipReason);
    }

    /// <summary>该跳过原因是否由<b>异常</b>造成（而非策略拒绝）—— 调用方据此累加「因异常跳过」的计数。</summary>
    internal static bool IsErrorSkip(string? skipReason) =>
        skipReason is not null && skipReason.StartsWith(CandidateSkipReasons.ErrorPrefix, StringComparison.Ordinal);
}

/// <summary>
/// 候选文件条目（路径 / 最后写入时间 / 字节数）—— 与扫描列表的元素一一对应。
/// </summary>
internal readonly record struct CandidateEntry(string Path, DateTime LastWrite, long Size);

/// <summary>
/// 跳过原因常量。策略原因是<b>稳定字符串</b>（可被单测逐条锁定），异常原因一律以
/// <see cref="ErrorPrefix"/> 开头，便于调用方区分「策略拒绝」与「坏文件」。
/// </summary>
internal static class CandidateSkipReasons
{
    /// <summary>扩展名不在 <see cref="FullTextIndexOptions.IsIndexableExtension"/> 白名单内（既有语义）。</summary>
    public const string NotIndexableExtension = "not-indexable-extension";

    /// <summary>文件名不匹配调用方的 glob 模式（既有语义）。</summary>
    public const string PatternMismatch = "pattern-mismatch";

    /// <summary>路径命中 <see cref="FullTextIndexOptions.IsExcludedPath"/> 排除清单（既有语义）。</summary>
    public const string ExcludedPath = "excluded-path";

    /// <summary>文件 0 字节，引擎不索引（既有语义）。</summary>
    public const string EmptyFile = "empty-file";

    /// <summary>文件超过 <see cref="FullTextIndexOptions.MaxFileSizeBytes"/>（既有语义）。</summary>
    public const string TooLarge = "too-large";

    /// <summary>该文件本身抛出文件系统异常而被跳过：<c>error:&lt;异常类型名&gt;: &lt;消息&gt;</c>。</summary>
    public const string ErrorPrefix = "error:";
}
