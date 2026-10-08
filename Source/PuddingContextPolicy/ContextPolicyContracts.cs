namespace PuddingContextPolicy;

/// <summary>
/// 纯策略组件的输入/输出合同。
/// <para>
/// 纪律：这些类型是**最小不可变 DTO**，只带策略需要的整数与稳定身份，不带任何宿主类型
/// （没有 <c>ChatMessage</c>、没有 <c>LlmConfig</c>、没有 <c>ContextUsageSnapshot</c>）。
/// Runtime 在边界处把既有模型转换成这里的形状，转换代码留在 Runtime 一侧。
/// </para>
/// </summary>
public enum ContextMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// 一条消息的策略视图。
/// </summary>
/// <param name="Identity">
/// 稳定身份（消息 id 或内容 hash）。用于跨版本校验「被替换的跨度没变」与去重指纹；
/// **不得**放正文（策略组件不需要正文，避免把内容带出边界）。
/// </param>
/// <param name="Role">角色。</param>
/// <param name="TokenEstimate">该消息的 token 估算（未校准；校准由引用方在边界处理）。</param>
public sealed record ContextMessageShape(
    string Identity,
    ContextMessageRole Role,
    int TokenEstimate);

/// <summary>
/// 有效输入上限（<see cref="ContextCapacityResult.EffectiveInputTokens"/>）比模型窗口小的原因。
/// 取值与 Runtime 的 <c>ContextEffectiveWindowSources</c> **逐字对齐**（同一套词汇，不另造）。
/// </summary>
public static class ContextEffectiveWindowSources
{
    public const string OutputReserve = "output_reserve";
    public const string ProviderInputLimit = "provider_input_limit";
    public const string SafetyMargin = "safety_margin";
    public const string OutputReserveAndSafetyMargin = "output_reserve_and_safety_margin";
    public const string FallbackOutputReserve = "fallback_output_reserve";
    public const string ModelWindow = "model_window";
}

/// <summary>
/// 容量算术的输入（冻结参数）。
/// </summary>
/// <param name="ModelWindowTokens">模型上下文窗口。</param>
/// <param name="ProviderInputLimitTokens">Provider 输入上限；null/非正 = 未配置（不参与取小）。</param>
/// <param name="RequestedOutputBudgetTokens">**实际**输出预算（容量侧给出的真实值）；null = 容量没给。</param>
/// <param name="SafetyBufferTokens">安全余量（与出站硬门禁同一冻结参数）。</param>
/// <param name="FallbackOutputBudgetTokens">
/// 容量没给输出预算时门禁使用的回退预留。它**不是**用户预算，必须与
/// <paramref name="RequestedOutputBudgetTokens"/> 区分，来源记为 <see cref="ContextEffectiveWindowSources.FallbackOutputReserve"/>。
/// </param>
public sealed record ContextCapacityInputs(
    int ModelWindowTokens,
    int? ProviderInputLimitTokens = null,
    int? RequestedOutputBudgetTokens = null,
    int SafetyBufferTokens = 0,
    int FallbackOutputBudgetTokens = 0);

/// <summary>容量算术的结果：各分量 + 有效输入上限 + 受限来源。</summary>
public sealed record ContextCapacityResult(
    int ModelWindowTokens,
    int? ProviderInputLimitTokens,
    int? RequestedOutputBudgetTokens,
    int ReservedOutputTokens,
    int SafetyBufferTokens,
    int EffectiveInputTokens,
    string EffectiveWindowSource)
{
    /// <summary>相对硬边界的余量（负数 = 该用量已越界）。</summary>
    public int Headroom(int usedTokens) => EffectiveInputTokens - Math.Max(0, usedTokens);

    /// <summary>该用量是否越过硬输入边界。</summary>
    public bool ExceedsHardLimit(int usedTokens) => Math.Max(0, usedTokens) > EffectiveInputTokens;
}

/// <summary>
/// 上下文压力分类：**软维护**与**硬保护**必须分开（ADR-095 D2）。
/// </summary>
public enum ContextPressureState
{
    /// <summary>未达软触发阈值：本轮什么都不做。</summary>
    BelowSoftTrigger,

    /// <summary>已达软阈值但请求仍能安全容纳：摘要生成**不得**进入发送关键路径，可延期。</summary>
    SoftEligible,

    /// <summary>估算输入已越过有效输入上限：**必须**同步保护，不得被退避/冷却/软准入跳过。</summary>
    HardProtectionExceeded,
}

/// <summary>压力分类结果。</summary>
public sealed record ContextPressureDecision(
    ContextPressureState State,
    int UsedTokens,
    int EffectiveInputTokens,
    int SoftTriggerTokens,
    int HeadroomTokens,
    string Reason)
{
    /// <summary>软维护是否可延期（即：允许把摘要生成移出发送关键路径）。</summary>
    public bool CanDeferSoftMaintenance => State == ContextPressureState.SoftEligible;

    /// <summary>是否必须同步保护。</summary>
    public bool RequiresSynchronousProtection => State == ContextPressureState.HardProtectionExceeded;
}

/// <summary>策略版本。任一策略语义变化都必须递增，用于让旧候选失效并进入指纹。</summary>
public static class ContextPolicyVersion
{
    public const string Current = "context-policy/1";
}
