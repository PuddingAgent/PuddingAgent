namespace PuddingCode.Abstractions;

/// <summary>
/// 记忆图书馆管理员——承担智能整理职责。
/// 位于潜意识 LLM 与图书馆 Core 之间的整理层。
/// 可以调用 LLM，可以做策略判断，输出必须是结构化操作。
/// ADR-028 Phase 4。
/// </summary>
public interface IMemoryLibrarian
{
    /// <summary>
    /// 摄入一段经验——自动选择或创建 Book，追加 Chapter，
    /// 创建 SourceReference 和 Pointer，挂载 TreeNode。
    /// </summary>
    Task<ExperienceWriteResult> IngestExperienceAsync(
        MemoryIngestionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 规划树维护操作——LLM 检查重复、合并候选、冲突候选。
    /// </summary>
    Task<IReadOnlyList<MemoryTreeOperation>> PlanTreeMaintenanceAsync(
        string workspaceId,
        string libraryId,
        CancellationToken ct = default);

    /// <summary>
    /// 应用已验证的树维护操作。
    /// </summary>
    Task ApplyTreeOperationAsync(
        MemoryTreeOperation operation,
        CancellationToken ct = default);
}
