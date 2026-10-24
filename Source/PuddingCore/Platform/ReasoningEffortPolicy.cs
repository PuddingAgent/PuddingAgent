namespace PuddingCode.Platform;

/// <summary>
/// 思考强度（reasoning effort）的唯一决策点。
///
/// 为什么需要一个独立函数：effort 现在有三个来源（每轮偏好、Agent/模型配置、provider 默认），
/// 而此前只有一句 <c>FirstNonBlank</c>，既没有合法值校验，也没有记录"最终是谁决定的"。
/// 结果是写错一个字母会静默退化到服务端默认，日志里看不出发生过什么。
///
/// 契约（顺序即优先级）：
/// ① 每轮偏好：deep → high，fast → low，auto/空 → 不表态；
/// ② 已配置值：Agent 模板或模型级 reasoningEffort，必须合法才采用；
/// ③ provider 模型默认值；
/// ④ 都没有 ⇒ null，表示不发送该字段，由服务端决定。
///
/// 非法值一律跳过而不是抛出：思考强度写错不应该让一次对话失败，
/// 但必须把被拒绝的原值带回给调用方记进日志。
/// </summary>
public static class ReasoningEffortPolicy
{
    public const string None = "none";
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    /// <summary>决策来源；日志必须记录它，否则"谁改的"永远说不清。</summary>
    public enum Origin
    {
        /// <summary>没有任何来源表态，请求里不出现该字段。</summary>
        Unset,
        /// <summary>本轮的执行偏好（自动/深入/快速）决定了强度。</summary>
        Preference,
        /// <summary>Agent 模板或模型级配置决定了强度。</summary>
        Configured,
        /// <summary>provider 注册表里的模型默认值决定了强度。</summary>
        ModelDefault,
    }

    /// <summary>一次决策的结果：最终强度、来源、以及被跳过的非法值（若有）。</summary>
    public readonly record struct Decision(string? Effort, Origin Source, string? RejectedValue)
    {
        public bool IsSet => Effort is not null;
    }

    /// <summary>合法强度集合；null / 空白视为"未表态"，不算非法。</summary>
    public static bool IsValid(string? effort)
        => effort is null
        || effort.Length == 0
        || effort is None or Minimal or Low or Medium or High;

    /// <summary>
    /// 解析最终强度。<paramref name="preference"/> 取 <c>auto</c>/<c>deep</c>/<c>fast</c>，
    /// 大小写与首尾空白不敏感，未知取值按 <c>auto</c> 处理（不阻断对话）。
    /// </summary>
    public static Decision Resolve(string? preference, string? configured, string? modelDefault)
    {
        var normalizedPreference = Normalize(preference);
        var preferred = normalizedPreference switch
        {
            "deep" => High,
            "fast" => Low,
            _ => null,
        };
        if (preferred is not null)
            return new Decision(preferred, Origin.Preference, FirstRejected(null, configured, modelDefault));

        var configuredValue = Normalize(configured);
        if (configuredValue is not null && IsValid(configuredValue))
            return new Decision(configuredValue, Origin.Configured, null);

        var modelValue = Normalize(modelDefault);
        if (modelValue is not null && IsValid(modelValue))
            return new Decision(modelValue, Origin.ModelDefault, FirstRejected(configured, null, null));

        return new Decision(null, Origin.Unset, FirstRejected(configured, modelDefault, null));
    }

    private static string? FirstRejected(string? first, string? second, string? third)
        => Rejected(first) ?? Rejected(second) ?? Rejected(third);

    private static string? Rejected(string? candidate)
    {
        var value = Normalize(candidate);
        return value is not null && !IsValid(value) ? value : null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().ToLowerInvariant();
    }
}
