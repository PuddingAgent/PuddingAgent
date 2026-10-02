using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 磁盘枚举的一个条目（**只有元数据**：不读正文、不算 hash）。
/// <para>
/// stat 读不到时为 null —— 与 <see cref="CodeSourceObservation"/> 同口径：不得用扫描时刻顶替，
/// 否则「读不到」会被伪装成「读到了某个没变的版本」。
/// </para>
/// </summary>
/// <param name="FilePath">绝对路径（保留枚举方给的大小写）。</param>
/// <param name="LastWriteTimeUtc">最后写入时刻；读不到时为 null。</param>
/// <param name="Length">字节长度；读不到时为 null。</param>
public sealed record CodeSourceDiskEntry(
    string FilePath,
    DateTimeOffset? LastWriteTimeUtc,
    long? Length);

/// <summary>
/// 路径忽略规则的**注入端口**（D3，2026-10-02）。
/// <para>
/// 规则集的**唯一真源**在叶子组件 <c>PuddingPathFiltering</c>（名字级噪声名单 + 与
/// <c>git check-ignore</c> 对齐的 .gitignore 语义），组件只通过本端口消费，不在这里另建名单；
/// 否则两套名单必然漂移，而漏枚举一个路径就等于「索引悄悄少了文件」。
/// </para>
/// <para>
/// 更正（2026-10-02）：本注释此前写成「组件不反向引用 PuddingPathFiltering」。事实上
/// <c>PuddingCodeIndex</c> 已把它作为允许的叶子依赖引用（<c>IndexExcludePatterns</c> 等），
/// 因此默认实现 <c>WorkspaceCodeSourceIgnoreRules</c> 就放在组件内；宿主要换规则时替换本端口即可。
/// </para>
/// </summary>
public interface ICodeSourceIgnoreRules
{
    /// <summary>该路径是否被排除在语义范围之外（目录被排除即整棵子树不枚举）。</summary>
    /// <param name="absolutePath">绝对路径。</param>
    /// <param name="isDirectory">该路径是目录时为 true。</param>
    bool IsIgnored(string absolutePath, bool isDirectory);
}

/// <summary>一次磁盘枚举的结果。</summary>
/// <param name="Entries">枚举到的文件条目（不含被忽略的路径）。</param>
/// <param name="RootUsable">
/// 根是否可读。false 时「不在磁盘上」这一结论对每个路径都成立，调用方**绝不能**据此删除任何记录。
/// </param>
/// <param name="Complete">
/// 是否完整枚举了整个语义范围。false 表示有子树没读到、或触发了条目上限 ——
/// 此时只能得出「本轮没看到某些路径」，不能得出「这些路径已被删除」。
/// </param>
/// <param name="IncompleteReason">不完整/不可用的原因（诊断与验收计数用）。</param>
public sealed record CodeSourceScanOutcome(
    IReadOnlyList<CodeSourceDiskEntry> Entries,
    bool RootUsable,
    bool Complete,
    string? IncompleteReason);

/// <summary>磁盘枚举原因串（稳定字符串）。</summary>
public static class CodeSourceScanReasons
{
    /// <summary>根路径为空。</summary>
    public const string RootPathMissing = "root_path_missing";

    /// <summary>根不存在 / 不可读 / 枚举失败。</summary>
    public const string RootUnusable = "root_unusable";

    /// <summary>某棵子树在枚举中途失败：本轮不完整。</summary>
    public const string SubtreeUnreadable = "subtree_unreadable";

    /// <summary>条目数达到上限：本轮不完整（宁可下一轮继续，也不把未枚举的路径当删除）。</summary>
    public const string EntryLimitReached = "entry_limit_reached";
}

/// <summary>
/// 枚举一个 scope root 下**候选源文件元数据**的端口（可替换，便于在组件边界独立测试）。
/// <para>实现必须是**只读**的：不写文件、不改时间戳、不读正文。</para>
/// </summary>
public interface ICodeSourceScanner
{
    /// <summary>枚举给定根下的候选文件（应用注入的忽略规则）。</summary>
    /// <param name="rootPath">语义范围根（绝对路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}

/// <summary>一次完整清单校准（扫描 + 判定 + 账本登记）的结果。</summary>
/// <param name="ChangeSet">真实变更集（需要动作的路径 + 完整性结论 + 建议水位）。</param>
/// <param name="CapturedVersion">
/// 本次登记在账本里的**捕获版本**：调用方执行完这批动作后必须把它原样回报给账本，
/// 只确认被捕获的版本（执行期间的新变化会被账本置为「还有工作」）。
/// </param>
/// <param name="CapabilityMissing">
/// 存储没有实现 <see cref="ICodeSourceMaintenanceStore"/>：本次没有读取/写入 manifest 与账本，
/// 只产出了「磁盘上现在有什么」。调用方必须据此保持既有行为，而不是猜测。
/// </param>
/// <param name="ScanStartedUtc">
/// 本轮扫描的开始时刻。执行完这批动作后，调用方把它连同「扫描是否完整」与「未解决路径数」一起回报给
/// 账本的提交接缝 —— **扫描水位只在那里推进**，扫描本身不改水位。
/// </param>
public sealed record CodeSourceScanRun(
    CodeSourceChangeSet ChangeSet,
    long CapturedVersion,
    DateTimeOffset ScanStartedUtc,
    bool CapabilityMissing);

/// <summary>一次校准运行的输入（除范围与根之外的事实）。</summary>
/// <param name="WatcherHints">watcher 提示过的路径（低延迟提示，不保证覆盖）。</param>
/// <param name="ChangedDuringScan">扫描期间被观察到又变化的路径（本轮不得定论）。</param>
/// <param name="ConsumerInputs">各消费者当前输入指纹（解析器策略 + 语义输入）。</param>
/// <param name="DeepVerify">本轮是否做深度核验（周期体检 / 溢出补偿）。</param>
/// <param name="ScanStartedUtc">
/// 本轮扫描开始时刻；为 null 时用服务自己的时钟（真实扫描总有开始时刻）。
/// </param>
public sealed record CodeSourceScanOptions(
    IReadOnlyCollection<string>? WatcherHints = null,
    IReadOnlyCollection<string>? ChangedDuringScan = null,
    IReadOnlyCollection<CodeConsumerInputFingerprint>? ConsumerInputs = null,
    bool DeepVerify = false,
    DateTimeOffset? ScanStartedUtc = null,
    bool Targeted = false);
