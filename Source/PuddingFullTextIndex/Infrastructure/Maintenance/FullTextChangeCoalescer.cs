using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 一条观察记录所描述的**最终状态**（方案 §3.1 合并规则里的「flush 时重新观察最终状态」）。
/// <para>
/// ⚠️ 这是**入参**而不是本层自己探测出来的：文件系统探测（stat / 扩展名判定 / 噪声判定 / 空文件 / 超限）
/// 属于 IO 与策略判定，由调用方（S3 的收集 / 补偿 / 体检层）完成 —— 本层因此保持**零 IO**，
/// 也**不复制**噪声名单（唯一真源是 <c>PathNoiseRules</c>）或扩展名白名单（唯一真源是 <c>FullTextIndexOptions</c>）。
/// </para>
/// </summary>
public enum FullTextPathObservation
{
    /// <summary>当前是可索引的普通文件 ⇒ 最终动作 <see cref="FullTextChangeKind.Upsert"/>。</summary>
    IndexableFile = 0,

    /// <summary>当前不存在（含 rename 的旧路径、离线删除）⇒ <c>Delete</c>。</summary>
    Missing = 1,

    /// <summary>当前是目录 ⇒ <c>Delete</c>。</summary>
    Directory = 2,

    /// <summary>扩展名已不在允许集合内（策略收紧 / 文件被改名成其它后缀）⇒ <c>Delete</c>。</summary>
    ExtensionNotAllowed = 3,

    /// <summary>命中噪声规则（目录名 / 文件名）⇒ <c>Delete</c>。</summary>
    Noisy = 4,

    /// <summary>当前是空文件 ⇒ <c>Delete</c>。</summary>
    Empty = 5,

    /// <summary>当前超出单文件大小上限 ⇒ <c>Delete</c>。</summary>
    Oversized = 6,

    /// <summary>
    /// 暂时不可读（共享冲突 / 权限瞬态 / 提取器故障）⇒ **不产出动作**，保留旧索引并标记待重试。
    /// <para>⚠️ 绝不能在这里改成 <c>Delete</c>：那等于把「读不到」当成「不存在」，会误删仍然有效的旧文档。</para>
    /// </summary>
    Unreadable = 7,
}

/// <summary>观察记录被**拒绝**的原因（本层不做静默丢弃：拒绝项一律随结果返回）。</summary>
public enum FullTextPathRejectionReason
{
    /// <summary>路径为空 / 仅空白。</summary>
    BlankPath = 0,

    /// <summary>来源位标为 0（无任何来源）—— 生产者必须声明来源，否则诊断链断裂。</summary>
    NoSource = 1,

    /// <summary>路径无法规范化（非法字符 / 过长 / 不支持的形式）。</summary>
    Unnormalizable = 2,

    /// <summary>路径落在 scope 之外（或就是 scope 根本身）—— 越界路径绝不进入本 scope 的变更集。</summary>
    OutsideScope = 3,
}

/// <summary>一条被拒绝的观察记录（值 + 原因枚举 + 可读消息，三者齐全）。</summary>
/// <param name="FullPath">被拒绝的原始路径（无法规范化时就是原样入参）。</param>
/// <param name="Reason">拒绝原因。</param>
/// <param name="Message">可读消息。</param>
public sealed record FullTextRejectedPath(
    string FullPath,
    FullTextPathRejectionReason Reason,
    string Message);

/// <summary>
/// 一条**观察记录**：某个路径在本轮窗口内被观察到的事实（方案 §3.1 的输入侧）。
/// </summary>
/// <param name="FullPath">绝对路径（保留观察方给的大小写，用于输出 / 诊断 / 读盘）。</param>
/// <param name="Sources">来源位标；必须非 0（三个触发源之一）。</param>
/// <param name="Observation">观察到的最终状态。</param>
/// <param name="LastWriteUtc">观察到的最后写入时刻（UTC）；读不到为 null（<b>不得</b>顶替）。</param>
/// <param name="Length">观察到的字节长度；读不到为 null。</param>
public sealed record FullTextChangeObservation(
    string FullPath,
    FullTextChangeSource Sources,
    FullTextPathObservation Observation,
    DateTimeOffset? LastWriteUtc = null,
    long? Length = null);

/// <summary>
/// 折叠结果：最终动作 + 待重试路径 + 拒绝路径（三者都必须被调用方消费，缺一个就是静默丢信息）。
/// </summary>
/// <param name="Changes">按规范化路径去重后的最终动作；同一路径最多出现一次，顺序为该路径**首次出现**的顺序（稳定）。</param>
/// <param name="DeferredPaths">暂时不可读的路径：保留旧索引、**不产出动作**、待下轮重试。</param>
/// <param name="RejectedPaths">被拒绝的观察记录（越界 / 空路径 / 无来源 / 无法规范化）。</param>
public sealed record FullTextCoalesceResult(
    IReadOnlyList<FullTextFileChange> Changes,
    IReadOnlyList<string> DeferredPaths,
    IReadOnlyList<FullTextRejectedPath> RejectedPaths);

/// <summary>
/// 变更折叠的**纯函数**层（方案 §3.1 合并规则）：零 IO、零线程、不读时钟、不做文件系统探测。
/// <para>
/// 语义：
/// <list type="number">
/// <item><description>同一规范化路径在一个窗口内**最多保留一个最终动作**（per-path latest-wins：以最后一条观察为准）。</description></item>
/// <item><description>多来源的 <see cref="FullTextChangeSource"/> 按**位或**合并（诊断用，不参与正确性判定）。</description></item>
/// <item><description>依据**最终观察到的状态**决策：可索引普通文件 ⇒ Upsert；不存在 / 变目录 / 扩展名不再允许 /
/// 命中噪声 / 空文件 / 超限 ⇒ Delete；暂时不可读 ⇒ 不产出动作 + 待重试。</description></item>
/// <item><description>rename ⇒ 旧路径 <see cref="FullTextChangeKind.Delete"/> + 新路径 <see cref="FullTextChangeKind.Upsert"/>
/// （见 <see cref="FromRename"/>；这正是「watcher 只收到一侧事件」也能被补偿扫描校准的原因）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 本类**不含**任何 SHA256：路径规范化只服务去重与大小写不敏感比较，
/// 命名哈希的唯一实现仍是 <c>FullTextIndexPaths</c>。
/// </para>
/// </summary>
public static class FullTextChangeCoalescer
{
    /// <summary>
    /// 把观察序列折叠成最终动作集合。
    /// </summary>
    /// <param name="observations">观察序列（顺序即发生顺序；同一路径以最后一条为准）。</param>
    /// <param name="scopeRoot">scope 语料根（绝对路径）；越界路径被拒绝而不是静默丢弃。</param>
    public static FullTextCoalesceResult Coalesce(
        IEnumerable<FullTextChangeObservation> observations,
        string scopeRoot)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);

        var scopeKey = NormalizeComparisonKey(scopeRoot);

        var order = new List<string>();
        var latest = new Dictionary<string, FullTextChangeObservation>(StringComparer.Ordinal);
        var mergedSources = new Dictionary<string, FullTextChangeSource>(StringComparer.Ordinal);
        var displayPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<FullTextRejectedPath>();

        foreach (var observation in observations)
        {
            if (observation is null)
                continue;

            if (string.IsNullOrWhiteSpace(observation.FullPath))
            {
                rejected.Add(new FullTextRejectedPath(
                    observation.FullPath ?? string.Empty,
                    FullTextPathRejectionReason.BlankPath,
                    "观察记录的路径为空或仅含空白字符。"));
                continue;
            }

            if (observation.Sources == default)
            {
                rejected.Add(new FullTextRejectedPath(
                    observation.FullPath,
                    FullTextPathRejectionReason.NoSource,
                    $"观察记录未声明任何来源（FullTextChangeSource 为 0）：{observation.FullPath}"));
                continue;
            }

            string key;
            try
            {
                key = NormalizeComparisonKey(observation.FullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected.Add(new FullTextRejectedPath(
                    observation.FullPath,
                    FullTextPathRejectionReason.Unnormalizable,
                    $"路径无法规范化（{ex.GetType().Name}）：{observation.FullPath}"));
                continue;
            }

            if (!SupplyScopeNormalizer.IsWithinScopeKey(scopeKey, key))
            {
                rejected.Add(new FullTextRejectedPath(
                    observation.FullPath,
                    FullTextPathRejectionReason.OutsideScope,
                    $"路径不在 scope 之内（或就是 scope 根本身），已拒绝而不是静默丢弃：{observation.FullPath}"));
                continue;
            }

            if (!latest.ContainsKey(key))
            {
                order.Add(key);
                mergedSources[key] = observation.Sources;
            }
            else
            {
                mergedSources[key] |= observation.Sources;
            }

            latest[key] = observation;      // per-path latest-wins
            displayPath[key] = observation.FullPath;
        }

        var changes = new List<FullTextFileChange>(order.Count);
        var deferred = new List<string>();

        foreach (var key in order)
        {
            var observation = latest[key];
            var sources = mergedSources[key];
            var path = displayPath[key];

            switch (observation.Observation)
            {
                case FullTextPathObservation.IndexableFile:
                    changes.Add(new FullTextFileChange(
                        path,
                        FullTextChangeKind.Upsert,
                        observation.LastWriteUtc,
                        observation.Length,
                        sources));
                    break;

                case FullTextPathObservation.Unreadable:
                    // 保留旧索引 + 待重试：**不产出动作**（产出 Delete 就等于把「读不到」当「不存在」）。
                    deferred.Add(path);
                    break;

                case FullTextPathObservation.Missing:
                case FullTextPathObservation.Directory:
                case FullTextPathObservation.ExtensionNotAllowed:
                case FullTextPathObservation.Noisy:
                case FullTextPathObservation.Empty:
                case FullTextPathObservation.Oversized:
                    changes.Add(new FullTextFileChange(
                        path,
                        FullTextChangeKind.Delete,
                        observation.LastWriteUtc,
                        observation.Length,
                        sources));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(observations),
                        observation.Observation,
                        "未知的 FullTextPathObservation 取值：拒绝猜测其在正确性上的含义。");
            }
        }

        return new FullTextCoalesceResult(changes, deferred, rejected);
    }

    /// <summary>
    /// rename 的**显式折叠输入**（方案 §3.1：「rename 折叠为旧路径 Delete、新路径 Upsert」）。
    /// 产出两条观察：旧路径「不存在」（⇒ Delete）、新路径「可索引文件」（⇒ Upsert）。
    /// <para>
    /// ⚠️ 前提：调用方已在新路径上做过最终 stat 与过滤，确认它是可索引普通文件；本方法不做任何探测（零 IO）。
    /// </para>
    /// </summary>
    /// <param name="oldFullPath">rename 前路径。</param>
    /// <param name="newFullPath">rename 后路径。</param>
    /// <param name="sources">来源位标（通常为 <see cref="FullTextChangeSource.Watcher"/>）。</param>
    /// <param name="newLastWriteUtc">新路径观察到的 mtime；读不到为 null。</param>
    /// <param name="newLength">新路径观察到的长度；读不到为 null。</param>
    public static IReadOnlyList<FullTextChangeObservation> FromRename(
        string oldFullPath,
        string newFullPath,
        FullTextChangeSource sources,
        DateTimeOffset? newLastWriteUtc = null,
        long? newLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFullPath);

        return
        [
            new FullTextChangeObservation(oldFullPath, sources, FullTextPathObservation.Missing),
            new FullTextChangeObservation(newFullPath, sources, FullTextPathObservation.IndexableFile, newLastWriteUtc, newLength),
        ];
    }

    /// <summary>
    /// 折叠 / 比较用的规范化键：绝对化 → **保盘根**去尾分隔符 → 分隔符统一 <c>\</c> + 不变文化小写。
    /// <para>
    /// ⚠️ 只服务**去重与大小写不敏感比较**（Windows First）。它**不是**命名哈希的输入，
    /// **不得**用于推导索引目录名（哈希唯一真源是 <c>FullTextIndexPaths.ResolveIndexDirectory</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 去尾分隔符必须走 <see cref="SupplyScopeNormalizer.TrimTrailingSeparators"/>（保盘根）：
    /// 裸 <c>TrimEnd</c> 会把 <c>C:\</c> 裁成 <c>C:</c>，与供给侧的 <c>c:\</c> 不同键
    /// ⇒ 推出不同租约文件 ⇒ 同一语料根上维护直写与供给整目录替换不互斥（缺陷 ①）。
    /// </para>
    /// </summary>
    /// <param name="fullPath">绝对路径（大小写与尾分隔符不敏感）。</param>
    public static string NormalizeComparisonKey(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // scope 键口径复用既有单一真源（保盘根裁剪 + 分隔符统一 + 不变文化小写），不另立第三套。
        return SupplyScopeNormalizer.ToScopeKey(SupplyScopeNormalizer.TrimTrailingSeparators(Path.GetFullPath(fullPath.Trim())));
    }

    /// <summary>
    /// 该路径是否落在 scope 之内（**不含** scope 根自身；根是目录，不可能是文件变更对象）。
    /// <para>
    /// 语义选择已记录在交付报告中：越界路径采取「**拒绝并如实返回**」
    /// （<see cref="FullTextPathRejectionReason.OutsideScope"/>），而不是静默过滤 —— 静默过滤会让
    /// 「scope 配错」表现为「一切正常但没有变更」。
    /// </para>
    /// </summary>
    /// <param name="scopeRoot">scope 语料根。</param>
    /// <param name="fullPath">待判定的文件路径。</param>
    public static bool IsWithinScope(string scopeRoot, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        return SupplyScopeNormalizer.IsWithinScopeKey(
            NormalizeComparisonKey(scopeRoot),
            NormalizeComparisonKey(fullPath));
    }
}
