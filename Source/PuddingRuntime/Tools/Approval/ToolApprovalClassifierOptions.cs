namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 安全分类器降级行为配置（方案 v2 §14.9 配置表，切片 S6a）。
/// <para>
/// 只新增键，不改任何既有默认值：<c>ToolApproval:Reviewer</c> 的默认值（classifier）与本类无关；
/// <see cref="OnClassifierUnavailable"/> 的默认值 <c>deferred</c> 与 §14.9.1 实证结论一致，
/// 本切片不读取也不改写该键的判定路径（仍由既有链路执行），仅作为配置形状补全，
/// 供 <c>classifier_status</c> 工具展示生效配置。
/// </para>
/// </summary>
public sealed class ToolApprovalClassifierOptions
{
    /// <summary>配置节名（§14.9）。</summary>
    public const string SectionName = "ToolApproval:Classifier";

    /// <summary>分类器不可用时的降级动作默认值：<c>deferred</c>（§14.9 / §14.9.1，已实证）。</summary>
    public const string DefaultOnClassifierUnavailable = "deferred";

    /// <summary>退避基数默认值：2000 ms（§14.9 / §14.9.2）。</summary>
    public const int DefaultUnavailableBackoffBaseMs = 2000;

    /// <summary>
    /// 分类器不可用时的降级动作：deferred | deny_once | allow_once（不建议 allow_once）。
    /// 默认 deferred；本切片不改变其判定路径，仅承载配置形状（§14.9 原表键）。
    /// </summary>
    public string OnClassifierUnavailable { get; set; } = DefaultOnClassifierUnavailable;

    /// <summary>
    /// 同一 <c>(tool_id, args_hash)</c> 连续 deferred 的退避基数（毫秒），§14.9.2：
    /// retry_after_ms = 基数 × 2^(n-1)，封顶 60 000；由
    /// <see cref="ClassifierHealthReporter"/> 读取并计算。
    /// </summary>
    public int UnavailableBackoffBaseMs { get; set; } = DefaultUnavailableBackoffBaseMs;
}
