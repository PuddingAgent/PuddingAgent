using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// Session Composition 不可变快照持久化实体（P0-5 步骤 1）。
/// 对应 <c>PuddingCode.Runtime.SessionCompositionRecord</c> 的落库形态，
/// 表名 <c>CompositionSnapshots</c>，复合主键 (SessionId, CompositionVersion)。
/// 纯 append-only：只插入、不更新、不删除；版本严格单调递增。
/// 只保存指纹与元数据，不保存 prompt 正文 / 工具 schema 全文。
/// </summary>
public class CompositionSnapshotEntity
{
    [MaxLength(32)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>该 session 的 composition 版本号，单调递增，从 1 开始。</summary>
    public long CompositionVersion { get; set; }

    /// <summary>system prompt 的 SHA-256 指纹（小写 hex）。</summary>
    [MaxLength(64)]
    public string SystemPromptHash { get; set; } = string.Empty;

    /// <summary>工具 schema 全量的 SHA-256 指纹（小写 hex）。</summary>
    [MaxLength(64)]
    public string ToolSpecHash { get; set; } = string.Empty;

    /// <summary>组合前缀 SHA-256 指纹（小写 hex）。</summary>
    [MaxLength(64)]
    public string PrefixHash { get; set; } = string.Empty;

    /// <summary>Runtime Skill Index 的稳定 SHA-256 指纹（小写 hex）；未启用时可为空。</summary>
    [MaxLength(64)]
    public string? SkillManifestHash { get; set; }

    /// <summary>序列化/规范化算法版本（复用 PrefixCacheSnapshotBuilder.Version = "prefix-v1"）。</summary>
    [MaxLength(32)]
    public string SerializationVersion { get; set; } = "prefix-v1";

    /// <summary>有序 append-only 全量工具 ID 列表（JSON 数组字符串，如 ["search_tools","file_read"]）。</summary>
    public string? ToolIds { get; set; }

    /// <summary>本次版本相对上一版本的变化原因。</summary>
    [MaxLength(64)]
    public string? ChangeReason { get; set; }

    /// <summary>权限/能力集纪元；权限变化显式 +1，触发开新版本。</summary>
    public int PermissionEpoch { get; set; }

    /// <summary>Unix 毫秒时间戳。</summary>
    public long CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Canonical system prefix 的 SHA-256 指纹（小写 hex）；尚无 canonical 前缀时可为空。</summary>
    [MaxLength(64)]
    public string? CanonicalSystemPrefixHash { get; set; }
}
