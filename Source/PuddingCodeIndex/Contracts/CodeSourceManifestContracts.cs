using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 一个已索引文件的**源状态指纹**（D2，2026-10-02 高磁盘读取修复）。
/// <para>
/// 指纹存在的意义：<see cref="CodeFileRecord.LastIndexedAtUtc"/> 是「索引时刻」，<b>不是</b>源文件修改时刻，
/// 因此不能当作源版本；没有源版本就无法判断「磁盘上的这个文件是否还需要处理」。
/// </para>
/// <para>
/// <b><see cref="ContentHash"/> 来自实际参与提取/解析的那一份内容</b>：不能把旧内容的 hash 与新读到的
/// stat 拼在一起提交（那会把「读到的是新内容、解析的是旧快照」记成一致的版本）。
/// </para>
/// </summary>
/// <param name="LastWriteTimeUtc">观察到的最后写入时刻（UTC）。</param>
/// <param name="Length">观察到的字节长度。</param>
/// <param name="ContentHash">稳定读取后的内容 hash（大小写不敏感的比较无意义，按 Ordinal 比较）。</param>
public sealed record SourceFingerprint(
    DateTimeOffset LastWriteTimeUtc,
    long Length,
    string ContentHash)
{
    /// <summary>stat 是否一致（mtime + length）。内容是否一致必须看 <see cref="HasSameContent"/>。</summary>
    public bool HasSameStat(SourceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return LastWriteTimeUtc == other.LastWriteTimeUtc && Length == other.Length;
    }

    /// <summary>stat 与内容都一致。</summary>
    public bool HasSameContent(SourceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return HasSameStat(other)
            && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
    }
}

/// <summary>
/// 一个消费者（全文 / 某个语言 provider）在该文件上**已提交**的版本。
/// <para>
/// 不能用一个成功标记替代所有消费者：全文与各语言 provider 分别推进自己的水位，一个消费者提交成功
/// 不代表另一个也完成。两个指纹（解析器策略、语义输入）则是「内容没变但结论已失效」的判据：
/// 工具链升级、项目/依赖配置变化都会改变同样的内容应当绑定到的结果。
/// </para>
/// </summary>
/// <param name="ProviderId">消费者标识（如 <c>fulltext</c> / <c>csharp</c> / <c>typescript</c>）。</param>
/// <param name="ParserPolicyFingerprint">解析器/提取策略指纹（工具链版本、提取选项）。</param>
/// <param name="SemanticInputFingerprint">语义输入指纹（项目文件、依赖、绑定选项等实际参与解析的输入）。</param>
/// <param name="AppliedVersion">该消费者已提交的维护版本（账本内的单调序号）。</param>
public sealed record AppliedFileVersion(
    string ProviderId,
    string ParserPolicyFingerprint,
    string SemanticInputFingerprint,
    long AppliedVersion);

/// <summary>持久 manifest 的一行：一个 scope 内一条规范化路径的源状态与各消费者水位。</summary>
/// <param name="FilePath">绝对路径（保留观察方给的大小写；比对按组件的路径比较器）。</param>
/// <param name="Fingerprint">已提交的源指纹；从未成功提交过时为 null（<b>首次没有基线必须处理</b>）。</param>
/// <param name="AppliedVersions">各消费者已提交的版本；空表示还没有任何消费者提交过。</param>
/// <param name="Complete">
/// <c>false</c> 表示这一行来自一次不完整/失败的提交（例如只写了 manifest、索引未提交）。
/// 不完整的行不得当作「已应用」，必须重新核对。
/// </param>
public sealed record CodeSourceEntry(
    string FilePath,
    SourceFingerprint? Fingerprint,
    IReadOnlyList<AppliedFileVersion> AppliedVersions,
    bool Complete = true);

/// <summary>
/// 磁盘元数据观察（stat 扫描的产物）。
/// <para>
/// ⚠️ <see cref="LastWriteTimeUtc"/> 与 <see cref="Length"/> 是**观察值**：读不到就是 null，
/// 不得用扫描时刻 / <c>UtcNow</c> / <c>0</c> 顶替 —— 顶替会把「读不到」伪装成「读到了某个值」，
/// 下游据此比较会得出虚假的「未变化」结论。
/// </para>
/// </summary>
/// <param name="FilePath">绝对路径。</param>
/// <param name="LastWriteTimeUtc">观察到的最后写入时刻；读不到时为 null。</param>
/// <param name="Length">观察到的长度；读不到时为 null。</param>
public sealed record CodeSourceObservation(
    string FilePath,
    DateTimeOffset? LastWriteTimeUtc,
    long? Length);

/// <summary>
/// 一个消费者当前的输入指纹：与 <see cref="AppliedFileVersion"/> 同口径比较，用来发现
/// 「文件内容没变，但该消费者应当重新绑定」。
/// </summary>
/// <param name="ProviderId">消费者标识，与 <see cref="AppliedFileVersion.ProviderId"/> 同口径。</param>
/// <param name="ParserPolicyFingerprint">解析器/提取策略指纹。</param>
/// <param name="SemanticInputFingerprint">语义输入指纹。</param>
public sealed record CodeConsumerInputFingerprint(
    string ProviderId,
    string ParserPolicyFingerprint,
    string SemanticInputFingerprint);

/// <summary>
/// 一个候选路径的**来源位标**（D2 三源合流）：watcher 提示、mtime/stat 扫描、溢出或深度核验、配置变化。
/// <para>多来源命中同一路径按位或合并，只用于诊断与优先级；正确性由最终动作与磁盘事实决定。</para>
/// </summary>
[Flags]
public enum CodeSourceChangeSource
{
    /// <summary>没有来源（不会出现在结果里）。</summary>
    None = 0,

    /// <summary>FileSystemWatcher 的实时提示。</summary>
    Watcher = 1,

    /// <summary>启动/周期 mtime + stat 扫描。</summary>
    MTimeScan = 2,

    /// <summary>溢出、目录变化或按周期触发的深度核验。</summary>
    IntegrityCheck = 4,

    /// <summary>消费者输入（解析器策略 / 语义输入）变化。</summary>
    Configuration = 8,
}

/// <summary>
/// 一个路径的**最终期望动作**（不描述过程）。
/// <para>
/// 「提取失败保留旧文档」「单文件失败只隔离该文件」属于执行层语义，不在这里表达。
/// </para>
/// </summary>
public enum CodeSourceAction
{
    /// <summary>已提交结果对每个消费者都仍然有效：不读正文、不解析、不写索引。</summary>
    Reuse = 0,

    /// <summary>内容与 stat 都一致：只刷新 manifest 里的源元数据（例如补齐 hash），不改索引文档/图。</summary>
    RefreshFingerprintOnly = 1,

    /// <summary>内容一致但消费者输入（策略/语义）变了：重新绑定，但不重新提取正文。</summary>
    RebindConsumers = 2,

    /// <summary>内容变化或尚不可知：稳定读取 + 提取 + 原子提交该文件。</summary>
    ReindexContent = 3,

    /// <summary>确认不在磁盘上（仅完整成功的扫描可以得出），删除该路径的索引记录并修复依赖。</summary>
    Delete = 4,

    /// <summary>
    /// 本轮不能定论（扫描期间又变了、扫描不完整、根不可用，或位于 racy 窗口且尚无内容核验）：
    /// 保留记录、下一轮重新核对，<b>绝不</b>当作删除。
    /// </summary>
    Deferred = 5,
}

/// <summary>候选/动作的原因（稳定字符串，用于诊断与验收计数）。</summary>
public static class CodeSourceChangeReasons
{
    /// <summary>manifest 里没有这个路径（新文件，或首次没有基线）。</summary>
    public const string NewFile = "new_file";

    /// <summary>stat（mtime/length）与已提交指纹不同。</summary>
    public const string StatChanged = "stat_changed";

    /// <summary>stat 读不到（权限/IO 失败）：不得据此断言未变化。</summary>
    public const string StatUnreadable = "stat_unreadable";

    /// <summary>watcher 提示过该路径：内容需要核验。</summary>
    public const string WatcherHint = "watcher_hint";

    /// <summary>mtime 落在扫描水位 - 重叠窗口内（同时间粒度/时钟回拨的边界候选）。</summary>
    public const string RacyWindow = "racy_window";

    /// <summary>深度核验（周期体检 / 溢出补偿）。</summary>
    public const string DeepVerify = "deep_verify";

    /// <summary>解析器/提取策略指纹变化。</summary>
    public const string ParserPolicyChanged = "parser_policy_changed";

    /// <summary>语义输入（项目/依赖/绑定选项）指纹变化。</summary>
    public const string SemanticInputChanged = "semantic_input_changed";

    /// <summary>该消费者从未在此文件上提交过。</summary>
    public const string ConsumerNeverApplied = "consumer_never_applied";

    /// <summary>内容 hash 与已提交指纹不同。</summary>
    public const string ContentChanged = "content_changed";

    /// <summary>内容 hash 与已提交指纹相同（stat 却变了：保留来源元数据的刷新）。</summary>
    public const string ContentUnchanged = "content_unchanged";

    /// <summary>manifest 有、磁盘未见，但本次扫描不完整或根不可用。</summary>
    public const string ScanIncomplete = "scan_incomplete";

    /// <summary>扫描期间该路径又发生变化：最终状态未知。</summary>
    public const string ChangedDuringScan = "changed_during_scan";

    /// <summary>
    /// 提供的内容 hash 与当前观察到的 stat 不一致：读取与写入竞争（读到的是另一份版本），
    /// 绝不能提交该结果 —— 重新观察后再来一轮。
    /// </summary>
    public const string UnstableRead = "unstable_read";

    /// <summary>manifest 有、完整扫描的磁盘枚举未见：确认删除。</summary>
    public const string MissingOnDisk = "missing_on_disk";

    /// <summary>根不可用（未挂载/改名/权限）：任何「不在磁盘上」的结论都不可信。</summary>
    public const string RootUnusable = "root_unusable";
}

/// <summary>一个路径的变更判定结果。</summary>
/// <param name="FilePath">绝对路径（保留请求里给的大小写）。</param>
/// <param name="Action">最终期望动作。</param>
/// <param name="Sources">来源位标（合并）。</param>
/// <param name="Reasons">判定原因（<see cref="CodeSourceChangeReasons"/>），诊断与验收用。</param>
/// <param name="Consumers">需要推进的消费者（<see cref="CodeSourceAction.RebindConsumers"/> / <see cref="CodeSourceAction.ReindexContent"/> 时有意义）。</param>
/// <param name="RequiresContentHash">
/// 调用方必须先稳定读取并计算内容 hash 再提交（<c>true</c> 时表示「不 hash 不得推进任何消费者水位」）。
/// </param>
public sealed record CodeSourceChange(
    string FilePath,
    CodeSourceAction Action,
    CodeSourceChangeSource Sources,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Consumers,
    bool RequiresContentHash);

/// <summary>
/// 一次变更判定请求：磁盘观察 + 持久 manifest + 三源提示 + 消费者输入指纹 + 扫描边界。
/// <para>
/// 纯数据：判定过程<b>不读文件、不访问数据库、不看时钟</b>，全部事实由调用方提供。
/// </para>
/// </summary>
/// <param name="Observations">本次扫描观察到的磁盘条目（stat 读不到时字段为 null）。</param>
/// <param name="Manifest">持久 manifest：规范化路径 → 该路径的源状态与消费者水位。</param>
/// <param name="ObservedContentHashes">
/// 调用方已稳定读取并计算出的内容 hash（只对需要核验的候选计算）。缺失表示「尚未 hash」——
/// 该路径必须按 <see cref="CodeSourceAction.ReindexContent"/> 处理，不得假定内容未变。
/// </param>
/// <param name="WatcherHints">watcher 提示过的路径（低延迟提示，不保证覆盖全部变化）。</param>
/// <param name="ChangedDuringScan">扫描期间又被观察到的路径：本轮不得得出最终结论（尤其不得删除）。</param>
/// <param name="ConsumerInputs">各消费者当前输入指纹（解析器策略 + 语义输入）。</param>
/// <param name="ScanStartedUtc">
/// 本轮扫描的<b>开始</b>时刻；watcher-only 批次为 null（此时不判定 racy 窗口，也不推进扫描水位）。
/// </param>
/// <param name="PreviousWatermarkUtc">本轮开始时读到的旧扫描水位；没有则 null（按陈旧处理）。</param>
/// <param name="RacyOverlap">mtime 水位重叠窗口：<c>mtime &gt;= ScanStartedUtc - RacyOverlap</c> 即为边界候选。</param>
/// <param name="DeepVerify">本轮是深度核验（周期体检 / 溢出补偿）：已知路径也要核验内容。</param>
/// <param name="ScopeComplete">本次扫描是否完整枚举了整个语义范围。</param>
/// <param name="RootUsable">语义范围根是否可读（未挂载/改名/权限失败时为 false）。</param>
public sealed record CodeSourceScanRequest(
    IReadOnlyList<CodeSourceObservation> Observations,
    IReadOnlyDictionary<string, CodeSourceEntry> Manifest,
    IReadOnlyDictionary<string, SourceFingerprint>? ObservedContentHashes = null,
    IReadOnlyCollection<string>? WatcherHints = null,
    IReadOnlyCollection<string>? ChangedDuringScan = null,
    IReadOnlyCollection<CodeConsumerInputFingerprint>? ConsumerInputs = null,
    DateTimeOffset? ScanStartedUtc = null,
    DateTimeOffset? PreviousWatermarkUtc = null,
    TimeSpan? RacyOverlap = null,
    bool DeepVerify = false,
    bool ScopeComplete = true,
    bool RootUsable = true);

/// <summary>
/// 一次变更判定的结果：**只包含需要动作的路径**（未变化的路径仅计数），加上水位与完整性结论。
/// </summary>
/// <param name="Changes">需要动作的路径（按路径去重，每路径最多一条）。</param>
/// <param name="ScanComplete"><c>true</c> 表示本轮是一次完整、根可用的核对（只有这种轮次可以得出删除结论）。</param>
/// <param name="NextScanWatermarkUtc">
/// 下一轮扫描水位：只有完整成功的扫描才推进到本轮 <see cref="CodeSourceScanRequest.ScanStartedUtc"/>；
/// 不完整/失败或 watcher-only 批次保持原水位（宁可重放，不可漏掉）。
/// <para>
/// 这是**建议**水位：调用方在任何需要动作的路径未能成功提交（提取失败、事务失败、取消）时必须保持
/// <see cref="CodeSourceScanRequest.PreviousWatermarkUtc"/>，按既有策略退避重试 ——
/// 已成功的路径可以凭指纹跳过重复工作，但全局水位不得掩盖失败。
/// </para>
/// </param>
/// <param name="ReuseCount">判定为「已提交结果仍然有效」的路径数（不产生 <see cref="Changes"/> 条目）。</param>
/// <param name="ReindexCount">需要读取内容并（重新）提交的路径数。</param>
/// <param name="RebindCount">只需重新绑定的路径数（内容一致、消费者输入变了）。</param>
/// <param name="MetadataRefreshCount">只需刷新源元数据的路径数。</param>
/// <param name="DeletedCount">确认删除的路径数。</param>
/// <param name="DeferredCount">本轮不能定论的路径数（扫描不完整、根不可用、扫描期间又变了）。</param>
/// <param name="UnresolvedMissingCount">manifest 有、磁盘未见且尚不能删除的路径数。</param>
public sealed record CodeSourceChangeSet(
    IReadOnlyList<CodeSourceChange> Changes,
    bool ScanComplete,
    DateTimeOffset? NextScanWatermarkUtc,
    int ReuseCount,
    int ReindexCount,
    int RebindCount,
    int MetadataRefreshCount,
    int DeletedCount,
    int DeferredCount,
    int UnresolvedMissingCount);
