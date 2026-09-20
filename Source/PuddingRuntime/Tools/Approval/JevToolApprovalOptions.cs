namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Jev 审批评审器配置（配置节 <c>ToolApproval:Jev</c>）。
/// <para>
/// 所有阈值只影响「Jev 判多少、截多少」，不改变 fail-closed 语义：
/// Jev 不可用 / 超时 / 坏答案永远产生 <c>DeferredDependency</c>，
/// 绝不静默回退到其它模型（Jev自动审批与白名单自学习改造方案 §3.2）。
/// </para>
/// </summary>
public sealed class ToolApprovalJevOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "ToolApproval:Jev";

    /// <summary>
    /// 总开关。false 时评审器不调用 Jev，直接返回 <c>DeferredDependency</c>（fail-closed）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>白名单自学习的最低校准概率（noul）。低于该值不产出 AllowlistProposals（方案 I4）。</summary>
    public double AllowlistProbabilityThreshold { get; set; } = 0.90;

    /// <summary>决策状态里 argumentsJson 的 UTF-8 截断字节数（控制 input token 成本）。</summary>
    public int StateTruncateBytes { get; set; } = 8192;

    /// <summary>评审自身 deadline（秒）。到期转 DeferredDependency，与 LLM 评审器的 30s 语义一致。</summary>
    public int ReviewTimeoutSeconds { get; set; } = 30;

    /// <summary>单次决策的提问上限（协议固定 4 问；仅作防御性上限，超出部分被截断）。</summary>
    public int MaxQuestions { get; set; } = 4;
}
