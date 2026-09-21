using Microsoft.Extensions.Configuration;
using PuddingCode.Operators;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Thresholds;

/// <summary>
/// 默认判据解析实现：值来源全部是<b>既有的单一事实源</b>——不新增配置键、不改变任何既有键的语义。
/// <list type="bullet">
/// <item>永久类门槛 ⇒ 既有的 <see cref="ToolCallClassifierPipelineOptions.PermanentConfidenceThreshold"/>
/// 实例值（未提供选项实例时退回既有常量 <see cref="ToolCallClassifierPipelineOptions.DefaultPermanentConfidenceThreshold"/>）；</item>
/// <item>规则沉淀门槛 ⇒ 既有的 <see cref="ClassificationRuleCurator.SuggestedExpiryConfidenceThreshold"/> 常量；</item>
/// <item>白名单概率门槛 ⇒ 既有配置节 <c>ToolApproval:Jev</c>（且与评审器读同一键、同一默认值）。</item>
/// </list>
/// <para>
/// 未配置时解析结果必须等于既有常量（0.90 / 0.95 / 0.90）：这是「不配置 = 行为逐位不变」的证据来源。
/// </para>
/// </summary>
public sealed class DefaultAcceptanceThresholdPolicyProvider : IAcceptanceThresholdPolicyProvider
{
    private readonly IConfiguration? _configuration;
    private readonly ToolCallClassifierPipelineOptions? _pipelineOptions;

    /// <summary>
    /// 两个依赖均为可选：都不提供时仍能解析（全部退回既有常量），
    /// 使「未注入」与「已注入」两条路径的取值必然相等。
    /// </summary>
    /// <param name="configuration">配置源；仅用于读既有的白名单概率门槛键。</param>
    /// <param name="pipelineOptions">既有的管线可调参数实例（单一事实源）；未提供时退回其既有常量。</param>
    public DefaultAcceptanceThresholdPolicyProvider(
        IConfiguration? configuration = null,
        ToolCallClassifierPipelineOptions? pipelineOptions = null)
    {
        _configuration = configuration;
        _pipelineOptions = pipelineOptions;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="policyId"/> 为空。</exception>
    /// <exception cref="KeyNotFoundException">id 未注册（fail-closed，不猜测默认值）。</exception>
    public AcceptanceThresholdPolicy Resolve(string policyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);

        return policyId switch
        {
            AcceptanceThresholdPolicyIds.PermanentConfidence => AcceptanceThresholdPolicies.PermanentConfidence(
                _pipelineOptions?.PermanentConfidenceThreshold
                ?? ToolCallClassifierPipelineOptions.DefaultPermanentConfidenceThreshold),

            AcceptanceThresholdPolicyIds.SuggestedExpiryConfidence => AcceptanceThresholdPolicies.SuggestedExpiryConfidence(
                ClassificationRuleCurator.SuggestedExpiryConfidenceThreshold),

            AcceptanceThresholdPolicyIds.AllowlistProbability => AcceptanceThresholdPolicies.AllowlistProbability(
                ResolveAllowlistProbability()),

            _ => throw new KeyNotFoundException(
                $"未注册的验收判据 id：{policyId}。解析不到判据必须 fail-closed（不得猜测默认值）。"),
        };
    }

    /// <summary>
    /// 读既有配置键 <c>ToolApproval:Jev:AllowlistProbabilityThreshold</c>（与评审器同一读法：同节、同键、同默认）；
    /// 未配置 ⇒ 等于 <see cref="ToolApprovalJevOptions"/> 的既有属性默认值。
    /// </summary>
    private double ResolveAllowlistProbability()
        => (_configuration?.GetSection(ToolApprovalJevOptions.SectionName).Get<ToolApprovalJevOptions>()
            ?? new ToolApprovalJevOptions()).AllowlistProbabilityThreshold;
}
