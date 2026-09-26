using Directory = System.IO.Directory;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 一次语料走查的结果：文件清单 + **逐目录失败计数**（二者缺一不可）。
/// <para>
/// ⚠️ <see cref="FailedDirectoryCount"/> 是「能否据此产生批量 Delete」的**唯一依据**：
/// 只要有一个目录没能枚举出来，就**不能**把「磁盘上没有这个路径」当成「这个文件已被删除」——
/// 那会把一次瞬时的权限 / IO 故障放大成「整棵子树被从索引里删掉」。
/// </para>
/// </summary>
/// <param name="Files">枚举到的文件绝对路径（按 <see cref="StringComparer.Ordinal"/> 排序 ⇒ 顺序确定）。</param>
/// <param name="FailedDirectoryCount">枚举失败的目录数（局部隔离计数；0 ⇒ 走查是完整的）。</param>
/// <param name="FirstFailure">首个失败原因（可诊断）；无失败时为 null。</param>
internal readonly record struct CorpusScanResult(
    IReadOnlyList<string> Files,
    int FailedDirectoryCount,
    string? FirstFailure)
{
    /// <summary>走查是否完整（完整 ⇒ 才允许「索引路径 − 磁盘路径 = Delete 候选」这一步）。</summary>
    internal bool IsComplete => FailedDirectoryCount == 0;
}

/// <summary>
/// 维护侧（补偿扫描 / 低优先级体检 / 只读完整性探针）**共用**的语料走查器。
/// <para>
/// <b>为什么必须共用同一份</b>：补偿扫描与体检探针都在回答「磁盘上现在有哪些可索引路径」。
/// 两份实现一旦漂移，「体检说 Healthy」与「补偿扫描说需要 Delete」会同时成立，界面上无法解释。
/// </para>
/// <para>
/// <b>与既有全量构建遍历的关系</b>：构建路径的剪枝遍历是 <c>LuceneSearchEngine.EnumerateFilesPruned</c>（private），
/// 本片不得改引擎文件（S3d 范围只允许落 <c>ProbeIntegrityAsync</c>），因此这里提供**维护侧**的同语义实现：
/// 显式栈 DFS **单次遍历** + 噪声目录剪枝（<see cref="FullTextIndexOptions.ExcludedDirectoryNames"/>，
/// 与构建路径同源 = <c>PathNoiseRules</c>，本类**不复刻清单**）。
/// </para>
/// <para>
/// <b>异常契约</b>（与 <c>FileCandidateCollector</c> 同口径）：
/// 文件系统类异常（<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> /
/// <see cref="System.Security.SecurityException"/>）**就地局部化**到单个目录并计数，
/// <b>绝不</b>终止整轮走查；<see cref="OperationCanceledException"/> 则在吞异常的区间**之外**检查并原样外抛
/// —— 取消是控制流，不是「坏目录」。
/// </para>
/// <para>本类只读：不创建目录、不写文件、不 stat 文件（文件级判定由调用方经 <c>FileCandidateCollector</c> 完成）。</para>
/// </summary>
internal static class MaintenanceCorpusScan
{
    /// <summary>
    /// 枚举 <paramref name="scopeRoot"/> 之下的全部文件路径（**单次** DFS + 噪声目录剪枝）。
    /// </summary>
    /// <param name="scopeRoot">scope 语料根（绝对路径）。</param>
    /// <param name="options">索引选项（取噪声目录清单与路径排除规则）。</param>
    /// <param name="cancellationToken">取消令牌（在吞异常的区间之外检查）。</param>
    internal static CorpusScanResult EnumerateFiles(
        string scopeRoot,
        FullTextIndexOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<string>();
        var failedDirectories = 0;
        string? firstFailure = null;

        if (!Directory.Exists(scopeRoot))
        {
            // 语料根本身不存在：**不是**「磁盘上没有文件」，而是「这次走查没有意义」。
            // 调用方据此**不得**产生任何 Delete 候选（否则驱动器未挂载会把索引整片删掉）。
            return new CorpusScanResult(
                Array.Empty<string>(),
                FailedDirectoryCount: 1,
                FirstFailure: $"语料根不存在：{scopeRoot}");
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(scopeRoot));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Length == 0)
                        continue;

                    // 噪声目录剪枝：命中即不再进入（与构建路径同一份清单来源）
                    if (options.ExcludedDirectoryNames.Contains(name))
                        continue;

                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                failedDirectories++;
                firstFailure ??= $"{current} :: 子目录枚举失败（{ex.GetType().Name}: {ex.Message}）";
            }

            try
            {
                files.AddRange(Directory.EnumerateFiles(current));
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                failedDirectories++;
                firstFailure ??= $"{current} :: 文件枚举失败（{ex.GetType().Name}: {ex.Message}）";
            }
        }

        // 顺序确定（体检切片游标 / 变更集顺序 / 测试断言都依赖它）
        files.Sort(StringComparer.Ordinal);

        return new CorpusScanResult(files, failedDirectories, firstFailure);
    }

    /// <summary>该异常是否属于「文件系统类」——就地局部化而不是终止整轮走查。</summary>
    internal static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
