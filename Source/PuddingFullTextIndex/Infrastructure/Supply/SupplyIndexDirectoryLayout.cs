using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 索引根目录下的**磁盘布局单一真源**（A2a R1/R2/R5）。
/// <code>
/// &lt;IndexRoot&gt;/
///     &lt;64 位小写 hex&gt;/                          ← live scope 索引目录（引擎 GetIndexDirectoryPath 的产物）
///     .staging/&lt;sha256(scopeKey)&gt;-&lt;jobId&gt;/       ← 本次构建的 staging 根（引擎在它之下再建 &lt;hash&gt; 目录）
///     .trash/&lt;sha256(scopeKey)&gt;-&lt;utcTicks&gt;/       ← 被替换下来的旧 live（切换成功后删除）
///     .supply-leases/                             ← 跨进程租约（见 FileSupplyLease）
///     .supply-manifests/                          ← 保留名（A1 未使用；预算口径与清理都必须跳过）
/// </code>
/// <para>
/// 三条不变式（A2a 的正确性前提）：
/// ① <b>同卷</b>：staging / trash / live 都在 <c>&lt;IndexRoot&gt;</c> 之下 ⇒ <c>Directory.Move</c> 是重命名语义，
/// 切换是原子的（跨卷 Move 会退化成复制+删除，不原子且可能部分可见）；
/// ② <b>live 目录名可识别</b>：恰为 64 位小写 hex（引擎用 <c>Convert.ToHexStringLower(SHA256(...))</c>）；
/// ③ <b>保留目录不参与预算</b>：以点开头的保留名（staging / trash / leases / manifests）一律不计入 live 用量。
/// </para>
/// <para>
/// ⚠️ 本类只在**供给层**内部使用；索引目录名的哈希规则**不在这里复刻** ——
/// 语料根 → 索引目录的映射由 <c>IFullTextIndexRootedEngine.ResolveIndexDirectory</c> 提供（单一真源）。
/// 这里的 <see cref="Sha256Hex"/> 仅用于供给层的**命名**（staging/trash 条目名、租约文件名），
/// 输入是 scope 键而不是文件系统路径。
/// </para>
/// </summary>
internal static class SupplyIndexDirectoryLayout
{
    /// <summary>staging 目录名（构建暂存，位于 IndexRoot 下）。</summary>
    internal const string StagingDirectoryName = ".staging";

    /// <summary>trash 目录名（旧 live 的中转，位于 IndexRoot 下；切换成功后尽力删除）。</summary>
    internal const string TrashDirectoryName = ".trash";

    /// <summary>保留目录名：A1 预留、A2a 未使用；预算口径与残留清理都必须跳过它。</summary>
    internal const string ManifestDirectoryName = ".supply-manifests";

    /// <summary>live 索引目录名的字符数（64 位小写 hex）。</summary>
    internal const int LiveIndexDirectoryNameLength = 64;

    internal static string StagingRoot(string indexRoot) => Path.Combine(indexRoot, StagingDirectoryName);

    internal static string TrashRoot(string indexRoot) => Path.Combine(indexRoot, TrashDirectoryName);

    /// <summary>
    /// 本次 job 的 staging 根：<c>&lt;IndexRoot&gt;/.staging/&lt;sha256(scopeKey)&gt;-&lt;jobToken&gt;</c>。
    /// 引擎会在这个根**之下**再建 <c>&lt;64 位 hex&gt;</c> 索引目录（同样是同卷，可直接重命名成 live）。
    /// </summary>
    internal static string ResolveStagingRoot(string indexRoot, string scopeKey, string jobToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobToken);

        return Path.Combine(StagingRoot(indexRoot), EntryName(scopeKey, jobToken));
    }

    /// <summary>
    /// 旧 live 的中转路径：<c>&lt;IndexRoot&gt;/.trash/&lt;sha256(scopeKey)&gt;-&lt;utcUtcTicks&gt;</c>。
    /// 用 ticks（D19 定宽）保证同一 scope 的多次切换不会撞名，且名字按时间可排序（便于人工排查）。
    /// </summary>
    internal static string ResolveTrashDirectory(string indexRoot, string scopeKey, DateTimeOffset utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        var ticks = utcNow.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
        return Path.Combine(TrashRoot(indexRoot), EntryName(scopeKey, ticks));
    }

    private static string EntryName(string scopeKey, string token) => $"{Sha256Hex(scopeKey)}-{token}";

    /// <summary>sha256 的小写 hex（供给层命名用；输入是 scope 键，不是路径）。</summary>
    internal static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>是否为 live scope 索引目录名（64 位小写 hex）。</summary>
    internal static bool IsLiveIndexDirectoryName(string name)
    {
        if (name.Length != LiveIndexDirectoryNameLength)
            return false;

        foreach (var ch in name)
        {
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))
                continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// 实测**全部 live scope 索引目录**的字节合计 —— 「配置集合总预算」判定式的 live 侧。
    /// 只统计 <c>&lt;IndexRoot&gt;</c> 下名字为 64 位小写 hex 的目录（保留目录与其它的名字一律不计）。
    /// <para>
    /// 索引根不存在 ⇒ 返回 0（正常首建场景；本方法**不创建**任何目录，保证 Plan 依旧零写入）。
    /// 枚举失败（IO / 权限）**向上抛** —— 量不出 live 用量时不允许默默按 0 判定（fail-closed）。
    /// </para>
    /// </summary>
    internal static long MeasureLiveIndexBytes(string indexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);

        if (!Directory.Exists(indexRoot))
            return 0;

        long total = 0;
        foreach (var child in Directory.EnumerateDirectories(indexRoot))
        {
            if (IsLiveIndexDirectoryName(Path.GetFileName(child)))
                total += MeasureDirectoryBytes(child);
        }

        return total;
    }

    /// <summary>
    /// 递归统计目录内文件字节数（目录不存在 ⇒ 0）。
    /// 单个目录/单个文件的 IO 与权限异常**局部化**（与 <c>FileSystemSupplyInventory</c> 同口径），
    /// 不因一个坏条目放弃整次统计。
    /// </summary>
    internal static long MeasureDirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不到大小的条目不计入；不因此中断统计。
                }
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var sub in subDirectories)
                pending.Push(sub);
        }

        return total;
    }

    /// <summary>
    /// 尽力删除一个文件或目录树：**不抛异常**（清理失败不是供给成败的判据），失败原因经
    /// <paramref name="error"/> 返回，由调用方如实登记。
    /// </summary>
    internal static bool TryDelete(string path, out string? error)
    {
        error = null;
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            // 已不存在 = 目标状态达成。
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// 一次残留清理的结果（R5）：清掉的条目名（相对 IndexRoot）+ 遇到的问题。
/// </summary>
/// <param name="RemovedEntries">被清掉的条目（形如 <c>.staging/&lt;name&gt;</c>），按清理顺序。</param>
/// <param name="Error">清理过程中遇到的问题；一切正常为 null。</param>
internal sealed record ArtifactCleanupResult(IReadOnlyList<string> RemovedEntries, string? Error)
{
    internal static ArtifactCleanupResult Empty { get; } = new(Array.Empty<string>(), null);

    /// <summary>把另一个清理错误并入本结果（条目名保持不变）；<paramref name="error"/> 为 null 时原样返回。</summary>
    internal ArtifactCleanupResult WithError(string? error) =>
        error is null ? this : new ArtifactCleanupResult(RemovedEntries, MergeError(Error, error));

    internal static string? MergeError(string? first, string? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return $"{first}；{second}";
    }
}
