using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 安全分类器驱动的审批评审器（<see cref="IToolApprovalReviewer"/> 适配器，方案 v2 §14.13 切片 S3b）：
/// 把审批出题单映射为 <see cref="ToolCallClassificationContext"/>，只经抽象
/// <see cref="IToolCallClassifier"/> 求值，再按固定映射表转成审批结果。
/// <para>
/// 零厂商词汇：本类型不感知任何具体分类器实现；生产接线时注入
/// <see cref="PuddingRuntime.Classification.ToolCallClassifierPipeline"/> 实例即可。
/// </para>
/// <para>
/// 结论映射（§14.5，严格四选一，不得改动）：
/// <list type="bullet">
/// <item><see cref="ClassificationOutcome.AllowOnce"/> ⇒ Approved + <see cref="ToolApprovalScope.Once"/>，无提案；</item>
/// <item><see cref="ClassificationOutcome.AllowPermanent"/> ⇒ Approved + Once（票据仍为单次）+ allow 提案（供上层策展器落规则，本评审器不落库）；</item>
/// <item><see cref="ClassificationOutcome.DenyOnce"/> ⇒ Denied，无提案；</item>
/// <item><see cref="ClassificationOutcome.DenyPermanent"/> ⇒ Denied + deny 提案（供上层策展器落规则）；</item>
/// <item><see cref="ClassificationOutcome.Unknown"/> ⇒ <see cref="ToolApprovalDecision.DeferredDependency"/>
///（ADR-091 §4.4：依赖等待不得折叠为 Approved/Denied/NeedHuman，§14.7）。</item>
/// </list>
/// </para>
/// <para>
/// 异常契约（§14.7/§14.9.2）：分类器抛异常 ⇒ DeferredDependency +
/// <see cref="ToolApprovalWire.CodeServiceUnavailable"/>，不冒泡、不放行、不折叠为人工；
/// 外层调用方取消触发的 <see cref="OperationCanceledException"/> 照常传播。
/// 每次评审对分类器至多请求一次，不做重试风暴。
/// <see cref="ToolApprovalReviewResult.RequiresHumanAuthorization"/> 一律 <c>false</c>；
/// <see cref="ToolApprovalReviewResult.ReviewerModel"/> 直接取自裁决（<c>verdict.ClassifierModel</c>），不伪造字段。
/// </para>
/// </summary>
public sealed class ClassifierToolApprovalReviewer : IToolApprovalReviewer
{
    /// <summary>注入 <see cref="ToolCallClassificationContext.RecentTrajectory"/> 的最大字符数（§14.5 有界载荷）。</summary>
    public const int MaxTrajectoryChars = 512;

    /// <summary>分类器原始理由并入审计 Reason（detail= 分量）的最大字符数。</summary>
    public const int MaxVerdictReasonChars = 400;

    /// <summary>逐分类可信度的规范键顺序（对齐 <see cref="ClassificationVerdict.PerOutcomeConfidence"/> 契约）；同输入同输出。</summary>
    private static readonly string[] CanonicalConfidenceKeys =
    [
        "allow_once",
        "allow_permanent",
        "deny_once",
        "deny_permanent",
    ];

    private readonly IToolCallClassifier _classifier;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造分类器审批评审器。</summary>
    /// <param name="classifier">分类器抽象端口；生产接线传管线实例，测试传 stub。</param>
    /// <param name="timeProvider">时间源（用于评审耗时入审计 Reason）；缺省系统时钟。</param>
    public ClassifierToolApprovalReviewer(IToolCallClassifier classifier, TimeProvider? timeProvider = null)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(descriptor);

        var startTimestamp = _timeProvider.GetTimestamp();
        ClassificationVerdict? verdict;
        try
        {
            // §14.9.2：单次评审只请求分类器一次，不做重试风暴。
            verdict = await _classifier.ClassifyAsync(ToContext(request, identity), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方主动取消：照常向上传播（ADR-091 §4.4，依赖等待语义不含调用者取消）。
            throw;
        }
        catch (Exception exception)
        {
            // 分类器异常/自身超时/不可用：降级为依赖等待（§14.7），不折叠为批准/拒绝/人工，不冒泡。
            return Deferred(
                $"Tool call classifier is unavailable (exception: {exception.GetType().Name}); waiting for the dependency to recover.",
                ToolApprovalWire.CodeServiceUnavailable);
        }

        if (verdict is null)
        {
            return Deferred(
                "Tool call classifier returned no verdict; waiting for the dependency to recover.",
                ToolApprovalWire.CodeServiceUnavailable);
        }

        var latencyMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return ToReviewResult(request, verdict, latencyMs);
    }

    private static ToolCallClassificationContext ToContext(ToolApprovalTicketRequest request, ToolApprovalIdentity identity)
        => new()
        {
            ToolId = request.ToolId,
            CommandName = request.CommandName,
            ArgumentsJson = request.RequestedArgumentsJson,
            WorkingDirectory = FirstWorkingDirectory(request),

            // 出题单契约不携带 shell 信息：保持 null，不伪造（§14.12.8 不破坏既有语义）。
            Shell = null,
            OperationContext = request.OperationContext,
            Purpose = request.Purpose,
            Necessity = request.Necessity,
            FactBasis = request.FactBasis,
            TargetResources = request.TargetResources,
            IsIrreversibleOperation = request.IsIrreversibleOperation,
            MayDamageOrDeleteData = request.MayDamageOrDeleteData,
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,

            // §14.5：轨迹必须有界——只放出题单要点的短摘要，禁止塞入整段历史。
            RecentTrajectory = BuildBoundedTrajectory(request),

            // 规则命中由分类器/管线内部求值；适配器没有规则信息，不注入、不伪造。
            MatchedRuleSummaries = [],
        };

    private static ToolApprovalReviewResult ToReviewResult(
        ToolApprovalTicketRequest request,
        ClassificationVerdict verdict,
        double latencyMs)
    {
        var reason = BuildReason(verdict, latencyMs);
        ToolApprovalReviewResult result = verdict.Outcome switch
        {
            ClassificationOutcome.AllowOnce => Approved(reason, proposal: null),
            ClassificationOutcome.AllowPermanent => Approved(reason, BuildProposal(request, verdict, ToolApprovalRuleEffect.Allow)),
            ClassificationOutcome.DenyOnce => Denied(reason, proposal: null),
            ClassificationOutcome.DenyPermanent => Denied(reason, BuildProposal(request, verdict, ToolApprovalRuleEffect.Deny)),

            // Unknown：未产生有效裁决 ⇒ 依赖等待（绝不得映射为 Approved/Denied/NeedHuman）。
            _ => Deferred(reason, ToolApprovalWire.CodeClassifierUnknown),
        };

        // 溯源字段直接取自裁决，不伪造（无模型实现为 null）。
        return result with { ReviewerModel = verdict.ClassifierModel };
    }

    private static ToolApprovalReviewResult Approved(string reason, ToolApprovalAllowlistProposal? proposal)
        => new()
        {
            Decision = ToolApprovalDecision.Approved,
            DecisionReason = reason,
            AllowedScope = ToolApprovalScope.Once,
            RequiresHumanAuthorization = false,
            AllowlistProposals = proposal is null ? [] : [proposal],
        };

    private static ToolApprovalReviewResult Denied(string reason, ToolApprovalAllowlistProposal? proposal)
        => new()
        {
            Decision = ToolApprovalDecision.Denied,
            DecisionReason = reason,
            AllowedScope = null,
            RequiresHumanAuthorization = false,
            AllowlistProposals = proposal is null ? [] : [proposal],
        };

    private static ToolApprovalReviewResult Deferred(string reason, string reasonCode)
        => new()
        {
            Decision = ToolApprovalDecision.DeferredDependency,
            DecisionReason = reason,
            AllowedScope = null,
            RequiresHumanAuthorization = false,
            ReasonCode = reasonCode,
        };

    /// <summary>审计 Reason：非空且必含分类器结论与可信度摘要；不伪造任何字段。</summary>
    private static string BuildReason(ClassificationVerdict verdict, double latencyMs)
    {
        var model = verdict.ClassifierModel ?? "n/a";
        var reason = FormattableString.Invariant(
            $"classifier outcome={verdict.Outcome} confidence={FormatConfidence(verdict)} classifierId={verdict.ClassifierId} model={model} latencyMs={latencyMs:F1}");
        if (!string.IsNullOrEmpty(verdict.ReasonCode))
            reason += FormattableString.Invariant($" code={verdict.ReasonCode}");
        return $"{reason} detail={Truncate(verdict.Reason, MaxVerdictReasonChars)}";
    }

    /// <summary>
    /// 永久类提案（供上层策展器落规则；本评审器不落库）。提案记录本身没有 Effect 字段
    ///（稳定契约不可改），效果按代码库 key=value 惯例编码进 Reason 首段，allow / deny 双向可解析。
    /// </summary>
    private static ToolApprovalAllowlistProposal BuildProposal(
        ToolApprovalTicketRequest request,
        ClassificationVerdict verdict,
        ToolApprovalRuleEffect effect)
        => new()
        {
            ToolId = request.ToolId,
            Command = request.CommandName,
            ArgumentsJson = request.RequestedArgumentsJson,
            Reason = FormattableString.Invariant(
                $"effect={(effect == ToolApprovalRuleEffect.Allow ? "allow" : "deny")} outcome={verdict.Outcome} confidence={FormatConfidence(verdict)} classifierId={verdict.ClassifierId} model={verdict.ClassifierModel ?? "n/a"}"),
        };

    private static string? FirstWorkingDirectory(ToolApprovalTicketRequest request)
        => request.OperationSteps
            .Select(step => step.WorkingDirectory)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>出题单要点的有界短摘要（不含任何对话历史）。</summary>
    private static string BuildBoundedTrajectory(ToolApprovalTicketRequest request)
    {
        var digest = FormattableString.Invariant(
            $"kind={request.TicketKind} requestedScope={request.RequestedScope} steps={request.OperationSteps.Count}");
        if (request.RequestedDuration is { } duration)
            digest += FormattableString.Invariant($" requestedDuration={duration}");
        if (!string.IsNullOrWhiteSpace(request.RiskNotes))
            digest += $" riskNotes={Truncate(request.RiskNotes, 160)}";
        return Truncate(digest, MaxTrajectoryChars);
    }

    /// <summary>按规范键顺序拼接逐分类可信度摘要；缺失为 n/a。</summary>
    private static string FormatConfidence(ClassificationVerdict verdict)
    {
        var confidence = verdict.PerOutcomeConfidence;
        if (confidence is not { Count: > 0 })
            return "n/a";

        return string.Join(
            ",",
            confidence
                .OrderBy(pair => ConfidenceKeyOrder(pair.Key))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => FormattableString.Invariant($"{pair.Key}={pair.Value:0.###}")));
    }

    private static int ConfidenceKeyOrder(string key)
    {
        for (var index = 0; index < CanonicalConfidenceKeys.Length; index++)
        {
            if (string.Equals(key, CanonicalConfidenceKeys[index], StringComparison.Ordinal))
                return index;
        }

        return CanonicalConfidenceKeys.Length;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value ?? string.Empty;

        return value[..Math.Max(0, maxLength - 1)] + "…";
    }
}
