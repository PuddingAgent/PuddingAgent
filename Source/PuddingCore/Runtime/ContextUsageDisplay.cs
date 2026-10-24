namespace PuddingCode.Runtime;

/// <summary>
/// 上下文用量面板的**唯一**口径（用户裁定 2026-10-24）：
/// 前端指示器与后端估算必须同参数、同函数，禁止两头各算一套百分比。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由很具体：面板曾经在「看起来 20%」时压缩已经触发。根因不是阈值配错，而是
/// **同一件事被算了三遍**——前端按「实测 ÷ 有效窗口」算标题、按「估算 ÷ 模型窗口」算分项，
/// 后端压缩判定又用「估算 ÷ 模型窗口」。三套分子分母并存，任何一次改动都只能对齐其中两套。
/// </para>
/// <para>
/// 现在唯一的算式在 <see cref="ContextUsageDisplayProjection.Project"/>：
/// 主指标 = <c>UsedTokens ÷ EffectiveWindowTokens</c>，与压缩判定**逐位相同**的分子分母；
/// Provider 实测只作为**次级参考**（<see cref="MeasuredPercent"/>），用于评估真实节省，不参与门禁。
/// </para>
/// </remarks>
/// <param name="NumeratorTokens">主指标分子（= <see cref="ContextHealthSnapshot.UsedTokens"/>，与压缩判定同一分子）。</param>
/// <param name="DenominatorTokens">主指标分母（= <see cref="ContextHealthSnapshot.EffectiveWindowTokens"/>，与压缩判定同一分母）。</param>
/// <param name="Percent">主指标百分比（0–100，已截断；超过有效窗口显示 100 而不是溢出）。</param>
/// <param name="RemainingPercent">与主指标**同分母**的可用余量（<c>100 − Percent</c>），避免两套标度相加。</param>
/// <param name="InputUnavailableTokens">模型窗口里不可用于输入的 token 数（<c>模型窗口 − 有效窗口</c>）。</param>
/// <param name="InputUnavailablePercent">上述部分的百分比，**分母是模型窗口**（不是有效窗口）。</param>
/// <param name="MeasuredNumeratorTokens">上一次主请求的 Provider 实测输入；null = 尚未采集。</param>
/// <param name="MeasuredPercent">实测 ÷ 有效窗口；null = 尚未采集。仅供评估效率，不参与任何门禁。</param>
/// <param name="Segments">分层构成（各段之和 ≤ <see cref="Percent"/>），前端不再自行缩放。</param>
public sealed record ContextUsageDisplay(
    int NumeratorTokens,
    int DenominatorTokens,
    double Percent,
    double RemainingPercent,
    int InputUnavailableTokens,
    double InputUnavailablePercent,
    int? MeasuredNumeratorTokens,
    double? MeasuredPercent,
    IReadOnlyList<ContextUsageSegment> Segments);

/// <summary>
/// 上下文用量的一层构成。<see cref="Key"/> 与前端图例一一对应，段宽 = <see cref="Percent"/>。
/// </summary>
public sealed record ContextUsageSegment(string Key, int Tokens, double Percent);

/// <summary>
/// <see cref="ContextUsageDisplay"/> 的唯一实现。纯函数：只读快照字段，无副作用、无外部状态。
/// </summary>
public static class ContextUsageDisplayProjection
{
    /// <summary>
    /// 从健康快照投影出面板显示值。**任何**新的一侧（前端/诊断/Agent 工具）都必须复用本方法，
    /// 不得自行把 <c>UsedTokens</c> 或 <c>LastMeasuredPromptTokens</c> 拿来除别的分母。
    /// </summary>
    public static ContextUsageDisplay Project(ContextHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 分母下限 1：有效窗口在契约上 ≥ 1（ContextHealthEvaluator 已 Math.Max(1, …)），
        // 但投影是公开纯函数，调用方可能传手工构造的快照 —— 除零必须在这里兜住，
        // 而不是让 NaN 流到 UI 变成 "NaN%".
        var denominator = Math.Max(1, snapshot.EffectiveWindowTokens);
        var numerator = Math.Max(0, snapshot.UsedTokens);
        // 与压缩判定同一算式：ContextHealthEvaluator 的 gateRatio = UsedTokens / EffectiveWindowTokens。
        // 显示侧只多做一件事 —— 截断到 0–100（超限在快照里另有 GateRatio 原始值可查）。
        var percent = ClampPercent(numerator / (double)denominator * 100d);

        var modelWindow = Math.Max(1, snapshot.ContextWindowTokens);
        var inputUnavailable = Math.Max(0, modelWindow - denominator);
        var inputUnavailablePercent = ClampPercent(inputUnavailable / (double)modelWindow * 100d);

        // 实测可以为 0（真实的 0 token 请求），所以只判 null 与负数，不判 0。
        int? measured = snapshot.LastMeasuredPromptTokens is >= 0 ? snapshot.LastMeasuredPromptTokens : null;
        double? measuredPercent = measured is null
            ? null
            : ClampPercent(measured.Value / (double)denominator * 100d);

        return new ContextUsageDisplay(
            numerator,
            denominator,
            percent,
            Math.Max(0, 100d - percent),
            inputUnavailable,
            inputUnavailablePercent,
            measured,
            measuredPercent,
            BuildSegments(snapshot, numerator, denominator));
    }

    /// <summary>
    /// 分层构成按「各桶之和 = 已使用」等比缩放后给出段宽（沿用既有口径）：
    /// 桶值来自请求组装期估算，与 <paramref name="numerator"/> 未必同源，
    /// 不缩放会出现「分项之和 38.3% 而标题 27.5%」。缩放后各段之和恒等于标题百分比。
    /// </summary>
    private static IReadOnlyList<ContextUsageSegment> BuildSegments(
        ContextHealthSnapshot snapshot,
        int numerator,
        int denominator)
    {
        var buckets = new (string Key, int? Value)[]
        {
            ("systemPrompt", snapshot.SystemPromptTokens),
            ("toolDefinitions", snapshot.ToolDefinitionTokens),
            ("compactionSummary", snapshot.CompactionSummaryTokens),
            ("conversation", snapshot.ConversationTokens),
            ("reasoning", snapshot.ReasoningTokens),
            ("toolResults", snapshot.ToolResultTokens),
        };

        var total = 0;
        foreach (var bucket in buckets)
            total += Math.Max(0, bucket.Value ?? 0);

        // 无分层数据、或已使用为 0 ⇒ 空列表（前端回落单色条，不伪造色段）。
        if (total <= 0 || numerator <= 0)
            return Array.Empty<ContextUsageSegment>();

        var scale = numerator / (double)total;
        var segments = new List<ContextUsageSegment>(buckets.Length);
        foreach (var bucket in buckets)
        {
            var raw = Math.Max(0, bucket.Value ?? 0);
            if (raw == 0)
                continue;
            var tokens = (int)Math.Round(raw * scale, MidpointRounding.AwayFromZero);
            if (tokens <= 0)
                continue;
            segments.Add(new ContextUsageSegment(
                bucket.Key,
                tokens,
                tokens / (double)denominator * 100d));
        }

        return segments;
    }

    private static double ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Clamp(value, 0d, 100d);
    }
}
