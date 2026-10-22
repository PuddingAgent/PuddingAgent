using System.Security.Cryptography;
using System.Text;

namespace PuddingContextPolicy;

/// <summary>候选选择的策略选项（默认值来自既有配置口径，组件不另设常量）。</summary>
/// <param name="ProtectedTailMessages">近期保留条数偏好；硬压力下受预算约束，始终保留最新完整单元。</param>
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
    /// <summary>
    /// CAS 用的保留**后缀**身份（被替换跨度之后，不含跨度之前的保留前缀）。
    /// 构造 <see cref="FrozenCandidateDescriptor"/> 时用它，而不是 <c>RetainedMessages</c> 的全量身份。
    /// </summary>
    IReadOnlyList<string> RetainedSuffixIdentities,
    string SourceGeneration,
    string SourceRevision,
    string? Fingerprint,
    ContextSummaryAllowance? SummaryAllowance = null)
{
    internal static CandidateSelectionResult NotCreated(string reason, int initialUsedTokens) =>
        new(false, reason, -1, 0, 0, initialUsedTokens, initialUsedTokens,
            [], [], [], string.Empty, string.Empty, null);
}

/// <summary>
/// 候选边界与指纹（纯函数）。
/// <para>
/// 硬不变量：
/// ① 前导 System 消息不可移除；
/// ② 最近区按条数及预算分配，边界扩展到完整执行单元；
/// ③ 当前用户输入不可移除；已完成执行单元可压缩；
/// ④ 只按**完整历史轮/执行单元**移除（不会把 tool call 与它的 tool result 拆开，也不会跨 User 边界拼接）。
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
        string sourceRevision,
        ContextRequestBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(options);

        budget ??= new ContextRequestBudget();
        var initialUsed = SumUsed(messages, nonMessageTokens);
        var initialBytes = messages.Sum(m => m.TextBytes) + budget.NonMessageTextBytes;
        var limit = capacity.EffectiveInputTokens;
        var textExceeded = budget.MaxTextBytes > 0 && initialBytes > budget.MaxTextBytes;
        if (initialUsed < limit * Math.Clamp(options.TriggerRatio, 0.05, 1) && !textExceeded)
            return CandidateSelectionResult.NotCreated("below_soft_threshold", initialUsed);

        var target = limit * Math.Clamp(options.TargetRatio, 0.05, 1);
        var textTarget = budget.MaxTextBytes * Math.Clamp(options.TargetRatio, 0.05, 1);
        var tailStart = Math.Max(0, messages.Count - Math.Max(1, options.ProtectedTailMessages));
        // Count is a preference, not an unbounded byte reservation. On hard pressure, allocate
        // at most a quarter of each request budget to recent records; whole-unit parsing below
        // expands the boundary backwards, so even an oversized newest batch stays intact.
        if (textExceeded || initialUsed > limit)
            while (tailStart < messages.Count - 1 &&
                ((textExceeded && messages.Skip(tailStart).Sum(m => m.TextBytes) > budget.MaxTextBytes / 4)
                 || (initialUsed > limit && messages.Skip(tailStart).Sum(m => m.TokenEstimate) > limit / 4)))
                tailStart++;
        var currentUser = -1;
        for (var i = 0; i < messages.Count; i++)
            if (messages[i].Role == ContextMessageRole.User) currentUser = i;

        // System/current input/pending batches are barriers. A candidate is one exact contiguous
        // span, including completed execution units within the active turn. Never bridge a barrier.
        var runs = new List<List<(int Start, int End)>>();
        List<(int Start, int End)>? run = null;
        for (var i = 0; i < messages.Count;)
        {
            var end = i + 1;
            if (messages[i].Role == ContextMessageRole.User && i != currentUser)
            {
                while (end < messages.Count && messages[end].Role is not
                    (ContextMessageRole.User or ContextMessageRole.System)) end++;
            }
            else if (messages[i].Role == ContextMessageRole.Assistant)
            {
                while (end < messages.Count && messages[end].Role == ContextMessageRole.Tool) end++;
            }
            var eligible = i != currentUser && end <= tailStart
                && messages[i].Role is not (ContextMessageRole.System or ContextMessageRole.Tool)
                && HasCompleteToolBatches(messages, i, end);
            if (eligible)
            {
                if (run is null) { run = []; runs.Add(run); }
                run.Add((i, end));
            }
            else run = null;
            i = end;
        }

        CandidateSelectionResult? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var units in runs)
        {
            var from = units[0].Start;
            foreach (var unit in units)
            {
                var removed = messages.Skip(from).Take(unit.End - from).ToArray();
                var removedTokens = removed.Sum(m => Math.Max(0, m.TokenEstimate));
                var removedBytes = removed.Sum(m => m.TextBytes);
                var projected = initialUsed - removedTokens;
                var retainedBytes = initialBytes - removedBytes;
                var tokenAllowance = Math.Max(0, Math.Min(Math.Min(budget.MaxSummaryTokens, removedTokens / 4),
                    limit - projected - budget.CheckpointWrapperTokens));
                var byteAllowance = budget.MaxTextBytes > 0
                    ? Math.Max(0, Math.Min(removedBytes / 4,
                        budget.MaxTextBytes - retainedBytes - budget.CheckpointWrapperBytes))
                    : long.MaxValue;
                // UTF-8 / JSON expansion is estimated conservatively here; final serialized
                // measurement is authoritative, never assume tokens and bytes are interchangeable.
                if (budget.MaxTextBytes > 0)
                    tokenAllowance = (int)Math.Min(tokenAllowance, byteAllowance / 8);
                var allowance = new ContextSummaryAllowance(tokenAllowance, byteAllowance, retainedBytes);
                var fits = tokenAllowance >= 16;
                var meetsTarget = projected + tokenAllowance + budget.CheckpointWrapperTokens <= target
                    && (!textExceeded || retainedBytes + Math.Min(byteAllowance, tokenAllowance * 8L)
                        + budget.CheckpointWrapperBytes <= textTarget);
                var score = (fits ? 10 : 0) + (meetsTarget ? 10 : 0)
                    + (textExceeded ? (double)removedBytes / initialBytes : (double)removedTokens / Math.Max(1, initialUsed));
                if (score > bestScore)
                {
                    var suffix = messages.Skip(unit.End).ToArray();
                    best = new CandidateSelectionResult(true, "candidate_created", from, removed.Length,
                        removedTokens, initialUsed, projected, removed,
                        messages.Take(from).Concat(suffix).ToArray(), suffix.Select(m => m.Identity).ToArray(),
                        sourceGeneration.ToString(), sourceRevision,
                        ComputeFingerprint(sourceGeneration, sourceRevision, routeVersion, toolSpecVersion, removed, suffix),
                        allowance);
                    bestScore = score;
                }
                if (fits && meetsTarget) break;
            }
        }
        return best ?? CandidateSelectionResult.NotCreated("nothing_removable", initialUsed);
    }

    private static bool HasCompleteToolBatches(IReadOnlyList<ContextMessageShape> messages, int start, int end)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < end; i++)
        {
            var message = messages[i];
            if (message.Role == ContextMessageRole.Tool)
            {
                if (message.ToolResultId is null || !pending.Remove(message.ToolResultId)) return false;
            }
            else
            {
                if (pending.Count != 0) return false;
                foreach (var id in message.ToolCallIds ?? [])
                    if (string.IsNullOrEmpty(id) || !pending.Add(id)) return false;
            }
        }
        return pending.Count == 0;
    }

    /// <summary>
    /// 稳定指纹：对（策略版本、源代次、源 revision、路由版本、工具定义版本、被替换跨度身份、
    /// 跨度之后的保留后缀身份）做 SHA-256。只含身份与版本，**不含正文**。
    /// </summary>
    public static string ComputeFingerprint(
        long sourceGeneration,
        string sourceRevision,
        string routeVersion,
        string toolSpecVersion,
        IReadOnlyList<ContextMessageShape> removed,
        IReadOnlyList<ContextMessageShape> retainedSuffix)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(retainedSuffix);

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

        builder.Append("retained-suffix:\n");
        foreach (var message in retainedSuffix)
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

}
