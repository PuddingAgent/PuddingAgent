namespace PuddingCode.Platform;

/// <summary>
/// 思考强度（reasoning effort）的唯一决策点。
///
/// 产品口径（用户裁定 2026-10-24）：
/// ① provider 提供 none / low / high / max，但 PuddingAgent 的实际场景只需 low / high / max；
/// ② **high 是默认**——绝大多数对话、工具调用、代码修改都在这一档；
/// ③ low 只用于闲聊解闷这类不需要推理的对话，属特例而非常态；
/// ④ max 用于困难思考、研究、软件开发这类真的需要深推理的任务。
///
/// 由此推出的两个结论：
/// - 「自动」的基线是 high，而不是 low；升 max 要靠证据，降 low 要靠明确的闲聊信号。
/// - 「深入」不能映射到 high（high 已是默认），只能映射到 max。
/// - none 不对用户暴露，只给压缩摘要这类非推理的内部调用使用。
///
/// 契约（顺序即优先级）：
/// ① 每轮偏好：fast → low，deep → max，auto/未知 → 不表态；
/// ② 已配置值：Agent 模板或模型级 reasoningEffort，必须合法才采用；
/// ③ provider 模型默认值；
/// ④ 都没有 ⇒ high（产品默认，而不是留空让服务端决定）。
///
/// 非法值一律跳过而不是抛出：思考强度写错不应该让一次对话失败，
/// 但必须把被拒绝的原值带回给调用方记进日志。
/// </summary>
public static class ReasoningEffortPolicy
{
    public const string None = "none";
    public const string Low = "low";
    public const string High = "high";
    public const string Max = "max";

    /// <summary>产品默认强度：不指定任何来源时使用。</summary>
    public const string Default = High;

    /// <summary>决策来源；日志必须记录它，否则"谁改的"永远说不清。</summary>
    public enum Origin
    {
        /// <summary>本轮的执行偏好（自动/深入/快速）决定了强度。</summary>
        Preference,
        /// <summary>Agent 模板或模型级配置决定了强度。</summary>
        Configured,
        /// <summary>provider 注册表里的模型默认值决定了强度。</summary>
        ModelDefault,
        /// <summary>没有任何来源表态，取产品默认 high。</summary>
        ProductDefault,
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
        || effort is None or Low or High or Max;

    /// <summary>
    /// 解析最终强度。<paramref name="preference"/> 取 <c>auto</c>/<c>deep</c>/<c>fast</c>，
    /// 大小写与首尾空白不敏感，未知取值按 <c>auto</c> 处理（不阻断对话）。
    /// </summary>
    public static Decision Resolve(string? preference, string? configured, string? modelDefault)
    {
        var normalizedPreference = Normalize(preference);
        var preferred = normalizedPreference switch
        {
            "deep" => Max,
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

        return new Decision(Default, Origin.ProductDefault, FirstRejected(configured, modelDefault, null));
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
