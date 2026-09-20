using System.Security.Cryptography;
using System.Text;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 阶段感知的确定性模型路由求值器。
///
/// 设计约束（与 <see cref="TaskAgentRouteMatcher"/> 同范式）：
/// ① 纯函数、无副作用、不访问数据库或可热变的模型目录；
/// ② 输入只有结构化记录——<see cref="WorkUnitRouteContext"/> 刻意不含标题/描述，自由文本在类型层面不可达；
/// ③ 同一 snapshot 输入得到逐字相同结果，<see cref="ModelRouteDecision.Fingerprint"/> 与候选顺序无关；
/// ④ 硬门顺序固定（capability → context → tool protocol → quality floor → security），
///    失败时返回机器可读码；选中时 Reason 由枚举化 token 组成。
/// </summary>
public static class ModelRoutePolicyEvaluator
{
    public const string SelectedCode = "route_selected";
    public const string NoCompatibleCode = "no_compatible_route";
    public const string PolicyContextMismatchCode = "policy_context_mismatch";
    public const string CapabilityMissingPrefix = "capability_missing:";
    public const string ContextWindowTooSmallCode = "context_window_too_small";
    public const string ToolProtocolUnsupportedCode = "tool_protocol_unsupported";
    public const string QualityFloorNotMetCode = "quality_floor_not_met";
    public const string SecurityTierMismatchCode = "security_tier_mismatch";

    /// <summary>
    /// 在候选档案中按策略选出一个兼容路由。
    /// 无任何候选通过硬门时**不静默回退**：返回未选中 + 拒绝码 + 理由 + 指纹。
    /// </summary>
    public static ModelRouteDecision Evaluate(
        RoutePolicy policy,
        WorkUnitRouteContext context,
        IReadOnlyList<ModelCapabilityProfile> candidates)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);

        var reason = BuildReason(policy, context);
        var fingerprint = Fingerprint(policy, context, candidates);

        if (!string.Equals(Normalize(policy.TaskType), Normalize(context.TaskType), StringComparison.Ordinal)
            || !string.Equals(Normalize(policy.Phase), Normalize(context.Phase), StringComparison.Ordinal))
        {
            return new ModelRouteDecision(
                false, PolicyContextMismatchCode, null, null, null, reason, fingerprint);
        }

        var eligible = new List<ModelCapabilityProfile>();
        string? firstFailure = null;
        foreach (var candidate in StableOrder(candidates))
        {
            var failure = EvaluateGates(policy, context, candidate);
            if (failure is null)
                eligible.Add(candidate);
            else
                firstFailure ??= failure;
        }

        if (eligible.Count == 0)
        {
            return new ModelRouteDecision(
                false, firstFailure ?? NoCompatibleCode, null, null, null, reason, fingerprint);
        }

        var winner = PreferenceOrder(policy, eligible)[0];
        return new ModelRouteDecision(
            true, SelectedCode, winner.ProviderId, winner.ModelId, winner.Protocol, reason, fingerprint);
    }

    /// <summary>
    /// 规范化指纹：覆盖策略、上下文与**按 provider/model 排序后**的候选集 ⇒ 与候选输入顺序无关。
    /// </summary>
    public static string Fingerprint(
        RoutePolicy policy,
        WorkUnitRouteContext context,
        IReadOnlyList<ModelCapabilityProfile> candidates)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);

        var canonical = string.Join('\n',
            Normalize(policy.TaskType),
            Normalize(policy.Phase),
            string.Join(',', Normalized(policy.RequiredCapabilityTags)),
            policy.RequiresToolProtocol ? "1" : "0",
            Normalize(policy.RequiredSecurityTier),
            policy.MinimumQualityScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            policy.MinimumContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            policy.Preference.ToString(),
            string.Join(',', Normalized(policy.PreferredProviderIds)),
            Normalize(context.TaskType),
            Normalize(context.Phase),
            Normalize(context.Risk),
            context.RequiredContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join('\n', Candidates(candidates)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? EvaluateGates(
        RoutePolicy policy,
        WorkUnitRouteContext context,
        ModelCapabilityProfile candidate)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in candidate.CapabilityTags)
            AddTag(tags, tag);

        foreach (var required in Normalized(policy.RequiredCapabilityTags))
        {
            if (!tags.Contains(required))
                return $"{CapabilityMissingPrefix}{required}";
        }

        var requiredContext = Math.Max(policy.MinimumContextTokens, context.RequiredContextTokens);
        if (requiredContext > candidate.ContextWindowTokens)
            return ContextWindowTooSmallCode;

        if (policy.RequiresToolProtocol && !candidate.SupportsToolProtocol)
            return ToolProtocolUnsupportedCode;

        if (candidate.QualityScore < policy.MinimumQualityScore)
            return QualityFloorNotMetCode;

        if (!string.IsNullOrWhiteSpace(policy.RequiredSecurityTier)
            && !string.Equals(
                Normalize(policy.RequiredSecurityTier),
                Normalize(candidate.SecurityTier),
                StringComparison.Ordinal))
        {
            return SecurityTierMismatchCode;
        }

        return null;
    }

    /// <summary>软偏好排序：首选 provider 优先，其次按偏好键，最后以 provider/model 序数稳定收口。</summary>
    private static List<ModelCapabilityProfile> PreferenceOrder(
        RoutePolicy policy,
        List<ModelCapabilityProfile> eligible)
    {
        var preferred = Normalized(policy.PreferredProviderIds).ToList();
        var ordered = policy.Preference switch
        {
            RouteSelectionPreference.LowestCost => eligible
                .OrderBy(item => item.OutputCostPerMillionTokens)
                .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase),
            RouteSelectionPreference.HighestQuality => eligible
                .OrderByDescending(item => item.QualityScore)
                .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase),
            _ => eligible
                .OrderByDescending(item => item.QualityScore)
                .ThenBy(item => item.OutputCostPerMillionTokens)
                .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ModelId, StringComparer.OrdinalIgnoreCase),
        };

        return preferred.Count == 0
            ? ordered.ToList()
            : ordered
                .OrderByDescending(item => preferred.Contains(Normalize(item.ProviderId)))
                .ToList();
    }

    private static IEnumerable<ModelCapabilityProfile> StableOrder(
        IReadOnlyList<ModelCapabilityProfile> candidates)
        => candidates
            .OrderBy(item => Normalize(item.ProviderId), StringComparer.Ordinal)
            .ThenBy(item => Normalize(item.ModelId), StringComparer.Ordinal);

    private static IEnumerable<string> Candidates(IReadOnlyList<ModelCapabilityProfile> candidates)
        => StableOrder(candidates).Select(item => string.Join('\t',
            Normalize(item.ProviderId),
            Normalize(item.ModelId),
            Normalize(item.Protocol),
            string.Join(',', Normalized(item.CapabilityTags)),
            item.ContextWindowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.SupportsToolProtocol ? "1" : "0",
            item.QualityScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            Normalize(item.SecurityTier),
            item.InputCostPerMillionTokens.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            item.OutputCostPerMillionTokens.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>理由由枚举化 token 组成（无自由文本），使「无解释模型选择」在结构上不可能发生。</summary>
    private static string BuildReason(RoutePolicy policy, WorkUnitRouteContext context)
        => string.Join(';',
            $"tasktype={Normalize(policy.TaskType)}",
            $"phase={Normalize(policy.Phase)}",
            $"risk={Normalize(context.Risk)}",
            $"preference={policy.Preference}",
            $"qualityfloor={policy.MinimumQualityScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}",
            $"contextrequired={Math.Max(policy.MinimumContextTokens, context.RequiredContextTokens).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"security={Normalize(policy.RequiredSecurityTier)}");

    private static IEnumerable<string> Normalized(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);

    private static void AddTag(ISet<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target.Add(Normalize(value));
    }

    private static string Normalize(string? value)
        => RoutePolicyCatalog.Normalize(value);
}
