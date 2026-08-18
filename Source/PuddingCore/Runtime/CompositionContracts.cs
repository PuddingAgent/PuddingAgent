namespace PuddingCode.Runtime;

/// <summary>
/// Session Composition 不可变快照记录（P0-5 步骤 1 契约）。
///
/// 语义：一个 session 的「稳定前缀 + 工具集合 + Skill Manifest 版本」指纹的不可变、append-only 账本。
/// 只保存 SHA-256 指纹与元数据，绝不保存/上报 prompt 正文、工具 schema 全文（对齐原文不脱敏原则）。
/// 普通请求只允许追加（CompositionVersion 单调递增、ToolIds 只增不收缩）；
/// 权限/能力集变更通过 <see cref="PermissionEpoch"/> 显式 +1 触发开新版本，而非 silent 收缩。
/// </summary>
public sealed record SessionCompositionRecord
{
    /// <summary>会话 ID（与 <c>Sessions.SessionId</c> 对齐，32 位 hex）。</summary>
    public required string SessionId { get; init; }

    /// <summary>该 session 的 composition 版本号，单调递增，从 1 开始（long，防溢出）。</summary>
    public required long CompositionVersion { get; init; }

    /// <summary>system prompt 的 SHA-256 指纹（小写 hex）。</summary>
    public required string SystemPromptHash { get; init; }

    /// <summary>工具 schema 全量的 SHA-256 指纹（小写 hex）。</summary>
    public required string ToolSpecHash { get; init; }

    /// <summary>组合前缀 SHA-256 指纹（小写 hex），= hash(SystemPromptHash + ToolSpecHash)。</summary>
    public required string PrefixHash { get; init; }

    /// <summary>Runtime Skill Index 的稳定 SHA-256 指纹（小写 hex）；未启用时可为空。</summary>
    public string? SkillManifestHash { get; init; }

    /// <summary>序列化/规范化算法版本，复用 <see cref="PrefixCacheSnapshotBuilder.Version"/>（"prefix-v1"）。</summary>
    public string SerializationVersion { get; init; } = PrefixCacheSnapshotBuilder.Version;

    /// <summary>有序 append-only 全量工具 ID 列表（只增不收缩）。</summary>
    public required IReadOnlyList<string> ToolIds { get; init; }

    /// <summary>本次版本相对上一版本的变化原因（initial / system_prompt_changed / tool_spec_changed / skill_manifest_changed / permission_changed / none）。</summary>
    public string? ChangeReason { get; init; }

    /// <summary>权限/能力集纪元。权限变化显式 +1，触发开新版本。</summary>
    public int PermissionEpoch { get; init; }

    /// <summary>快照创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Canonical system prefix 的 SHA-256 指纹（小写 hex）；尚无 canonical 前缀时可为空。</summary>
    public string? CanonicalSystemPrefixHash { get; init; }
}

/// <summary>
/// Composition 不可变持久化存储接口（P0-5 步骤 1）。
/// 契约放 Core 层，实现（SQLite / 文件）放 Runtime 层。
/// 语义：
/// - <see cref="AppendAsync"/> 只允许追加，版本必须严格单调递增（append-only，不重写、不收缩）；
/// - <see cref="GetLatestAsync"/> 返回该 session 最大 CompositionVersion 的记录；
/// - <see cref="LoadAsync"/> 返回该 session 全部记录（版本升序）。
/// </summary>
public interface ICompositionStore
{
    /// <summary>读取 session 最新 composition 记录；无任何记录时返回 null。</summary>
    Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 追加一条 composition 记录（append-only）。
    /// 返回是否成功：版本小于等于当前最大版本、或并发下撞上 (SessionId, CompositionVersion) 唯一约束时返回 false，
    /// 不抛异常；参数非法（null/空白 sessionId、版本 &lt; 1）仍抛 <see cref="ArgumentException"/>。
    /// </summary>
    Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default);

    /// <summary>读取 session 全部 composition 记录（CompositionVersion 升序）；无记录时返回空列表。</summary>
    Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Composition 版本观测结果（P0-5 步骤 2）。
/// <see cref="Version"/> 为该 session 内单调递增的 composition 版本号（相同 hash 组合复用）；
/// <see cref="ChangeReason"/> 为本次相对上次的变化原因（initial / *_changed / none）。
/// </summary>
public readonly record struct CompositionObservation(int Version, string ChangeReason);

/// <summary>
/// 进程内 composition 版本登记表接口（P0-5 步骤 2）。
/// 契约放 Core 层，实现（纯内存 / 持久化写穿）放 Runtime 层。
/// 语义：
/// - <see cref="Observe"/> 按 (sessionId, systemPromptHash, toolSpecHash) 递增版本，相同组合复用同一版本；
/// - 只处理 hash 指纹，绝不接收/保存 prompt 或工具 schema 正文；
/// - 实现可自行决定是否写穿 <see cref="ICompositionStore"/>（写穿失败必须降级为纯内存，不抛给调用方）。
/// </summary>
public interface ICompositionVersionRegistry
{
    /// <summary>
    /// 观测一次 composition，返回版本号与变化原因（原子）。
    /// <paramref name="toolIds"/>、<paramref name="permissionEpoch"/> 与 <paramref name="skillManifestHash"/>
    /// 仅供写穿持久化使用，纯内存实现可忽略；<paramref name="permissionEpoch"/> 变化由调用方显式 +1 触发开新版本。
    /// </summary>
    CompositionObservation Observe(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        int permissionEpoch = 0,
        string? skillManifestHash = null);
}
