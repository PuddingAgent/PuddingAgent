using PuddingCode.Operators;

namespace PuddingRuntime.Thresholds;

/// <summary>
/// 三个逐标签验收门槛的稳定 id（<b>集中定义</b>，不得散落到各消费点）。
/// <para>
/// 为什么 id 是运行时层的事：其中一项 id 天然携带供应商段（该门槛本就只属于该供应商路径的
/// 白名单提案），而契约层（<c>PuddingCore/Operators</c>）受架构门禁约束、不得出现供应商名词。
/// 因此 id 集中在本文件顶部定义，消费点只引用常量。
/// </para>
/// </summary>
public static class AcceptanceThresholdPolicyIds
{
    /// <summary>永久类结论逐分类可信度门槛（既有默认 0.90）。</summary>
    public const string PermanentConfidence = "tool_approval.permanent_confidence";

    /// <summary>规则沉淀置信度门槛（既有默认 0.95）。</summary>
    public const string SuggestedExpiryConfidence = "classification.rule.suggested_expiry_confidence";

    /// <summary>白名单提案校准概率门槛（既有默认 0.90）。</summary>
    public const string AllowlistProbability = "tool_approval.jev.allowlist_probability";

    /// <summary>
    /// 内置版本号。判据被移动过一次之后必须递增——否则历史结果无法自证用的是哪一版
    /// （见 <see cref="IVersionedCriterion.Version"/>）。
    /// </summary>
    public const int BuiltInVersion = 1;
}

/// <summary>
/// 内置判据的构造点：id / 版本 / 默认值三者只在这里定义一次，供解析实现与可观测面共用，
/// 避免「同一个门槛在多处各写一个默认值」。
/// </summary>
public static class AcceptanceThresholdPolicies
{
    /// <summary>
    /// 永久类结论逐分类可信度门槛：永久类裁决需对应标签可信度达标，否则降级为对应单次类。
    /// </summary>
    public static AcceptanceThresholdPolicy PermanentConfidence(double requiredConfidence)
        => AcceptanceThresholdPolicy.Create(
            AcceptanceThresholdPolicyIds.PermanentConfidence,
            AcceptanceThresholdPolicyIds.BuiltInVersion,
            requiredConfidence,
            appliesToLabels: ["allow_permanent", "deny_permanent"],
            note: "永久类结论逐分类可信度门槛；缺失或低于门槛 ⇒ 降级为对应单次类。");

    /// <summary>
    /// 规则沉淀置信度门槛：低于或缺失 ⇒ 建议 30 天有效期（保守，不落永久规则）。
    /// </summary>
    public static AcceptanceThresholdPolicy SuggestedExpiryConfidence(double requiredConfidence)
        => AcceptanceThresholdPolicy.Create(
            AcceptanceThresholdPolicyIds.SuggestedExpiryConfidence,
            AcceptanceThresholdPolicyIds.BuiltInVersion,
            requiredConfidence,
            note: "规则沉淀置信度门槛；缺失或低于门槛 ⇒ 建议 30 天有效期。");

    /// <summary>
    /// 白名单提案校准概率门槛：低于或缺失 ⇒ 不产出提案（保守，不提案）。
    /// </summary>
    public static AcceptanceThresholdPolicy AllowlistProbability(double requiredConfidence)
        => AcceptanceThresholdPolicy.Create(
            AcceptanceThresholdPolicyIds.AllowlistProbability,
            AcceptanceThresholdPolicyIds.BuiltInVersion,
            requiredConfidence,
            note: "白名单提案校准概率门槛；缺失或低于门槛 ⇒ 不产出提案。");
}
