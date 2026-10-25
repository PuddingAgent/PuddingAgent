namespace PuddingCode.Platform;

/// <summary>
/// 思考强度（reasoning effort）的唯一决策点。
///
/// 产品口径（用户裁定 2026-10-24）：
/// ① provider 提供 none / low / high / max，PuddingAgent 只用 low / high / max；
/// ② 前端三档与强度一一对应：快速 = low、深入 = high、极致 = max；
/// ③ high 是默认；
/// ④ none 不对用户暴露，只给压缩摘要这类非推理的内部调用使用。
///
/// 因此「每轮偏好」直接就是档位名，不再做语义转译：
/// 曾经把「深入」映射到 max 是错的——手动档位的语义高于自动基线，
/// 既然界面把「深入」摆在用户面前，它就必须等于 high，而不是被系统再升一档。
///
/// 契约（顺序即优先级）：
/// ① 每轮偏好：low / high / max 直接采用；
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

    /// <summary>
    /// 能力标签：只有声明了该标签的模型才会被补上产品默认值。
    /// 写成常量而不是裸字符串，因为它是跨层约定（注册表打标签、运行时读标签）。
    /// </summary>
    public const string ReasoningCapabilityTag = "reasoning";

    /// <summary>前端三档的展示名，与强度一一对应。</summary>
    public static string Label(string effort) => effort switch
    {
        Low => "快速",
        Max => "极致",
        _ => "深入",
    };

    /// <summary>决策来源；日志必须记录它，否则"谁改的"永远说不清。</summary>
    public enum Origin
    {
        /// <summary>不发送该字段：既无来源，且模型未声明推理能力。</summary>
        Unset,
        /// <summary>本轮的手动档位（快速/深入/极致）决定了强度。</summary>
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
    /// 解析最终强度。<paramref name="preference"/> 取 <c>low</c>/<c>high</c>/<c>max</c>
    /// （即界面上的快速/深入/极致），大小写与首尾空白不敏感；
    /// 其它取值一律按"未表态"处理，不阻断对话。
    ///
    /// <paramref name="useProductDefault"/> 仅在模型声明了 reasoning 能力时置 true。
    /// 理由：openai 协议的网关只要拿到非 null 就把 reasoning_effort 写进报文
    /// （OpenAiLlmGateway.cs:443），对不支持该参数的模型会造成 400。
    /// 显式来源（手动档位 / 模板 / 模型配置）不受此开关影响——它们代表使用者的明确意图，
    /// 改它们属于行为变更，不在"引入产品默认"这件事的授权范围内。
    /// </summary>
    public static Decision Resolve(
        string? preference, string? configured, string? modelDefault, bool useProductDefault = true)
    {
        var preferred = Normalize(preference);
        if (preferred is Low or High or Max)
            return new Decision(preferred, Origin.Preference, FirstRejected(configured, modelDefault, null));

        var configuredValue = Normalize(configured);
        if (configuredValue is not null && IsValid(configuredValue))
            return new Decision(configuredValue, Origin.Configured, null);

        var modelValue = Normalize(modelDefault);
        if (modelValue is not null && IsValid(modelValue))
            return new Decision(modelValue, Origin.ModelDefault, FirstRejected(configured, null, null));

        if (!useProductDefault)
            return new Decision(null, Origin.Unset, FirstRejected(configured, modelDefault, null));

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
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>
    /// 「跟随模型默认」的写入别名：归一化为 null，表示清除该 Agent 的显式配置。
    /// 之所以保留这个名字而不是让界面传空串：命令行的空参数无法与「漏写参数」区分。
    /// </summary>
    public const string AutoSelection = "auto";

    /// <summary>用户在界面/命令入口可以写入的档位。</summary>
    public static bool IsUserSelectable(string? effort)
        => effort is High or Max;

    /// <summary>
    /// 归一化一次用户选择（聊天面板下拉或 <c>/effort</c> 命令）——写入前的唯一裁决点。
    ///
    /// 语义：
    /// ① null / 空白 / <c>auto</c> / <c>default</c> ⇒ <paramref name="effort"/> = null，
    ///    表示清除该 Agent 的显式配置、回到模型默认；
    /// ② <c>high</c> / <c>max</c>（大小写与首尾空白不敏感）⇒ 归一化为小写原值；
    /// ③ 其它一律拒绝并给出原因。low / none / medium 都在拒绝之列：
    ///    provider 视角 low 合法、medium 根本不存在，但对本产品它们都不是可选项，
    ///    静默接受会让用户以为改了档位、实际按默认跑。
    /// </summary>
    public static bool TryNormalizeSelection(string? raw, out string? effort, out string? error)
    {
        effort = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var value = raw.Trim().ToLowerInvariant();
        if (value is AutoSelection or "default")
            return true;

        if (IsUserSelectable(value))
        {
            effort = value;
            return true;
        }

        error = $"unsupported reasoning effort '{raw.Trim()}'; expected one of: high, max, auto";
        return false;
    }
}
