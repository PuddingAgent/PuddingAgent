using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Classification;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Jev 决策模型驱动的仲裁分类器（<see cref="IToolCallClassifier"/> 的模型实现，
/// 分类器管线 <see cref="PuddingRuntime.Classification.ToolCallClassifierPipeline"/> 仲裁位的生产实现，切片 S3d）。
/// <para>
/// 请求形态（对齐「背景知识 + 分类问题」两段式）：
/// <see cref="JevDecisionRequest.State"/> 携带有界背景（工作目录 / shell / 工具 id / 参数 JSON /
/// 意图与必要性 / 不可逆与破坏标记 / 目标资源 / 最近轨迹的有界摘要，超长一律截断）；
/// Questions 携带四选一裁决问题（outcome）与四个逐分类校准概率问题
/// （confidence.allow_once / confidence.allow_permanent / confidence.deny_once / confidence.deny_permanent），
/// 一次 round trip、五问并行、共享 state 成本（提问表风格对齐 <see cref="JevToolApprovalReviewer"/> §3.1）。
/// </para>
/// <para>
/// 安全不变量（由 JevToolCallClassifierTests 逐条锁定）：
/// C1 解析失败 / 无结论 ⇒ <see cref="ClassificationOutcome.Unknown"/>（fail-closed，绝不默认 allow/deny）；
/// C2 永久类结论缺失对应逐分类可信度 ⇒ 防御性降级为对应单次类（不允许缺可信度的无门槛永久规则；
///     「低于门槛」的降级由上层管线 §14.13.3 执行，本层只管「缺失」）；
/// C3 异常 / 自身超时 / 空返回 ⇒ Unknown + 稳定原因码，绝不冒泡；
///     外层 <see cref="OperationCanceledException"/> 照常传播；
/// C4 每次 <see cref="ClassifyAsync"/> 只请求 Jev 一次（不重试风暴）；
/// C5 每次返回必带非空 Reason / ReasonCode / ClassifierId；LatencyMs 经注入
///     <see cref="TimeProvider"/> 计量；无静态可变状态。
/// </para>
/// </summary>
public sealed class JevToolCallClassifier : IToolCallClassifier
{
    /// <summary>分类器稳定标识（审计溯源与健康面聚合键）。</summary>
    public const string WellKnownClassifierId = "jev";

    /// <summary>稳定协议原因码：Jev 正常产出四选一裁决（含防御性降级场景）。</summary>
    public const string ReasonCodeVerdict = "classifier.jev.verdict";

    /// <summary>稳定协议原因码：Jev 返回缺失/不可解析（fail-closed，绝不默认 allow/deny）。</summary>
    public const string ReasonCodeUnparsed = "classifier.jev.unparsed";

    /// <summary>稳定协议原因码：Jev 依赖不可用/超时/异常（fail-closed，等待依赖恢复）。</summary>
    public const string ReasonCodeUnavailable = "classifier.jev.unavailable";

    /// <summary>稳定协议原因码：配置禁用（ToolApproval:Jev:Enabled=false，fail-closed）。</summary>
    public const string ReasonCodeDisabled = "classifier.jev.disabled";

    private const string OutcomeQuestion = "outcome";

    private const string ConfidenceQuestionPrefix = "confidence.";

    private const string ConfidenceKeyAllowOnce = "allow_once";
    private const string ConfidenceKeyAllowPermanent = "allow_permanent";
    private const string ConfidenceKeyDenyOnce = "deny_once";
    private const string ConfidenceKeyDenyPermanent = "deny_permanent";

    /// <summary>逐分类可信度规范键（对齐 <see cref="ClassificationVerdict.PerOutcomeConfidence"/> 契约）。</summary>
    private static readonly string[] ConfidenceKeys =
    [
        ConfidenceKeyAllowOnce,
        ConfidenceKeyAllowPermanent,
        ConfidenceKeyDenyOnce,
        ConfidenceKeyDenyPermanent,
    ];

    /// <summary>非轨迹背景字段（意图 / 必要性 / 事实依据条目 / 目标资源条目等）的单字段截断字节预算。</summary>
    private const int ContextFieldTruncateBytes = 512;

    /// <summary>FactBasis / TargetResources / MatchedRuleSummaries 的条数上限（有界摘要）。</summary>
    private const int MaxListItems = 8;

    /// <summary>Reason 中引用不可解析 choice 原文时的显示截断长度（防止超长垃圾塞满审计理由）。</summary>
    private const int MaxChoiceEchoChars = 64;

    private readonly IJevDecisionService _decisionService;
    private readonly ToolApprovalJevOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造 Jev 仲裁分类器。复用 Jev 审批评审器的既有配置类型，不新建第二套 options。</summary>
    /// <param name="decisionService">Jev 决策端口（与 <see cref="JevToolApprovalReviewer"/> 共用同一端口实现）。</param>
    /// <param name="options">既有 Jev 配置（本实现消费 Enabled / StateTruncateBytes / ReviewTimeoutSeconds）。</param>
    /// <param name="timeProvider">时间源；缺省系统时钟（测试注入假钟）。</param>
    public JevToolCallClassifier(
        IJevDecisionService decisionService,
        ToolApprovalJevOptions options,
        TimeProvider? timeProvider = null)
    {
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string ClassifierId => WellKnownClassifierId;

    /// <inheritdoc />
    public async Task<ClassificationVerdict> ClassifyAsync(
        ToolCallClassificationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startTimestamp = _timeProvider.GetTimestamp();

        ClassificationVerdict verdict;
        try
        {
            // 取消检查放在 Jev IO 之前；取消是调用方意图，不属于「异常吞掉」契约（对齐 SystemRuleClassifier）。
            ct.ThrowIfCancellationRequested();

            verdict = _options.Enabled
                ? await ClassifyCoreAsync(context, ct).ConfigureAwait(false)
                : Unknown(
                    "Jev 分类器已被配置禁用（ToolApproval:Jev:Enabled=false），fail-closed 返回未知。",
                    ReasonCodeDisabled);
        }
        catch (OperationCanceledException)
        {
            // 行为契约：外层 OperationCanceledException 照常传播，不得折叠成 Unknown。
            throw;
        }
        catch (Exception ex)
        {
            // fail-closed：任何非取消异常（含 JevDecisionException）不得冒泡，返回 Unknown，绝不伪造结论。
            verdict = Unknown(
                $"Jev 决策调用失败，fail-closed 返回未知（不冒泡）：{Describe(ex)}",
                ReasonCodeUnavailable);
        }

        return verdict with { LatencyMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds };
    }

    private async Task<ClassificationVerdict> ClassifyCoreAsync(
        ToolCallClassificationContext context,
        CancellationToken ct)
    {
        var request = BuildDecisionRequest(context);

        JevDecisionResult result;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ReviewTimeoutSeconds)));
            // C4：每次 ClassifyAsync 对 Jev 端口只发起一次请求（端口内部重试策略不属于本层职责）。
            result = await _decisionService.DecideAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 自身 deadline 到期（调用者主动取消不在此列，向上传播，与 LLM/Jev 评审器语义一致）。
            return Unknown("Jev 决策调用超过自身 deadline，fail-closed 返回未知。", ReasonCodeUnavailable);
        }

        return ParseVerdict(result);
    }

    private ClassificationVerdict ParseVerdict(JevDecisionResult result)
    {
        var rawChoice = result.GetAnswer(OutcomeQuestion)?.Choice;
        var requestedOutcome = ParseOutcome(rawChoice);
        if (requestedOutcome is null)
        {
            // C1：缺答案 / 不可识别 ⇒ Unknown（fail-closed），绝不默认 allow/deny。
            return Unknown(
                $"Jev 返回的裁决缺失或不可识别（choice={Echo(rawChoice)}），fail-closed 返回未知。",
                ReasonCodeUnparsed);
        }

        var confidences = new Dictionary<string, double>(ConfidenceKeys.Length);
        foreach (var key in ConfidenceKeys)
        {
            if (TryGetConfidence(result, key, out var value))
            {
                confidences[key] = value;
            }
        }

        // C2：永久类缺失自身逐分类可信度 ⇒ 防御性降级为对应单次类（绝不让「缺可信度」产出无门槛永久规则）。
        string? downgradeNote = null;
        var effectiveOutcome = requestedOutcome.Value;
        if (effectiveOutcome == ClassificationOutcome.AllowPermanent
            && !confidences.ContainsKey(ConfidenceKeyAllowPermanent))
        {
            effectiveOutcome = ClassificationOutcome.AllowOnce;
            downgradeNote = DowngradeNote(ConfidenceKeyAllowPermanent, ConfidenceKeyAllowOnce);
        }
        else if (effectiveOutcome == ClassificationOutcome.DenyPermanent
                 && !confidences.ContainsKey(ConfidenceKeyDenyPermanent))
        {
            effectiveOutcome = ClassificationOutcome.DenyOnce;
            downgradeNote = DowngradeNote(ConfidenceKeyDenyPermanent, ConfidenceKeyDenyOnce);
        }

        return new ClassificationVerdict
        {
            Outcome = effectiveOutcome,
            Reason = BuildReason(result.Model, effectiveOutcome, confidences, downgradeNote),
            PerOutcomeConfidence = confidences,
            ClassifierId = WellKnownClassifierId,
            ClassifierModel = string.IsNullOrWhiteSpace(result.Model) ? null : result.Model,
            ReasonCode = ReasonCodeVerdict,
        };
    }

    private static string DowngradeNote(string requestedKey, string effectiveKey)
        => $"原始结论 {requestedKey} 因缺失对应逐分类可信度（{ConfidenceQuestionName(requestedKey)} 无有效 0..1 noul 值），"
           + $"防御性降级为 {effectiveKey}——绝不允许缺可信度的无门槛永久规则。";

    private static string BuildReason(
        string? model,
        ClassificationOutcome outcome,
        IReadOnlyDictionary<string, double> confidences,
        string? downgradeNote)
    {
        var confText = string.Join(
            ", ",
            ConfidenceKeys.Select(key => confidences.TryGetValue(key, out var value)
                ? $"{key}={value.ToString("0.000", CultureInfo.InvariantCulture)}"
                : $"{key}=<missing>"));

        var text = $"Jev 仲裁裁决 {outcome.ToString().ToLowerInvariant()}（model={model ?? "unknown"}）：逐分类可信度 {confText}。";
        return downgradeNote is null ? text : text + downgradeNote;
    }

    private static ClassificationOutcome? ParseOutcome(string? choice)
        => choice?.Trim().ToLowerInvariant() switch
        {
            ConfidenceKeyAllowOnce => ClassificationOutcome.AllowOnce,
            ConfidenceKeyAllowPermanent => ClassificationOutcome.AllowPermanent,
            ConfidenceKeyDenyOnce => ClassificationOutcome.DenyOnce,
            ConfidenceKeyDenyPermanent => ClassificationOutcome.DenyPermanent,
            _ => null,
        };

    private static bool TryGetConfidence(JevDecisionResult result, string outcomeKey, out double value)
    {
        value = 0d;
        var noul = result.GetAnswer(ConfidenceQuestionName(outcomeKey))?.Noul;
        if (noul is not { } raw || double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return false;
        }

        value = Math.Clamp(raw, 0d, 1d);
        return true;
    }

    private static string ConfidenceQuestionName(string outcomeKey) => ConfidenceQuestionPrefix + outcomeKey;

    private JevDecisionRequest BuildDecisionRequest(ToolCallClassificationContext context)
    {
        // State 只放裁决必需信息（成本按 input token 计）；参数 / 轨迹等超长输入一律有界截断。
        var state = new JsonObject
        {
            ["toolId"] = context.ToolId,
            ["commandName"] = context.CommandName,
            ["argumentsJson"] = TruncateUtf8(context.ArgumentsJson, _options.StateTruncateBytes),
            ["workingDirectory"] = context.WorkingDirectory,
            ["shell"] = context.Shell,
            ["operationContext"] = TruncateUtf8(context.OperationContext, ContextFieldTruncateBytes),
            ["purpose"] = TruncateUtf8(context.Purpose, ContextFieldTruncateBytes),
            ["necessity"] = TruncateUtf8(context.Necessity, ContextFieldTruncateBytes),
            ["factBasis"] = ToBoundedJsonArray(context.FactBasis),
            ["targetResources"] = ToBoundedJsonArray(context.TargetResources),
            ["matchedRuleSummaries"] = ToBoundedJsonArray(context.MatchedRuleSummaries),
            ["isIrreversibleOperation"] = context.IsIrreversibleOperation,
            ["mayDamageOrDeleteData"] = context.MayDamageOrDeleteData,
            ["workspaceId"] = context.WorkspaceId,
            ["sessionId"] = context.SessionId,
            ["recentTrajectory"] = TruncateUtf8(context.RecentTrajectory, _options.StateTruncateBytes),
        };

        // 一次 round trip、五问并行、共享 state 成本：
        // ① 四选一裁决问题；②–⑤ 四个逐分类校准概率（键对齐 PerOutcomeConfidence 规范键）。
        JevQuestion[] questions =
        [
            new()
            {
                Name = OutcomeQuestion,
                Type = JevQuestionType.Choice,
                // Instructions 必须设置：真链路口径（JevDecisionLiveTests 是唯一被联网验证过的形状）里
                // **每个**问题都带 Instructions。只给 ChoiceCriteria 而不说明“要判断什么”，
                // 真实端点无法推进该问；而离线桩不校验该字段 ⇒ 14 例离线测试结构上漏检
                // （这正是 S3c-2 真链路探针存在的意义）。
                Instructions =
                    "对本次工具调用给出四选一裁决：仅放行本次 / 放行且可沉淀为长期 allow 规则 / " +
                    "仅拒绝本次 / 拒绝且可沉淀为长期 deny 规则；choice 必须取 criteria 中的键名之一。",
                ChoiceCriteria = new Dictionary<string, string?>
                {
                    [ConfidenceKeyAllowOnce] = "仅放行本次调用；不沉淀任何长期规则。",
                    [ConfidenceKeyAllowPermanent] = "放行本次调用，且该裁决可沉淀为长期 allow 规则。",
                    [ConfidenceKeyDenyOnce] = "仅拒绝本次调用；不沉淀任何长期规则。",
                    [ConfidenceKeyDenyPermanent] = "拒绝本次调用，且该裁决可沉淀为长期 deny 规则。",
                },
            },
            ConfidenceQuestion(ConfidenceKeyAllowOnce),
            ConfidenceQuestion(ConfidenceKeyAllowPermanent),
            ConfidenceQuestion(ConfidenceKeyDenyOnce),
            ConfidenceQuestion(ConfidenceKeyDenyPermanent),
        ];

        return new JevDecisionRequest { State = state, Questions = questions };
    }

    private static JevQuestion ConfidenceQuestion(string outcomeKey) => new()
    {
        Name = ConfidenceQuestionName(outcomeKey),
        Type = JevQuestionType.Noul,
        Instructions = $"给出本次工具调用应被归类为 {outcomeKey} 的校准概率（0..1）。四个分类的概率都必须给出数值。",
        NoulCriteria = new JevBoolCriteria
        {
            True = $"本调用符合 {outcomeKey} 的语义。",
            False = $"本调用不符合 {outcomeKey} 的语义。",
        },
    };

    private static JsonArray ToBoundedJsonArray(IReadOnlyList<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items.Take(MaxListItems))
        {
            array.Add((JsonNode?)JsonValue.Create(TruncateUtf8(item, ContextFieldTruncateBytes)));
        }

        return array;
    }

    private static ClassificationVerdict Unknown(string reason, string reasonCode) => new()
    {
        Outcome = ClassificationOutcome.Unknown,
        Reason = reason,
        ReasonCode = reasonCode,
        ClassifierId = WellKnownClassifierId,
        ClassifierModel = null,
        AppliedRuleId = null,
    };

    private static string Describe(Exception ex)
        => ex is JevDecisionException jev
            ? $"JevDecisionException({jev.Code}): {jev.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";

    private static string Echo(string? choice)
    {
        if (string.IsNullOrEmpty(choice))
        {
            return "<missing>";
        }

        var trimmed = choice.Trim();
        return trimmed.Length <= MaxChoiceEchoChars
            ? trimmed
            : trimmed[..MaxChoiceEchoChars] + "…";
    }

    private static string? TruncateUtf8(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var budget = Math.Max(0, maxBytes);
        if (bytes.Length <= budget)
            return value;

        var truncated = new byte[budget];
        Buffer.BlockCopy(bytes, 0, truncated, 0, budget);
        // 截断处可能切断 UTF-8 序列；GetString 以 U+FFFD 替换，不影响决策摘要用途。
        return Encoding.UTF8.GetString(truncated);
    }
}
