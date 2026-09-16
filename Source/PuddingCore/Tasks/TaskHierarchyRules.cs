namespace PuddingCode.Tasks;

/// <summary>
/// 任务看板母/子层级规则（Stage 1，D1–D5）：纯函数、无 IO、不读写存储、不修改入参。
/// <para>
/// 层级为<b>单层</b>（母 → 子，D1）：母卡自身不得再有父；本类型不做多级遍历，也不解析成环路径，
/// 只按「父自身不得已有父」这一条拒绝多级/成环。
/// </para>
/// <para>
/// 本类型只做<b>判定</b>，不做拦截与写入：调用方（Stage 2 的 manage_tasks / 派发器 / 归档命令）
/// 必须先调用本类型，再决定拒绝还是落库。约定：
/// <list type="bullet">
/// <item>D2 有子卡的母卡即「容器」：不可 claim / 不可自动派发，<see cref="WorkspaceTask.AutoDispatchEnabled"/>
/// 对容器一律视为 false（见 <see cref="CanBeDispatched"/> / <see cref="IsAutoDispatchEffective"/>）。</item>
/// <item>D3 母卡 <see cref="WorkspaceTask.Status"/> 不因子卡派生：本类型只提供只读聚合投影
/// （见 <see cref="CountChildren"/>），<b>不</b>返回也不写回任何状态。</item>
/// <item>D4 归档/取消母卡时若存在未终态子卡，默认 fail-closed 拒绝，须显式 force 才级联
/// （见 <see cref="ValidateArchiveOrCancel"/>）。</item>
/// <item>D5 父子关系仅管理者（manage_tasks）可写，执行者侧只读：本类型不区分调用方身份，
/// 由上层门禁负责。</item>
/// </list>
/// </para>
/// </summary>
public static class TaskHierarchyRules
{
    /// <summary>空子卡集合（只读单例，避免重复分配）。</summary>
    private static readonly IReadOnlyList<WorkspaceTask> NoChildren = [];

    /// <summary>
    /// 返回直接子卡（<see cref="WorkspaceTask.ParentTaskId"/> 等于 <paramref name="taskId"/> 的任务）。
    /// <paramref name="taskId"/> 为空或 <paramref name="candidates"/> 为 null 时返回空集合；不去重、不排序。
    /// </summary>
    public static IReadOnlyList<WorkspaceTask> GetChildren(
        string? taskId,
        IEnumerable<WorkspaceTask>? candidates)
    {
        if (string.IsNullOrEmpty(taskId) || candidates is null)
        {
            return NoChildren;
        }

        var children = new List<WorkspaceTask>();
        foreach (var candidate in candidates)
        {
            if (candidate is not null
                && string.Equals(candidate.ParentTaskId, taskId, StringComparison.Ordinal))
            {
                children.Add(candidate);
            }
        }

        return children.Count == 0 ? NoChildren : children.AsReadOnly();
    }

    /// <summary>是否存在子卡（容器判定的原始谓词）。</summary>
    public static bool HasChildren(string? taskId, IEnumerable<WorkspaceTask>? candidates)
        => GetChildren(taskId, candidates).Count > 0;

    /// <summary>
    /// 是否为「容器」（D2）：只要存在直接子卡即为容器，与自身 <see cref="WorkspaceTask.Status"/> 无关。
    /// </summary>
    public static bool IsContainer(string? taskId, IEnumerable<WorkspaceTask>? candidates)
        => HasChildren(taskId, candidates);

    /// <summary>
    /// 是否存在未终态子卡（D4 判定的输入）。终态口径复用 <see cref="TaskStateMachine.IsTerminal"/>
    /// （Completed / Failed / Cancelled / Archived）。
    /// </summary>
    public static bool HasNonTerminalChildren(string? taskId, IEnumerable<WorkspaceTask>? candidates)
    {
        var children = GetChildren(taskId, candidates);
        for (var i = 0; i < children.Count; i++)
        {
            if (!TaskStateMachine.IsTerminal(children[i].Status))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否允许被 claim / 派发（D2）：容器一律不可，叶子（无子卡）可。
    /// 本 Stage 只给判定，实际拦截留 Stage 2；<b>不</b>读取也不改写 <see cref="WorkspaceTask.Status"/>。
    /// </summary>
    public static bool CanBeDispatched(string? taskId, IEnumerable<WorkspaceTask>? candidates)
        => !IsContainer(taskId, candidates);

    /// <summary>
    /// 自动派发的有效开关（D2）：容器一律返回 false——即便母卡行上
    /// <see cref="WorkspaceTask.AutoDispatchEnabled"/> 为 true，也不得自动派发；叶子任务原样返回。
    /// </summary>
    public static bool IsAutoDispatchEffective(
        string? taskId,
        bool autoDispatchEnabled,
        IEnumerable<WorkspaceTask>? candidates)
        => autoDispatchEnabled && CanBeDispatched(taskId, candidates);

    /// <summary>
    /// 校验「挂父」合法性（单层约束，D1）。返回 null = 合法；否则返回契约错误码（不抛异常）。
    /// <para>
    /// 分支：
    /// <paramref name="taskId"/> 为空 → <see cref="TaskErrorCode.TaskHierarchyInvalid"/>（无法标识子卡）；
    /// <paramref name="parentTaskId"/> 为空 → 合法（脱挂为顶层，既有数据的 ParentTaskId 即 null）；
    /// 父 == 自身 → 自引用，<see cref="TaskErrorCode.TaskHierarchyInvalid"/>；
    /// 父不在 <paramref name="existingTasks"/> 中 → <see cref="TaskErrorCode.TaskParentNotFound"/>；
    /// 父自身已有父（多级/成环）→ <see cref="TaskErrorCode.TaskHierarchyInvalid"/>。
    /// </para>
    /// 工作区一致性由调用方保证：<paramref name="existingTasks"/> 应只包含同一工作区的任务。
    /// </summary>
    public static TaskErrorCode? ValidateParentAssignment(
        string? taskId,
        string? parentTaskId,
        IEnumerable<WorkspaceTask>? existingTasks)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return TaskErrorCode.TaskHierarchyInvalid;
        }

        if (string.IsNullOrEmpty(parentTaskId))
        {
            return null;
        }

        if (string.Equals(taskId, parentTaskId, StringComparison.Ordinal))
        {
            return TaskErrorCode.TaskHierarchyInvalid;
        }

        var parent = FindTask(parentTaskId, existingTasks);
        if (parent is null)
        {
            return TaskErrorCode.TaskParentNotFound;
        }

        // 单层约束：父自身已有父 → 多级挂载（或成环），拒绝。
        return string.IsNullOrEmpty(parent.ParentTaskId)
            ? null
            : TaskErrorCode.TaskHierarchyInvalid;
    }

    /// <summary>
    /// 归档/取消母卡的 fail-closed 判定（D4）。返回 null = 允许；否则返回拒绝原因错误码。
    /// <para>
    /// <paramref name="force"/> = true 时跳过子卡检查（显式级联，由调用方负责级联顺序）。
    /// <paramref name="taskId"/> 为空时返回 null：任务不存在的判定由调用方的 not_found 门禁负责，
    /// 本类型不做「任务是否存在」的越权判断。
    /// </para>
    /// </summary>
    public static TaskErrorCode? ValidateArchiveOrCancel(
        string? taskId,
        IEnumerable<WorkspaceTask>? candidates,
        bool force = false)
    {
        if (string.IsNullOrEmpty(taskId) || force)
        {
            return null;
        }

        return HasNonTerminalChildren(taskId, candidates)
            ? TaskErrorCode.TaskHasNonTerminalChildren
            : null;
    }

    /// <summary>
    /// 只读聚合投影（D3）：统计子卡总数 / 终态数 / 未终态数。
    /// <b>仅供展示与门禁判定</b>，禁止据此写回或派生母卡 <see cref="WorkspaceTask.Status"/>。
    /// </summary>
    public static TaskHierarchyCounts CountChildren(
        string? taskId,
        IEnumerable<WorkspaceTask>? candidates)
    {
        var children = GetChildren(taskId, candidates);
        var terminal = 0;
        for (var i = 0; i < children.Count; i++)
        {
            if (TaskStateMachine.IsTerminal(children[i].Status))
            {
                terminal++;
            }
        }

        return new TaskHierarchyCounts(children.Count, terminal, children.Count - terminal);
    }

    /// <summary>按 <see cref="WorkspaceTask.TaskId"/> 在内存集合中查找任务；找不到或入参为空返回 null。</summary>
    public static WorkspaceTask? FindTask(string? taskId, IEnumerable<WorkspaceTask>? candidates)
    {
        if (string.IsNullOrEmpty(taskId) || candidates is null)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (candidate is not null
                && string.Equals(candidate.TaskId, taskId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>子卡只读聚合计数（D3 只读投影：<b>不</b>参与母卡 Status 派生）。</summary>
/// <param name="Total">直接子卡总数。</param>
/// <param name="Terminal">终态子卡数（Completed / Failed / Cancelled / Archived）。</param>
/// <param name="NonTerminal">未终态子卡数。</param>
public readonly record struct TaskHierarchyCounts(int Total, int Terminal, int NonTerminal)
{
    /// <summary>全部子卡均为终态（D4 归档允许条件）；无子卡时亦为 true。</summary>
    public bool AllTerminal => NonTerminal == 0;
}
