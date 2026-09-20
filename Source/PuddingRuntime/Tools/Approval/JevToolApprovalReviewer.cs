using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Jev 决策模型驱动的工具审批评审器（<see cref="IToolApprovalReviewer"/> 的可切换实现，
/// 与 <see cref="LlmToolApprovalReviewer"/> 互为替代；开关 <c>ToolApproval:Reviewer=jev</c>）。
/// <para>方案：Docs/Features/Jev自动审批与白名单自学习改造方案.md §3.1。</para>
/// <para>
/// 安全不变量（由 JevToolApprovalReviewerTests 逐条锁定）：
/// I1 确定性 deny 前置——命中 <see cref="ToolApprovalCommandFirewall"/> 危险模式时绝不调用 Jev；
/// I2 Jev 不可用/超时/缺答案/坏答案一律 DeferredDependency（fail-closed，不折叠为 Approved/NeedHuman）；
/// I3 白名单提案只允许精确匹配——命令含 shell 元字符/通配符/换行时绝不产出提案；
/// I4 白名单校准概率低于阈值或 decision≠approve 时不产出提案；
/// I5 提案 Reason 携带 provenance（Jev 模型 + 概率 + 风险等级），经工单落库可撤销；
/// I6 白名单命中不进入评审器（由 InMemoryToolApprovalService.CheckAsync 的 allowlist 快速通道保证）。
/// </para>
/// </summary>
public sealed class JevToolApprovalReviewer : IToolApprovalReviewer
{
    /// <summary>稳定协议原因码（不得重命名，外部按码分支）。</summary>
    public const string ReasonCodeApproved = "jev_approved";

    /// <summary>稳定协议原因码：Jev 判 deny。</summary>
    public const string ReasonCodeDenied = "jev_denied";

    /// <summary>稳定协议原因码：Jev 判需要人工。</summary>
    public const string ReasonCodeNeedHuman = "jev_need_human";

    /// <summary>稳定协议原因码：Jev 依赖不可用/超时/异常（fail-closed 等待依赖恢复）。</summary>
    public const string ReasonCodeUnavailable = "jev_unavailable";

    /// <summary>稳定协议原因码：确定性 deny 规则优先，跳过 Jev。</summary>
    public const string ReasonCodeSkippedDenyRule = "jev_skipped_deny_rule";

    /// <summary>稳定协议原因码：Jev 响应缺答案/不可识别（fail-closed）。</summary>
    public const string ReasonCodeInvalidResponse = "jev_invalid_response";

    private const string DecisionQuestion = "decision";
    private const string RiskQuestion = "risk";
    private const string ScopeQuestion = "scope";
    private const string AllowlistQuestion = "allowlist";

    private readonly IJevDecisionService? _jevDecisionService;
    private readonly ILogger<JevToolApprovalReviewer>? _logger;
    private readonly ToolApprovalJevOptions _options;

    /// <summary>
    /// 所有依赖均为可选：DI 未注册 Jev 时仍可构造，评审退化为
    /// <see cref="ToolApprovalDecision.DeferredDependency"/>（fail-closed，不抛异常）。
    /// </summary>
    public JevToolApprovalReviewer(
        IJevDecisionService? jevDecisionService = null,
        ILogger<JevToolApprovalReviewer>? logger = null,
        IConfiguration? configuration = null)
    {
        _jevDecisionService = jevDecisionService;
        _logger = logger;
        _options = configuration?.GetSection(ToolApprovalJevOptions.SectionName).Get<ToolApprovalJevOptions>()
                   ?? new ToolApprovalJevOptions();
    }

    /// <inheritdoc />
    public async Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var normalizedToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId);

        if (!_options.Enabled || _jevDecisionService is null)
        {
            _logger?.LogWarning(
                "[ToolApproval] jev reviewer unavailable enabled={Enabled} jevRegistered={JevRegistered} tool={ToolId} workspace={WorkspaceId} session={SessionId}",
                _options.Enabled,
                _jevDecisionService is not null,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId);
            return Deferred("Jev decision review is unavailable; waiting for the dependency to recover.", ReasonCodeUnavailable);
        }

        // I1：确定性 deny 前置。命中危险模式时既不调用 Jev，也绝不让模型推翻确定性规则。
        var denyRule = ToolApprovalCommandFirewall.Evaluate(normalizedToolId, request.RequestedArgumentsJson);
        if (denyRule is { Allowed: false })
        {
            _logger?.LogWarning(
                "[ToolApproval] jev reviewer skipped by deterministic deny rule rule={RuleId} tool={ToolId} workspace={WorkspaceId} session={SessionId}",
                denyRule.RuleId,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId);
            return new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = denyRule.Message,
                RequiresHumanAuthorization = true,
                MissingRequirements = [denyRule.RuleId],
                RecommendedFix = "Destructive operations are outside automatic approval. Provide backup and rollback evidence, or use explicit human authorization.",
                ReviewerModel = "command-firewall",
                ReasonCode = ReasonCodeSkippedDenyRule,
            };
        }

        var decisionRequest = BuildDecisionRequest(request, identity, descriptor, normalizedToolId, denyRule);

        JevDecisionResult result;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ReviewTimeoutSeconds)));
            result = await _jevDecisionService.DecideAsync(decisionRequest, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 自身 deadline 到期；调用者主动取消不在此列，继续向上抛（与 LLM 评审器语义一致）。
            _logger?.LogWarning(
                "[ToolApproval] jev review deadline exceeded timeoutSeconds={TimeoutSeconds} tool={ToolId} workspace={WorkspaceId} session={SessionId}",
                _options.ReviewTimeoutSeconds,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId);
            return Deferred("Jev decision review exceeded its own deadline.", ReasonCodeUnavailable);
        }
        catch (JevDecisionException ex)
        {
            _logger?.LogWarning(
                ex,
                "[ToolApproval] jev review failed code={Code} tool={ToolId} workspace={WorkspaceId} session={SessionId}",
                ex.Code,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId);
            return Deferred($"Jev decision review failed ({ex.Code}).", ReasonCodeUnavailable);
        }

        return MapResult(result, request, identity, normalizedToolId);
    }

    private ToolApprovalReviewResult MapResult(
        JevDecisionResult result,
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        string normalizedToolId)
    {
        var decision = result.GetAnswer(DecisionQuestion)?.Choice?.Trim().ToLowerInvariant() switch
        {
            "approve" => ToolApprovalDecision.Approved,
            "deny" => ToolApprovalDecision.Denied,
            "need_human" => ToolApprovalDecision.NeedHuman,
            _ => (ToolApprovalDecision?)null,
        };

        if (decision is null)
        {
            // I2：缺 answers / 不可识别的 decision 一律 fail-closed 到依赖等待。
            _logger?.LogWarning(
                "[ToolApproval] jev review returned an invalid decision answer model={Model} tool={ToolId} workspace={WorkspaceId} session={SessionId}",
                result.Model,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId);
            return Deferred("Jev decision review returned a missing or unrecognized decision answer.", ReasonCodeInvalidResponse);
        }

        var risk = result.GetAnswer(RiskQuestion)?.Score;
        var scopeAnswer = result.GetAnswer(ScopeQuestion)?.Choice?.Trim().ToLowerInvariant();
        var scope = scopeAnswer switch
        {
            "once" => ToolApprovalScope.Once,
            "session" => ToolApprovalScope.Session,
            "timed" => ToolApprovalScope.Timed,
            _ => (ToolApprovalScope?)null,
        };
        var allowlistProbability = result.GetAnswer(AllowlistQuestion)?.Noul;

        if (decision == ToolApprovalDecision.Denied)
        {
            return new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = $"Jev decision model denied this tool call (model={result.Model}).",
                RequiresHumanAuthorization = false,
                RecommendedFix = "Reduce the operation risk or provide backup and rollback evidence, then retry request_tool_approval.",
                ReviewerModel = result.Model,
                ReasonCode = ReasonCodeDenied,
            };
        }

        if (decision == ToolApprovalDecision.NeedHuman)
        {
            return new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.NeedHuman,
                DecisionReason = $"Jev decision model requires human authorization for this tool call (model={result.Model}).",
                RequiresHumanAuthorization = true,
                ReviewerModel = result.Model,
                ReasonCode = ReasonCodeNeedHuman,
            };
        }

        // decision == Approved。无人工确认包络（方案 §3.1 映射表第 1 行）：
        // risk<=1 且 scope!=once 且白名单概率达阈值 —— 四者缺一则 RequiresHumanAuthorization=true。
        var allowlistHit = allowlistProbability is { } probability && probability >= _options.AllowlistProbabilityThreshold;
        var lowRiskEnvelope = (risk ?? double.MaxValue) <= 1d
                              && scope is { } grantedScope
                              && grantedScope != ToolApprovalScope.Once
                              && allowlistHit;

        var proposals = allowlistHit && TryBuildExactAllowlistProposal(result, request, risk, out var proposal)
            ? [proposal]
            : Array.Empty<ToolApprovalAllowlistProposal>();

        return new ToolApprovalReviewResult
        {
            Decision = ToolApprovalDecision.Approved,
            DecisionReason = lowRiskEnvelope
                ? $"Jev decision model approved this tool call without human confirmation (model={result.Model}, risk={FormatNumber(risk)}, scope={scopeAnswer})."
                : $"Jev decision model approved this tool call, but human authorization is required (model={result.Model}, risk={FormatNumber(risk)}, scope={scopeAnswer ?? "unknown"}).",
            AllowedScope = scope,
            AllowedDuration = scope == ToolApprovalScope.Timed ? request.RequestedDuration : null,
            RequiresHumanAuthorization = !lowRiskEnvelope,
            AllowlistProposals = proposals,
            ReviewerModel = result.Model,
            ReasonCode = ReasonCodeApproved,
        };
    }

    private bool TryBuildExactAllowlistProposal(
        JevDecisionResult result,
        ToolApprovalTicketRequest request,
        double? risk,
        out ToolApprovalAllowlistProposal proposal)
    {
        proposal = null!;
        var command = ExtractExactCommand(request.RequestedArgumentsJson);

        // I3：白名单只允许精确匹配。无法取到精确 command，或命令含任何 shell
        // 元字符/通配符/换行/反斜杠（命令替换、重定向、glob、$IFS 等重写面），一律不提案。
        if (command is null || ContainsShellMetacharacter(command))
            return false;

        var probability = result.GetAnswer(AllowlistQuestion)!.Noul!.Value;
        proposal = new ToolApprovalAllowlistProposal
        {
            ToolId = request.ToolId,
            Command = command,
            ArgumentsJson = NormalizeArgumentsJson(request.RequestedArgumentsJson),
            Reason = string.Create(
                CultureInfo.InvariantCulture,
                $"jev allowlist proposal: model={result.Model}; allowlistProbability={probability:F3}; threshold={_options.AllowlistProbabilityThreshold:F2}; risk={FormatNumber(risk)}; proposedBy={nameof(JevToolApprovalReviewer)}"),
        };
        return true;
    }

    private JevDecisionRequest BuildDecisionRequest(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        string normalizedToolId,
        ToolApprovalFirewallDecision? denyRule)
    {
        // State 只放决策必需信息（成本按 input token 计）；argumentsJson 截断控制极端输入。
        var state = new JsonObject
        {
            ["toolId"] = normalizedToolId,
            ["category"] = descriptor.Category.ToString(),
            ["permissionLevel"] = descriptor.PermissionLevel.ToString(),
            ["safetyFlags"] = descriptor.Safety.ToString(),
            ["argumentsJson"] = TruncateUtf8(request.RequestedArgumentsJson, _options.StateTruncateBytes),
            ["workingDirectory"] = request.OperationSteps.FirstOrDefault(
                static step => !string.IsNullOrWhiteSpace(step.WorkingDirectory))?.WorkingDirectory,
            ["workspaceId"] = identity.WorkspaceId,
            ["sessionId"] = identity.SessionId,
            ["agentInstanceId"] = identity.AgentInstanceId,
            ["purpose"] = TruncateUtf8(request.Purpose, 512),
            ["requestedScope"] = request.RequestedScope.ToString(),
            ["mayDamageOrDeleteData"] = request.MayDamageOrDeleteData,
            ["isIrreversibleOperation"] = request.IsIrreversibleOperation,
            ["mayExposeSecrets"] = request.MayExposeSecrets,
            ["backupTaken"] = request.BackupTaken,
            ["allowlistRequested"] = request.RequestAllowlistRule,
            ["preCheck"] = denyRule is null ? "gray_zone" : denyRule.RuleId,
        };

        // 一次 round trip、四问并行、共享 state 成本（方案 §3.1 提问表）。
        JevQuestion[] questions =
        [
            new()
            {
                Name = DecisionQuestion,
                Type = JevQuestionType.Choice,
                // Instructions 必须设置：官方 API 对缺 instructions 的 choice 问题直接拒绝
                // （真链路原文：Question "outcome" needs instructions.，2026-09-21 实测于 S3c-2 探针）。
                // 只给 ChoiceCriteria 而不说明“要判断什么”，真链路会 400 ⇒ 回退路径 Reviewer=jev 实际不可用。
                Instructions = "对本次工具调用给出三选一裁决：approve / deny / need_human，必须取 criteria 中的键名之一。",
                ChoiceCriteria = new Dictionary<string, string?>
                {
                    ["approve"] = "Safe enough to run automatically right now.",
                    ["deny"] = "Unsafe or policy-violating; must not run.",
                    ["need_human"] = "Uncertain or consequential; requires a human decision.",
                },
            },
            new()
            {
                Name = RiskQuestion,
                Type = JevQuestionType.Score,
                ScoreCriteria =
                [
                    "0 - Harmless read-only operation",
                    "1 - Local and reversible",
                    "2 - Affects shared state or external systems",
                    "3 - Destructive or irreversible",
                ],
            },
            new()
            {
                Name = ScopeQuestion,
                Type = JevQuestionType.Choice,
                Instructions = "给出批准的作用域：once / session / timed，必须取 criteria 中的键名之一。",
                ChoiceCriteria = new Dictionary<string, string?>
                {
                    ["once"] = "Approve this single invocation only.",
                    ["session"] = "Approve for the rest of this session.",
                    ["timed"] = "Approve for a bounded time window.",
                },
            },
            new()
            {
                Name = AllowlistQuestion,
                Type = JevQuestionType.Noul,
                Instructions = "Judge whether this exact tool call is suitable for the exact-match allowlist. " +
                               "Answer true only when the command can be reproduced byte-for-byte, has no parameter ambiguity, " +
                               "and is safe to auto-approve unchanged in the future.",
                NoulCriteria = new JevBoolCriteria
                {
                    True = "Exact command is reproducible and safe to auto-approve in the future.",
                    False = "Command is ambiguous, parameterized, or unsafe for future auto-approval.",
                },
            },
        ];

        return new JevDecisionRequest
        {
            State = state,
            Questions = questions.Take(Math.Max(1, _options.MaxQuestions)).ToArray(),
        };
    }

    private static ToolApprovalReviewResult Deferred(string reason, string reasonCode)
        => new()
        {
            Decision = ToolApprovalDecision.DeferredDependency,
            DecisionReason = reason,
            RequiresHumanAuthorization = true,
            ReasonCode = reasonCode,
        };

    private static string? ExtractExactCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("command", out var command)
                || command.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = command.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeArgumentsJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            return JsonNode.Parse(argumentsJson)?.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ContainsShellMetacharacter(string command)
    {
        foreach (var ch in command)
        {
            switch (ch)
            {
                case ';':
                case '&':
                case '|':
                case '>':
                case '<':
                case '$':
                case '`':
                case '(':
                case ')':
                case '{':
                case '}':
                case '*':
                case '?':
                case '~':
                case '\\':
                case '\r':
                case '\n':
                    return true;
            }
        }

        return false;
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

    private static string FormatNumber(double? value)
        => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
}
