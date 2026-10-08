using System.Security.Cryptography;
using System.Text;

namespace PuddingContextPolicy;

/// <summary>候选选择的策略选项（默认值来自既有配置口径，组件不另设常量）。</summary>
/// <param name="ProtectedTailMessages">受保护尾部条数（最后 N 条永不被裁剪）。</param>
/// <param name="TriggerRatio">软触发比例。</param>
/// <param name="TargetRatio">压实目标比例。</param>
public sealed record CandidateSelectionOptions(
    int ProtectedTailMessages = 8,
    double TriggerRatio = 0.80,
    double TargetRatio = 0.50);

/// <summary>
/// 冻结候选的选择结果。候选一旦产出即视为**不可变**：任何相关变化都走
/// <see cref="ContextCandidateApplicability"/> 的严格校验，不做尾部拼接或自动 rebase。
/// </summary>
/// <param name="ProjectedUsedTokensExcludingSummary">
/// 移除跨度后、**尚未加入摘要**的整请求估算。它**不是**提交后的真实值 ——
/// 提交前必须用真实摘要长度重算（见 <see cref="ContextNetGainAdmission.Evaluate"/>）。
/// </param>
public sealed record CandidateSelectionResult(
    bool Created,
    string Reason,
    int RemovedFromIndex,
    int RemovedCount,
    int RemovedTokens,
    int InitialUsedTokens,
    int ProjectedUsedTokensExcludingSummary,
    IReadOnlyList<ContextMessageShape> RemovedMessages,
    IReadOnlyList<ContextMessageShape> RetainedMessages,
    string SourceGeneration,
    string SourceRevision,
    string? Fingerprint)
{
    internal static CandidateSelectionResult NotCreated(string reason, int initialUsedTokens) =>
        new(false, reason, -1, 0, 0, initialUsedTokens, initialUsedTokens,
            [], [], string.Empty, string.Empty, null);
}

/// <summary>
/// 候选边界与指纹（纯函数）。
/// <para>
/// 硬不变量：
/// ① 前导 System 消息不可移除；
/// ② 最后 <see cref="CandidateSelectionOptions.ProtectedTailMessages"/> 条不可移除；
/// ③ 当前用户轮（最后一条 User 及其之后）不可移除；
/// ④ 只按**完整会话单元**移除（不会把 tool call 与它的 tool result 拆开，也不会跨 User 边界拼接）。
/// </para>
/// </summary>
public static class ContextCandidateSelector
{
    /// <summary>选择可压缩的旧跨度并产出稳定指纹；不满足条件时返回未创建并给出原因码。</summary>
    /// <param name="messages">按发送顺序排列的消息策略视图。</param>
    /// <param name="nonMessageTokens">不在这份列表里的开销（system 信封 + 工具 schema 等）。</param>
    /// <param name="capacity">容量结果（提供有效输入上限）。</param>
    /// <param name="options">策略选项。</param>
    /// <param name="routeVersion">路由/模型版本（参与指纹与后续校验）。</param>
    /// <param name="toolSpecVersion">工具定义版本（参与指纹与后续校验）。</param>
    /// <param name="sourceGeneration">源上下文代次。</param>
    /// <param name="sourceRevision">源历史 revision。</param>
    public static CandidateSelectionResult Select(
        IReadOnlyList<ContextMessageShape> messages,
        int nonMessageTokens,
        ContextCapacityResult capacity,
        CandidateSelectionOptions options,
        string routeVersion,
        string toolSpecVersion,
        long sourceGeneration,
        string sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(options);

        var working = messages.ToList();
        var initialUsed = SumUsed(working, nonMessageTokens);
        var originalLeadingSystemCount = working.TakeWhile(m => m.Role == ContextMessageRole.System).Count();
        var limit = capacity.EffectiveInputTokens;
        var ratio = options.TriggerRatio is > 0 and <= 1 ? options.TriggerRatio : 1.0;
        var softTrigger = (int)Math.Ceiling(limit * ratio);
        if (initialUsed < softTrigger)
            return CandidateSelectionResult.NotCreated("below_soft_threshold", initialUsed);

        var targetRatio = Math.Clamp(
            options.TargetRatio,
            0.05,
            Math.Clamp(options.TriggerRatio, 0.05, 1.0));
        var target = (int)Math.Floor(limit * targetRatio);
        var protectedTailCount = Math.Max(0, options.ProtectedTailMessages);

        var removed = new List<ContextMessageShape>();
        while (SumUsed(working, nonMessageTokens) > target && TryRemoveOldestUnit(working, protectedTailCount, out var unit))
        {
            removed.AddRange(unit);
        }

        if (removed.Count == 0)
            return CandidateSelectionResult.NotCreated("nothing_removable", initialUsed);

        var leadingSystemCount = working.TakeWhile(m => m.Role == ContextMessageRole.System).Count();
        if (leadingSystemCount == 0 || working.Count <= leadingSystemCount)
            return CandidateSelectionResult.NotCreated("protected_only", initialUsed);

        var retained = working.ToArray();
        var protectedTail = ComputeProtectedTail(working, leadingSystemCount, protectedTailCount);
        var projected = SumUsed(working, nonMessageTokens);
        var fingerprint = ComputeFingerprint(
            sourceGeneration,
            sourceRevision,
            routeVersion,
            toolSpecVersion,
            removed,
            retained);

        return new CandidateSelectionResult(
            true,
            "candidate_created",
            // 移除总是发生在 system 前缀之后，因此被替换跨度在原列表里的起点就是前导 system 条数。
            originalLeadingSystemCount,
            removed.Count,
            removed.Sum(m => Math.Max(0, m.TokenEstimate)),
            initialUsed,
            projected,
            removed,
            // 保留侧记录**完整后缀**（中间保留段 + 受保护尾部）：提交前要求它逐位不变，
            // 这样「尾部之前插入/删除消息」也会让旧候选失效，而不只是「尾部被追加」。
            retained,
            sourceGeneration.ToString(),
            sourceRevision,
            fingerprint);
    }

    /// <summary>
    /// 稳定指纹：对（策略版本、源代次、源 revision、路由版本、工具定义版本、被替换跨度身份、
    /// 保留后缀身份）做 SHA-256。只含身份与版本，**不含正文**；<c>protectedTail</c> 供调用方
    /// 需要单独定位受保护尾部时使用（它是保留后缀的一个子后缀）。
    /// </summary>
    public static string ComputeFingerprint(
        long sourceGeneration,
        string sourceRevision,
        string routeVersion,
        string toolSpecVersion,
        IReadOnlyList<ContextMessageShape> removed,
        IReadOnlyList<ContextMessageShape> retained)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(retained);

        var builder = new StringBuilder();
        builder.Append("v=").Append(ContextPolicyVersion.Current).Append('\n');
        builder.Append("gen=").Append(sourceGeneration).Append('\n');
        builder.Append("rev=").Append(sourceRevision ?? string.Empty).Append('\n');
        builder.Append("route=").Append(routeVersion ?? string.Empty).Append('\n');
        builder.Append("tools=").Append(toolSpecVersion ?? string.Empty).Append('\n');
        builder.Append("removed:\n");
        foreach (var message in removed)
        {
            builder.Append(message.Role).Append('\t').Append(message.Identity).Append('\n');
        }

        builder.Append("retained:\n");
        foreach (var message in retained)
        {
            builder.Append(message.Role).Append('\t').Append(message.Identity).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static int SumUsed(IReadOnlyList<ContextMessageShape> messages, int nonMessageTokens)
    {
        var total = Math.Max(0, nonMessageTokens);
        foreach (var message in messages)
            total += Math.Max(0, message.TokenEstimate);
        return total;
    }

    /// <summary>
    /// 移除最旧的一个完整会话单元。保护规则与硬预算守卫同源：
    /// 不跨 User/System 边界拼接，不越过受保护尾部，不把 tool call 与其 result 拆开。
    /// </summary>
    private static bool TryRemoveOldestUnit(
        List<ContextMessageShape> messages,
        int protectedTailCount,
        out List<ContextMessageShape> removedUnit)
    {
        removedUnit = [];
        var firstRemovable = messages.FindIndex(m => m.Role != ContextMessageRole.System);
        if (firstRemovable < 0)
            return false;

        var protectedTailStart = ComputeProtectedTailStart(messages, firstRemovable, protectedTailCount);
        if (firstRemovable >= protectedTailStart)
            return false;

        var removeEnd = firstRemovable + 1;
        while (removeEnd < messages.Count
            && messages[removeEnd].Role is not (ContextMessageRole.User or ContextMessageRole.System))
        {
            removeEnd++;
        }

        if (removeEnd > protectedTailStart)
            return false;

        removedUnit = messages.GetRange(firstRemovable, Math.Max(1, removeEnd - firstRemovable));
        messages.RemoveRange(firstRemovable, Math.Max(1, removeEnd - firstRemovable));
        return true;
    }

    private static int ComputeProtectedTailStart(
        IReadOnlyList<ContextMessageShape> messages,
        int firstRemovable,
        int protectedTailCount)
    {
        var protectedTailStart = Math.Max(firstRemovable, messages.Count - protectedTailCount);
        var currentUser = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ContextMessageRole.User)
                continue;
            currentUser = i;
            break;
        }

        if (currentUser >= 0)
            protectedTailStart = Math.Min(protectedTailStart, currentUser);
        return protectedTailStart;
    }

    private static IReadOnlyList<ContextMessageShape> ComputeProtectedTail(
        IReadOnlyList<ContextMessageShape> messages,
        int leadingSystemCount,
        int protectedTailCount)
    {
        var start = ComputeProtectedTailStart(messages, leadingSystemCount, protectedTailCount);
        if (start >= messages.Count)
            return [];
        return messages.Skip(start).ToArray();
    }
}
