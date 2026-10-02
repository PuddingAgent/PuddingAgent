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

/// <summary>一个待重试路径的账本记录（持久待办）。</summary>
/// <param name="FilePath">绝对路径。</param>
/// <param name="Reason">最近一次失败原因（<see cref="CodeSourceChangeReasons"/> 或执行层给出的原因串）。</param>
/// <param name="Attempts">连续失败次数（成功提交后清零）。</param>
/// <param name="FirstFailedAtUtc">第一次失败时刻。</param>
/// <param name="NextAttemptAtUtc">下一次允许尝试的时刻（退避阶梯）。</param>
public sealed record CodeSourceRetry(
    string FilePath,
    string Reason,
    int Attempts,
    DateTimeOffset FirstFailedAtUtc,
    DateTimeOffset NextAttemptAtUtc);

/// <summary>一个消费者推进到的水位（按消费者分别记录）。</summary>
/// <param name="ProviderId">消费者标识。</param>
/// <param name="AppliedVersion">该消费者已提交的维护版本。</param>
public sealed record CodeSourceProviderAdvance(string ProviderId, long AppliedVersion);

/// <summary>
/// 一轮提交要回报给账本的事实。**提交只确认被捕获的那个版本**：
/// 较新的变化由账本置为「又有变化」，由调用方继续补跑。
/// </summary>
/// <param name="Epoch">捕获该批次时的账本世代；与当前世代不符 ⇒ 该结果已过期，不得记入。</param>
/// <param name="CapturedVersion">批次开始时捕获的 <c>DesiredVersion</c>。</param>
/// <param name="ProviderAdvances">本轮真正提交成功的消费者水位。</param>
/// <param name="ScanStartedUtc">本轮扫描开始时刻（watcher-only 批次为 null）。</param>
/// <param name="ScanComplete">本轮是否完整枚举了语义范围且根可用。</param>
/// <param name="UnresolvedPathCount">本轮没能定论/没能提交的路径数（大于 0 时水位不得推进）。</param>
public sealed record CodeSourceCommitCompletion(
    long Epoch,
    long CapturedVersion,
    IReadOnlyList<CodeSourceProviderAdvance> ProviderAdvances,
    DateTimeOffset? ScanStartedUtc,
    bool ScanComplete,
    int UnresolvedPathCount);

/// <summary>一轮提交的结果。</summary>
public enum CodeSourceCommitOutcome
{
    /// <summary>提交被接受：捕获版本与当前期望一致，且没有未解决路径。</summary>
    Committed = 0,

    /// <summary>
    /// 提交被接受，但执行期间又出现了新变化（或仍有未解决路径）：已提交的消费者水位照常推进，
    /// 账本标记「又有变化」，调用方必须继续补跑。
    /// </summary>
    Superseded = 1,

    /// <summary>世代已过期（scope 被重建/重置后又在跑的旧批次）：结果不得记入，也不得推进任何水位。</summary>
    StaleEpoch = 2,
}

/// <summary>账本的只读快照（可序列化持久化；本阶段只定义形状与语义）。</summary>
/// <param name="WorkspaceId">工作空间。</param>
/// <param name="ScopeId">范围（也是 store 的 project id）。</param>
/// <param name="Epoch">世代：scope 重建/重置时递增，旧世代的在途批次一律作废。</param>
/// <param name="DesiredVersion">已观察到的期望版本（每次记录变化批次递增）。</param>
/// <param name="CommittedVersion">已提交版本（只前进，且只确认被捕获的版本）。</param>
/// <param name="ConsumerAppliedVersions">各消费者水位（全文/语言 provider 分别推进）。</param>
/// <param name="PendingRetries">待重试路径（失败退避；成功提交后移除）。</param>
/// <param name="ScanWatermarkUtc">
/// 成功扫描水位：只有「完整 + 根可用 + 没有未解决路径 + 捕获版本即当前期望」的轮次才推进；
/// 否则保持旧值（宁可重放，不可漏掉）。
/// </param>
/// <param name="DirtyAgain">执行期间又出现了新变化：调用方必须继续补跑。</param>
public sealed record CodeSourceMaintenanceLedgerState(
    string WorkspaceId,
    string ScopeId,
    long Epoch,
    long DesiredVersion,
    long CommittedVersion,
    IReadOnlyDictionary<string, long> ConsumerAppliedVersions,
    IReadOnlyDictionary<string, CodeSourceRetry> PendingRetries,
    DateTimeOffset? ScanWatermarkUtc,
    bool DirtyAgain);

/// <summary>一个 scope 的源维护状态：持久 manifest + 维护账本。</summary>
/// <param name="Manifest">规范化路径 → 该路径的源状态与消费者水位（无记录表示首次没有基线）。</param>
/// <param name="Ledger">该 scope 的维护账本快照（没有持久记录时是默认值：世代 0、各水位 0、无待重试）。</param>
public sealed record CodeSourceMaintenanceSnapshot(
    IReadOnlyDictionary<string, CodeSourceEntry> Manifest,
    CodeSourceMaintenanceLedgerState Ledger);

/// <summary>
/// 源维护状态的**可选持久化能力端口**（D2，2026-10-02）。
/// <para>
/// 故意不把成员加到 <see cref="ICodeIndexStore"/>：给共享端口加成员会破坏每一个实现者
/// （组件外的测试替身也在内），而「谁能持久化 manifest/账本」本来就是可选能力。
/// 维护链路按能力检测使用它；实现者不实现时保持既有行为，不引入兼容适配层。
/// </para>
/// <para>
/// 三条语义要求：
/// <list type="bullet">
///   <item><description><see cref="SaveSourceManifestAsync"/> 必须**单事务**：一批 upsert 与删除要么全成，要么全不成，
///     不允许出现「manifest 更新了但旧行还在」的中间态。</description></item>
///   <item><description>删除一个路径必须同时移除它的消费者水位（否则新文件会继承旧文件的已应用版本）。</description></item>
///   <item><description><see cref="SaveMaintenanceLedgerAsync"/> 必须拒绝**回退写入**（世代更旧、或同世代期望版本更旧），
///     并整体替换待重试集合 —— 账本是单调版本，不能因一次迟到写入而倒退。</description></item>
/// </list>
/// </para>
/// </summary>
public interface ICodeSourceMaintenanceStore
{
    /// <summary>读取一个 scope 的 manifest 与账本（没有记录时返回空 manifest 与默认账本）。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围（store 的 project id）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CodeSourceMaintenanceSnapshot> LoadSourceMaintenanceAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>单事务写入一批 manifest 行并删除给定路径（删除同时移除其消费者水位）。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="entries">要 upsert 的路径（含指纹与各消费者已应用版本）。</param>
    /// <param name="removedFilePaths">要删除的路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响的 manifest 行数（upsert + 删除）。</returns>
    Task<int> SaveSourceManifestAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSourceEntry> entries,
        IReadOnlyCollection<string> removedFilePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入账本（版本、扫描水位、待重试集合）：整体替换该 scope 的待重试行。
    /// </summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="state">账本快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <c>true</c> 表示写入被接受；<c>false</c> 表示这是一次**回退写入**（世代更旧，或同世代期望版本更旧）
    /// 已被拒绝，存储保持原值。
    /// </returns>
    Task<bool> SaveMaintenanceLedgerAsync(
        string workspaceId,
        string projectId,
        CodeSourceMaintenanceLedgerState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// **原子替换**一批文件的索引结果与源指纹（D4）：整个批次一个事务。
    /// <para>每个文件的语义（顺序即所有权规则）：</para>
    /// <list type="number">
    ///   <item><description>找出该文件**消失的符号**；先记录「其他文件指向这些消失符号的入边」的
    ///     来源文件（它们需要重新绑定），再删除这些入边 —— 指向**仍然存在**符号的入边必须保留。</description></item>
    ///   <item><description>删除该文件**拥有**的引用/关系行（所有权 = <c>SourceFilePath</c> 属于该文件，
    ///     或来源符号属于该文件的旧符号集），随后重建。</description></item>
    ///   <item><description>整体替换该文件的符号行，写入文件记录、符号、引用、关系，
    ///     并在**同一事务**里更新 manifest 行（指纹 + `Complete`）与该文件的各消费者已应用版本。</description></item>
    ///   <item><description>任何一步失败 ⇒ 整个批次回滚：旧索引结果与旧指纹保持完整，不得留下半成品。</description></item>
    /// </list>
    /// </summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="replacements">本批次要替换的文件（每个文件的路径与 scope 必须一致）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CodeSourceFileReplacementResult> ReplaceFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSourceFileReplacement> replacements,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 一个文件的**原子替换**（D4，2026-10-02）：索引结果（文件记录 + 符号 + 引用 + 关系）与
/// 该消费者已应用指纹/manifest 行必须在**同一个事务**里提交。
/// <para>
/// 调用方必须在内存里完成稳定读取与提取，再调用替换；<b>不得先 clear 再提取</b> ——
/// 那样提取失败就会留下「旧结果已删、新结果没有」的空洞。
/// </para>
/// </summary>
/// <param name="File">文件记录（<see cref="CodeFileRecord.FilePath"/> 必须与 <paramref name="Source"/> 的路径一致）。</param>
/// <param name="Symbols">该文件本次提取到的符号（空表示没有可索引符号，旧符号仍会被移除）。</param>
/// <param name="References">该文件拥有的引用行。</param>
/// <param name="Relations">该文件拥有的关系行。</param>
/// <param name="Source">同事务写入的源指纹与各消费者已应用版本。</param>
public sealed record CodeSourceFileReplacement(
    CodeFileRecord File,
    IReadOnlyList<CodeSymbolRecord> Symbols,
    IReadOnlyList<CodeReferenceRecord> References,
    IReadOnlyList<CodeRelationRecord> Relations,
    CodeSourceEntry Source);

/// <summary>原子替换的结果。</summary>
/// <param name="ReplacedFileCount">本次事务里被替换的文件数。</param>
/// <param name="InvalidatedDependentFilePaths">
/// 因「目标符号消失」而失去入边的**其他文件**（不在本批次内）：调用方必须为它们安排重新绑定，
/// 否则引用/关系图会静默残缺。保留在仍存在符号上的入边**不会**出现在这里。
/// </param>
/// <param name="RemovedSymbolIds">本次替换中消失的符号 id（诊断与验收计数）。</param>
public sealed record CodeSourceFileReplacementResult(
    int ReplacedFileCount,
    IReadOnlyList<string> InvalidatedDependentFilePaths,
    IReadOnlyList<string> RemovedSymbolIds);

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
