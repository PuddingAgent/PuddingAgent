namespace PuddingCode.Runtime;

/// <summary>
/// Session Composition 不可变快照记录（P0-5 步骤 1 契约）。
///
/// 语义：一个 session 的「稳定前缀 + 工具集合 + Skill Manifest 版本」指纹的不可变、append-only 账本。
/// 只保存 SHA-256 指纹与元数据，绝不保存/上报 prompt 正文、工具 schema 全文（对齐原文不脱敏原则）。
/// 普通请求只允许追加（<see cref="CompositionVersion"/> 严格单调递增、ToolIds 只增不收缩）；
/// 权限/能力集变更通过 <see cref="PermissionEpoch"/> 显式 +1 触发开新版本，而非 silent 收缩。
///
/// C01-B：把「先后顺序」与「内容身份」拆成两个独立维度——
/// <see cref="CompositionVersion"/>（= revision，严格单调，A→B→A 得 1/2/3）
/// 与 <see cref="ContentId"/>（= 内容哈希，可复用，A→B→A 得 A/B/A）。
/// </summary>
public sealed record SessionCompositionRecord
{
    /// <summary>会话 ID（与 <c>Sessions.SessionId</c> 对齐，32 位 hex）。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Composition revision：该 session 内严格单调递增的位置序号，从 1 开始（long，防溢出）。
    /// 不复用：即使内容回到旧值（A→B→A）也继续递增（1/2/3）。内容是否复用见 <see cref="ContentId"/>。
    /// </summary>
    public required long CompositionVersion { get; init; }

    /// <summary>
    /// 内容身份（小写 sha256 hex），复用既有 canonical 形状哈希规则
    /// （<see cref="CompositionSnapshot.ComputeContentId"/>，与 <see cref="PrefixHash"/> 同源）。
    /// 内容相同即相同 ContentId，可跨 revision 复用（A→B→A 得 A/B/A）。
    /// 历史行（C01-B 之前写入）为 null —— 读取方必须据此判定「无法证明精确内容」，不得谎称精确恢复。
    /// </summary>
    public string? ContentId { get; init; }

    /// <summary>system prompt 的 SHA-256 指纹（小写 hex）。</summary>
    public required string SystemPromptHash { get; init; }

    /// <summary>工具 schema 全量的 SHA-256 指纹（小写 hex）。</summary>
    public required string ToolSpecHash { get; init; }

    /// <summary>组合前缀 SHA-256 指纹（小写 hex），= hash(SystemPromptHash + ToolSpecHash)。</summary>
    public required string PrefixHash { get; init; }

    /// <summary>Runtime Skill Index 的稳定 SHA-256 指纹（小写 hex）；未启用时可为空。</summary>
    public string? SkillManifestHash { get; init; }

    /// <summary>序列化/规范化算法版本，复用 <see cref="PrefixCacheSnapshotBuilder.Version"/>。</summary>
    public string SerializationVersion { get; init; } = PrefixCacheSnapshotBuilder.Version;

    /// <summary>有序 append-only 全量工具 ID 列表（只增不收缩）。</summary>
    public required IReadOnlyList<string> ToolIds { get; init; }

    /// <summary>本次版本相对上一版本的变化原因（initial / system_prompt_changed / tool_spec_changed / skill_manifest_changed / permission_changed / none）。</summary>
    public string? ChangeReason { get; init; }

    /// <summary>权限/能力集纪元。权限变化显式 +1，触发开新版本。</summary>
    public int PermissionEpoch { get; init; }

    /// <summary>
    /// 工具曝光纪元（C01-B R2/AC4）：只在**曝光集合**（工具按需发现 / 显示集合）变化时 +1，
    /// 与 <see cref="PermissionEpoch"/>（授权配置维度）完全独立——工具发现不得被记为权限变化，
    /// 权限撤销也不得被记为工具发现。历史行（C01-B 之前写入）为 0。
    /// </summary>
    public long ExposureRevision { get; init; }

    /// <summary>快照创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Canonical system prefix 的 SHA-256 指纹（小写 hex）；尚无 canonical 前缀时可为空。</summary>
    public string? CanonicalSystemPrefixHash { get; init; }
}

/// <summary>
/// Composition 变更原因常量（C01-B R4/R9）：跨层单一事实来源，禁止各层各写一套字符串字面量。
/// </summary>
public static class CompositionChangeReasons
{
    /// <summary>system prompt 相对上次观测发生变化。</summary>
    public const string SystemPromptChanged = "system_prompt_changed";

    /// <summary>工具 schema 全量发生变化。</summary>
    public const string ToolSpecChanged = "tool_spec_changed";

    /// <summary>Skill manifest 发生变化。</summary>
    public const string SkillManifestChanged = "skill_manifest_changed";

    /// <summary>权限集合指纹变化 → 权限纪元 +1（<see cref="CompositionObservation.PermissionEpoch"/> 维度）。</summary>
    public const string PermissionChanged = "permission_changed";

    /// <summary>工具曝光集合变化 → 曝光纪元 +1（<see cref="CompositionObservation.ExposureRevision"/> 维度，工具按需发现）。</summary>
    public const string ExposureChanged = "tool_exposure_changed";

    /// <summary>曝光排序策略相对 legacy（全量字母序）发生偏差 → 一次性显式 epoch，禁止静默改序。</summary>
    public const string OrderingStrategyChanged = "ordering_strategy_changed";

    /// <summary>既有曝光引用的工具定义已不存在 → 不谎称精确恢复，但不阻塞执行。</summary>
    public const string ToolDefinitionMissing = "tool_definition_missing";

    /// <summary>无变化。</summary>
    public const string None = "none";
}

/// <summary>append 结果分类（C01-B CAS 合同）。</summary>
public enum CompositionAppendOutcome
{
    /// <summary>已提交（含幂等重放：该 revision 已在 store 中）。</summary>
    Committed = 1,

    /// <summary>预期 head 与 store 实际 head 不符 → 调用方必须重读 head 并重算 proposed composition。</summary>
    Conflict = 2,

    /// <summary>存储暂不可用（busy / IO 失败）→ 可重试；调用方不得静默发出未提交形状。</summary>
    Unavailable = 3,
}

/// <summary>
/// append 结构化结果（C01-B R4/R5）：替代原 <c>bool</c> 单值语义，
/// 区分「已提交 / CAS 冲突 / 存储不可用（可重试）」三种状态。
/// </summary>
public readonly record struct CompositionAppendResult(
    CompositionAppendOutcome Outcome,
    long Revision,
    long ExpectedRevision,
    long ActualRevision,
    string? FailureReason)
{
    /// <summary>可重试不可用错误码（对外语义，见设计文档 <c>01:258-262</c>）。</summary>
    public const string UnavailableErrorCode = "composition_commit_unavailable";

    /// <summary>是否已提交。</summary>
    public bool IsCommitted => Outcome == CompositionAppendOutcome.Committed;

    /// <summary>是否 CAS 冲突。</summary>
    public bool IsConflict => Outcome == CompositionAppendOutcome.Conflict;

    /// <summary>是否存储不可用（可重试）。</summary>
    public bool IsUnavailable => Outcome == CompositionAppendOutcome.Unavailable;

    /// <summary>提交成功。</summary>
    public static CompositionAppendResult Committed(long revision) =>
        new(CompositionAppendOutcome.Committed, revision, revision, revision, null);

    /// <summary>CAS 冲突：预期 head 与实际 head 明确回报，供调用方重读后重算。</summary>
    public static CompositionAppendResult Conflict(long expectedRevision, long actualRevision) =>
        new(CompositionAppendOutcome.Conflict, actualRevision, expectedRevision, actualRevision, null);

    /// <summary>存储不可用（可重试），失败原因带 <see cref="UnavailableErrorCode"/> 前缀。</summary>
    public static CompositionAppendResult Unavailable(string reason) =>
        new(CompositionAppendOutcome.Unavailable, 0, 0, 0, UnavailableErrorCode + ": " + reason);
}

/// <summary>
/// Composition 不可变持久化存储接口（P0-5 步骤 1）。
/// 契约放 Core 层，实现（SQLite / 文件）放 Runtime 层。
/// 语义：
/// - <see cref="AppendAsync"/> 只允许追加，且必须携带 <c>expectedRevision</c> 做 **CAS**：
///   期望 head 不符时返回 <see cref="CompositionAppendOutcome.Conflict"/>，
///   **严禁**用「先查 MAX 再 INSERT」代替完整预期版本检查；
/// - <see cref="GetLatestAsync"/> 返回该 session 最大 CompositionVersion 的记录；
/// - <see cref="LoadAsync"/> 返回该 session 全部记录（版本升序）。
/// </summary>
public interface ICompositionStore
{
    /// <summary>读取 session 最新 composition 记录；无任何记录时返回 null。</summary>
    Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 以 CAS 语义追加一条 composition 记录（append-only）。
    /// 单事务内比较 <paramref name="expectedRevision"/> 与该 session 当前 head，一致才插入；
    /// 不一致返回 <see cref="CompositionAppendOutcome.Conflict"/>（回报 expected/actual），
    /// 存储不可用返回 <see cref="CompositionAppendOutcome.Unavailable"/>（可重试），均不抛异常。
    /// 参数非法（null/空白 sessionId、版本 &lt; 1、expectedRevision &lt; 0）仍抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="record">待追加记录；<c>record.CompositionVersion</c> 为目标 revision（通常 = expectedRevision + 1）。</param>
    /// <param name="expectedRevision">调用方认为的当前 head revision；空 session 为 0。</param>
    Task<CompositionAppendResult> AppendAsync(
        SessionCompositionRecord record,
        long expectedRevision,
        CancellationToken ct = default);

    /// <summary>读取 session 全部 composition 记录（CompositionVersion 升序）；无记录时返回空列表。</summary>
    Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Composition 版本观测结果（P0-5 步骤 2 / C01-B）。
/// <see cref="Revision"/> 为该 session 内**严格单调递增**的 composition revision（不复用，A→B→A = 1/2/3）；
/// <see cref="ContentId"/> 为**内容身份**（相同内容复用同一值，A→B→A = A/B/A）；
/// <see cref="ChangeReason"/> 为本次相对上次的变化原因（initial / *_changed / none）；
/// <see cref="PermissionEpoch"/> 为本次观测生效的权限纪元（注册表内部基于权限指纹检测自增，
/// 或显式传入的基准值；供写穿持久化记录）。
/// <see cref="ExposureRevision"/> 为本次观测生效的工具曝光纪元（与权限纪元分离，
/// 只在曝光集合变化时推进，见 <see cref="CompositionChangeReasons.ExposureChanged"/>）。
/// </summary>
public readonly record struct CompositionObservation(
    long Revision,
    string ContentId,
    string ChangeReason,
    int PermissionEpoch = 0,
    long ExposureRevision = 0);

/// <summary>
/// 进程内 composition 版本登记表接口（P0-5 步骤 2）。
/// 契约放 Core 层，实现（纯内存 / 持久化写穿）放 Runtime 层。
/// 语义：
/// - <see cref="Observe"/> 每次观测分配一个**新的**严格单调 revision，并回报可复用的 <c>ContentId</c>；
/// - 只处理 hash 指纹，绝不接收/保存 prompt 或工具 schema 正文；
/// - 实现可自行决定是否写穿 <see cref="ICompositionStore"/>。
///   写穿失败**不得静默降级为纯内存继续**：实现必须把失败变为可观察状态
///   （计数 / 结构化结果），"必须执行" 路径应使用显式 CAS 提交并把
///   <see cref="CompositionAppendResult.Unavailable"/>（可重试 <c>composition_commit_unavailable</c>）回报调用方。
/// </summary>
public interface ICompositionVersionRegistry
{
    /// <summary>
    /// 观测一次 composition，返回新 revision / 内容身份 / 变化原因（原子）。
    /// <paramref name="toolIds"/>、<paramref name="permissionEpoch"/> 与 <paramref name="skillManifestHash"/>
    /// 仅供写穿持久化使用，纯内存实现可忽略；<paramref name="permissionEpoch"/> 变化由调用方显式 +1 触发开新版本。
    /// <paramref name="permissionFingerprint"/> 为当前权限集合的稳定 SHA-256 指纹（可为 null）；
    /// 实现应检测指纹变化并在变化时自增内部权限纪元（P0-5 step 4c：permissionEpoch 检测 +1）。
    /// </summary>
    CompositionObservation Observe(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        int permissionEpoch = 0,
        string? skillManifestHash = null,
        string? permissionFingerprint = null,
        string? canonicalSystemPrefixHash = null);
}
