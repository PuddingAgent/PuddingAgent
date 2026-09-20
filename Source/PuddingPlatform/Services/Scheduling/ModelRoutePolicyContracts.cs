namespace PuddingPlatform.Services.Scheduling;

/// <summary>路由选择的软偏好（决定性排序键；不含任何自由文本）。</summary>
public enum RouteSelectionPreference
{
    /// <summary>最低成本优先（Explore/triage 类）。</summary>
    LowestCost,

    /// <summary>质量降序、成本升序（Change/Test 类）。</summary>
    Balanced,

    /// <summary>最高声明质量优先（Plan/high-risk review 类）。</summary>
    HighestQuality,
}

/// <summary>
/// 模型能力档案：硬门（capability / context / tool protocol / quality floor / security）
/// 与软偏好（成本、质量）的声明式来源。
/// 只承载结构化字段——模型身份不得由自由文本决定。
/// </summary>
public sealed record ModelCapabilityProfile(
    string ProviderId,
    string ModelId,
    string? Protocol,
    IReadOnlyList<string> CapabilityTags,
    int ContextWindowTokens,
    bool SupportsToolProtocol,
    decimal QualityScore,
    string SecurityTier,
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens);

/// <summary>
/// per-(taskType, phase) 的路由策略：硬门 + 软偏好 + 首选 provider。
/// 由配置/目录提供，绝不从任务标题或描述派生。
/// </summary>
public sealed record RoutePolicy(
    string TaskType,
    string Phase,
    IReadOnlyList<string> RequiredCapabilityTags,
    bool RequiresToolProtocol,
    string? RequiredSecurityTier,
    decimal MinimumQualityScore,
    int MinimumContextTokens,
    RouteSelectionPreference Preference,
    IReadOnlyList<string> PreferredProviderIds);

/// <summary>WorkUnit 侧的路由输入（结构化；刻意不含标题/描述等自由文本）。</summary>
public sealed record WorkUnitRouteContext(
    string TaskType,
    string Phase,
    string? Risk,
    int RequiredContextTokens);

/// <summary>
/// 路由判定结果。<see cref="Selected"/> 为 false 时 <see cref="Code"/> 给出机器可读拒绝码，
/// <see cref="Reason"/> 由枚举化 token 组成——禁止无解释的模型选择。
/// </summary>
public sealed record ModelRouteDecision(
    bool Selected,
    string Code,
    string? ProviderId,
    string? ModelId,
    string? Protocol,
    string Reason,
    string Fingerprint);

/// <summary>
/// 与阶段绑定的默认策略目录：Explore/triage 偏快低成本；Plan 与 high-risk review 偏高质量；
/// Change 平衡（质量降序→成本升序）；Test/deploy 要求确定性工具协议（工具优先）；
/// Verify 固定只读隔离 route。未知 phase 回落到确定性默认值，而不是抛异常。
/// </summary>
public static class RoutePolicyCatalog
{
    /// <summary>Verifier 专用只读隔离安全层。</summary>
    public const string IsolatedReadOnlySecurityTier = "isolated-readonly";

    /// <summary>常规安全层。</summary>
    public const string StandardSecurityTier = "standard";

    private const decimal DefaultQualityFloor = 0.60m;

    public static RoutePolicy For(string taskType, string phase, int minimumContextTokens = 0)
    {
        var normalizedPhase = Normalize(phase);
        return normalizedPhase switch
        {
            "explore" or "triage" => new(
                taskType, phase, [], RequiresToolProtocol: false, RequiredSecurityTier: null,
                MinimumQualityScore: DefaultQualityFloor, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.LowestCost, PreferredProviderIds: []),

            "plan" or "review" => new(
                taskType, phase, [], RequiresToolProtocol: false, RequiredSecurityTier: null,
                MinimumQualityScore: 0.85m, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.HighestQuality, PreferredProviderIds: []),

            "change" => new(
                taskType, phase, [], RequiresToolProtocol: true, RequiredSecurityTier: null,
                MinimumQualityScore: 0.75m, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.Balanced, PreferredProviderIds: []),

            "test" or "deploy" => new(
                taskType, phase, [], RequiresToolProtocol: true, RequiredSecurityTier: null,
                MinimumQualityScore: 0.70m, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.Balanced, PreferredProviderIds: []),

            "verify" => new(
                taskType, phase, [], RequiresToolProtocol: false,
                RequiredSecurityTier: IsolatedReadOnlySecurityTier,
                MinimumQualityScore: 0.80m, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.Balanced, PreferredProviderIds: []),

            _ => new(
                taskType, phase, [], RequiresToolProtocol: false, RequiredSecurityTier: null,
                MinimumQualityScore: 0.75m, MinimumContextTokens: minimumContextTokens,
                Preference: RouteSelectionPreference.Balanced, PreferredProviderIds: []),
        };
    }

    internal static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
