using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 一个文件本次提取出的**语义变化**（D3，2026-10-02）：哪些符号消失/改变了。
/// <para>
/// 只有真正提取过、能比较新旧符号集的文件才会出现在这里 —— 没有提取就没有依据，
/// 不允许凭「文件被改了」猜测依赖方（那会把影响面放大到整个 project）。
/// </para>
/// </summary>
/// <param name="FilePath">发生语义变化的文件。</param>
/// <param name="ChangedSymbolIds">
/// 消失或签名改变的符号 id。空列表表示该文件没有影响依赖方的语义变化（正文变化仍会重新索引它自己）。
/// </param>
public sealed record CodeFileSemanticChange(
    string FilePath,
    IReadOnlyList<string> ChangedSymbolIds);

/// <summary>
/// 反向依赖查询端口（D3）：给定一批符号 id，找出**其他文件**中依赖它们的位置。
/// <para>
/// 由存储实现（关系/引用表是唯一图真源）；组件不假设调用方持有整张图。
/// 返回的是「需要重新绑定的文件路径」，不含符号归属方自己（调用方按需过滤）。
/// </para>
/// </summary>
public interface ICodeGraphDependencyQuery
{
    /// <summary>批量查询依赖给定符号的文件路径（去重、稳定排序）。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="symbolIds">目标符号 id（调用方按需分块）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<string>> ListDependentFilePathsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<string> symbolIds,
        CancellationToken cancellationToken = default);
}

/// <summary>一个路径在本次维护中的**执行动作**（D3 的更新计划）。</summary>
public enum CodeSourceUpdateAction
{
    /// <summary>提取正文并原子提交（内容变化 / 新文件 / 需要核验）。</summary>
    Extract = 0,

    /// <summary>内容未变，只重新绑定（消费者输入变化，或它是受影响依赖方）。</summary>
    RebindOnly = 1,

    /// <summary>确认删除（完整扫描 + 未在扫描期间变化）。</summary>
    Delete = 2,

    /// <summary>本轮不动它：未定论（Deferred）或正在退避重试。</summary>
    RetryLater = 3,
}

/// <summary>更新计划原因串（稳定字符串）。</summary>
public static class CodeSourceUpdateReasons
{
    /// <summary>它依赖的符号发生了变化（反向依赖扩展得到，本身内容没变）。</summary>
    public const string DependentOfChangedSymbols = "dependent_of_changed_symbols";

    /// <summary>该路径正在退避重试窗口内：本轮不重复尝试。</summary>
    public const string RetryBackoffActive = "retry_backoff_active";

    /// <summary>该路径本轮没有定论（扫描期间又变化 / 扫描不完整 / 根不可用），留待下一轮核对。</summary>
    public const string DeferredPendingRecheck = "deferred_pending_recheck";

    /// <summary>本轮扩展依赖方时触到上限：还有未纳入的依赖方，必须下一轮继续。</summary>
    public const string DependencyExpansionTruncated = "dependency_expansion_truncated";
}

/// <summary>更新计划里的一条：路径 + 动作 + 原因 + 需要推进的消费者 + 依赖深度。</summary>
/// <param name="FilePath">绝对路径。</param>
/// <param name="Action">执行动作。</param>
/// <param name="Reasons">原因（变更判定原因串 + <see cref="CodeSourceUpdateReasons"/>）。</param>
/// <param name="Providers">需要推进的消费者。</param>
/// <param name="DependencyDepth">0 = 直接变化；1 = 由反向依赖扩展得到。</param>
public sealed record CodeSourceUpdateItem(
    string FilePath,
    CodeSourceUpdateAction Action,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Providers,
    int DependencyDepth);

/// <summary>
/// 一次维护的**执行计划**：按动作分组、组内按路径稳定排序，供执行层逐项执行。
/// <para>
/// 顺序即依赖：先 <see cref="CodeSourceUpdateAction.Extract"/>（把新符号写进去），
/// 再 <see cref="CodeSourceUpdateAction.RebindOnly"/>（依赖方据此重新绑定），
/// 然后 <see cref="CodeSourceUpdateAction.Delete"/>，最后是 <see cref="CodeSourceUpdateAction.RetryLater"/>。
/// </para>
/// </summary>
/// <param name="Items">计划条目（Extract → RebindOnly → Delete → RetryLater，组内按路径排序）。</param>
/// <param name="ExtractCount">需要提取并提交的文件数。</param>
/// <param name="RebindCount">只需重新绑定的文件数。</param>
/// <param name="DeleteCount">确认删除的文件数。</param>
/// <param name="RetryCount">本轮不动的文件数。</param>
/// <param name="DependencyExpansionCount">由反向依赖扩展得到的条目数。</param>
/// <param name="Truncated">
/// 依赖扩展触到上限：还有未纳入的依赖方，调用方必须在下一次维护继续（不得当作已完成）。
/// </param>
public sealed record CodeSourceUpdatePlan(
    IReadOnlyList<CodeSourceUpdateItem> Items,
    int ExtractCount,
    int RebindCount,
    int DeleteCount,
    int RetryCount,
    int DependencyExpansionCount,
    bool Truncated);
