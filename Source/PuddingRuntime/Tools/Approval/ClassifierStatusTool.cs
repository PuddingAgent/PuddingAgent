using System.Text.Json;
using Microsoft.Extensions.Options;
using PuddingCode.Classification;
using PuddingCode.Models;
using PuddingCode.Operators;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Thresholds;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Read-only health probe for the security classifier layer (v2 plan §8.2 / §14.10 D5, slice S6a).
/// <para>
/// 只读探查：<b>不授予任何权限、不落任何规则、不触发任何真实分类器网络调用</b>
/// （探查健康 ≠ 触发判断——全部读数来自进程内 <see cref="ClassifierHealthReporter"/> 的既有记录）。
/// </para>
/// <para>
/// 输出（全部为服务端权威读数，§8.2）：各分类器实现的 ClassifierId 与健康状态、最近失败原因码、
/// per-key 连续 deferred 计数与当前退避档、仲裁位是否已注册（未注册时可见 fail-closed 占位 id）、
/// 生效阈值与判据标识（永久类可信度门槛及其判据 id + 版本 / 仲裁超时 / 退避基数与 3/5 档位）以及当前生效的
/// <c>ToolApproval:Reviewer</c> 取值。输出不含任何密钥 / 令牌（对齐 list_llm_providers 脱敏纪律；
/// 参数只以 SHA-256 哈希出现）。
/// </para>
/// <para>
/// 厂商中立：本工具与输出 schema 不写死任何厂商名；ClassifierId 只作为运行期数据出现。
/// </para>
/// </summary>
[Tool(
    id: "classifier_status",
    name: "Classifier status",
    description: "只读探查安全分类器健康面：各分类器实现的 ClassifierId 与健康状态、连续 deferred 计数与退避档、仲裁位注册状态（fail-closed 占位可见）、生效阈值与当前 Reviewer 取值。Read-only classifier health probe; performs no classifier network call and grants no permission.",
    category: ToolCategory.Security,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 27)]
public sealed class ClassifierStatusTool : PuddingToolBase<ClassifierStatusArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ClassifierHealthReporter _healthReporter;
    private readonly ToolCallClassifierPipelineOptions _pipelineOptions;
    private readonly ClassifierArbiterRegistrationState _arbiterState;
    private readonly IToolCallClassifier _classifier;
    private readonly ToolApprovalRuntimeOptions _runtimeOptions;
    private readonly IAcceptanceThresholdPolicyProvider? _thresholdPolicyProvider;

    public ClassifierStatusTool(
        ClassifierHealthReporter healthReporter,
        ToolCallClassifierPipelineOptions pipelineOptions,
        ClassifierArbiterRegistrationState arbiterState,
        IToolCallClassifier classifier,
        IOptions<ToolApprovalRuntimeOptions> runtimeOptions,
        IAcceptanceThresholdPolicyProvider? thresholdPolicyProvider = null)
    {
        _healthReporter = healthReporter ?? throw new ArgumentNullException(nameof(healthReporter));
        _pipelineOptions = pipelineOptions ?? throw new ArgumentNullException(nameof(pipelineOptions));
        _arbiterState = arbiterState ?? throw new ArgumentNullException(nameof(arbiterState));

        // 解析分类器端口（管线）：强制 DI 完成管线组装，保证仲裁位注册状态已被写入。
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _runtimeOptions = runtimeOptions?.Value ?? throw new ArgumentNullException(nameof(runtimeOptions));
        // S2a：可选判据端口（未注入 ⇒ 报告内置默认判据，不报假值、不报空）。
        _thresholdPolicyProvider = thresholdPolicyProvider;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ClassifierStatusArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        _ = _classifier; // 保留引用语义：构造期已强制解析管线（见构造函数注释）。

        // S2a：生效门槛与其判据标识（id + 版本）一并输出；三者同源（单个判据对象），
        // 原数字字段保留不删（向后兼容），未注入端口时数值仍等于既有 options 取值。
        var permanentConfidencePolicy = ResolvePermanentConfidencePolicy();

        var payload = new
        {
            reviewer = _runtimeOptions.Reviewer,
            arbiterRegistered = _arbiterState.ArbiterRegistered,
            arbiterClassifierId = _arbiterState.ArbiterClassifierId,
            thresholds = new
            {
                permanentConfidenceThreshold = permanentConfidencePolicy.RequiredConfidence,
                permanentConfidencePolicyId = permanentConfidencePolicy.PolicyId,
                permanentConfidencePolicyVersion = permanentConfidencePolicy.Version,
                arbiterTimeoutMs = _pipelineOptions.ArbiterTimeoutMs,
                unavailableBackoffBaseMs = _healthReporter.UnavailableBackoffBaseMs,
                degradedAfterConsecutiveDeferred = ClassifierHealthReporter.DegradedAfterConsecutiveDeferred,
                unavailableAfterConsecutiveDeferred = ClassifierHealthReporter.UnavailableAfterConsecutiveDeferred,
                maxRetryAfterMs = ClassifierHealthReporter.MaxRetryAfterMs,
            },
            classifiers = _healthReporter
                .Snapshot()
                .Select(s => new
                {
                    classifierId = s.ClassifierId,
                    health = s.Health.ToString().ToLowerInvariant(),
                    detail = s.Detail,
                    consecutiveFailures = s.ConsecutiveFailures,
                    lastCheckedAtUtc = s.LastCheckedAtUtc,
                    lastLatencyMs = s.LastLatencyMs,
                })
                .ToArray(),
            deferredCounters = _healthReporter
                .SnapshotDeferredCounters()
                .Select(c => new
                {
                    classifierId = c.ClassifierId,
                    toolId = c.ToolId,
                    argumentsHash = c.ArgumentsHash,
                    consecutiveDeferred = c.ConsecutiveDeferred,
                    retryAfterMs = c.RetryAfterMs,
                    lastReasonCode = c.LastReasonCode,
                    lastDeferredAtUtc = c.LastDeferredAtUtc,
                })
                .ToArray(),
        };

        return Task.FromResult(ToolExecutionResult.Ok(
            JsonSerializer.Serialize(payload, JsonOptions)));
    }

    /// <summary>
    /// 生效的永久类门槛判据：注入判据端口时取自端口（与管线同一取值来源），未注入时构造内置默认
    /// （同一 id / 版本 / 与既有 options 同一数值），使两条路径的报告形状一致。
    /// </summary>
    private AcceptanceThresholdPolicy ResolvePermanentConfidencePolicy()
        => _thresholdPolicyProvider?.Resolve(AcceptanceThresholdPolicyIds.PermanentConfidence)
           ?? AcceptanceThresholdPolicies.PermanentConfidence(_pipelineOptions.PermanentConfidenceThreshold);
}

/// <summary><see cref="ClassifierStatusTool"/> 参数：只读探查，无必填项。</summary>
public sealed record ClassifierStatusArgs
{
}
