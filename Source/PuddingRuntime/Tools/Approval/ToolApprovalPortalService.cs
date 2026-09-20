using System.Globalization;
using System.Text.Json;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 门户动作的出题单投影：<see cref="RequestToolApprovalArgs"/> 既有工单字段 + 门户专有参数的内部载体。
/// 它<b>不是</b>第二套 Agent 表单——Agent 侧仍然只填既有审批单字段（方案 v2 §14.4）。
/// </summary>
public sealed record ToolApprovalPortalRequest
{
    /// <summary>身份四元组（作用域与审计溯源边界；完全访问作用域 = workspace_id + agent_instance_id）。</summary>
    public required ToolApprovalIdentity Identity { get; init; }

    /// <summary>被裁决 / 被加规则的工具 id（rules_list 与 full_access_status/revoke 不消费该值）。</summary>
    public required string ToolId { get; init; }

    public string? CommandName { get; init; }
    public string? Purpose { get; init; }
    public string? Necessity { get; init; }
    public IReadOnlyList<string> FactBasis { get; init; } = [];
    public string? RequestedArgumentsJson { get; init; }
    public IReadOnlyList<string> TargetResources { get; init; } = [];
    public bool IsIrreversibleOperation { get; init; }
    public bool MayDamageOrDeleteData { get; init; }
    public string? OperationContext { get; init; }

    /// <summary>本次执行快照的工作目录（规则键 working_directory 分量 / 分类上下文）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>目标 shell（规则键 shell 分量 / 分类上下文）；既有审批单无此字段，通常为 null。</summary>
    public string? Shell { get; init; }

    // —— rules_update 专用 ——

    /// <summary><c>add</c> / <c>disable</c>。</summary>
    public string? RuleOp { get; init; }

    /// <summary><c>allow</c> / <c>deny</c>（rule_op=add 必填）。</summary>
    public string? RuleEffect { get; init; }

    /// <summary>rule_op=disable 必填。</summary>
    public string? RuleId { get; init; }

    /// <summary>规则操作理由（溯源入库）。</summary>
    public string? AllowlistReason { get; init; }

    // —— full_access_request 专用 ——

    /// <summary>请求 TTL（秒）；缺省 300，&gt;300 由授予服务拒绝（绝不截断）。</summary>
    public int? FullAccessDurationSeconds { get; init; }
}

/// <summary>
/// 审批门户服务（方案 v2 §14，切片 S4）：承载 <c>request_tool_approval</c> 的六个非 submit 动作——
/// <c>classify</c> / <c>rules_list</c> / <c>rules_update</c> / <c>full_access_request</c> /
/// <c>full_access_status</c> / <c>full_access_revoke</c>。
/// <para>
/// 设计约束（§14.2–§14.8）：
/// - 只依赖抽象 <see cref="IToolCallClassifier"/> / <see cref="IAgentFullAccessGrantService"/>
///   与既有审批三件套 store，<b>不依赖任何厂商实现</b>（§14.3 可 grep 验收）；
/// - <c>classify</c>：分类器不可用或返回 Unknown ⇒ <b>deferred</b>（不落规则、不授予完全访问、
///   绝不渲染成 allow/deny）；仅永久类结论经 <see cref="ClassificationRuleCurator"/> 落规则（幂等/冲突
///   deny 胜/尽窄/溯源），单次类不落规则；
/// - <c>rules_*</c>：零网络、本地确定性数据，分类器不可用时照常工作；disable 为软禁用
///   （Status=Disabled 保留审计链，重复禁用幂等）；rule_id 不存在 ⇒ 明确失败，不伪造成功；
/// - <c>full_access_*</c>：必须经分类器裁决，仅放行类结论可授予；分类器不可用 ⇒ 一律拒绝
///   （fail-closed，语义是放宽闸门）；TTL 由服务端计时（注入 <see cref="TimeProvider"/>），
///   &gt;300 秒拒绝而非截断；作用域 = workspace_id + agent_instance_id，不跨 Agent、不跨 workspace；
/// - 审计必落：ClassifierInvoked / ClassifierUnavailable（classify、full_access_request），
///   规则与冲突审计由策展器负责，完全访问五类审计由授予服务负责；
/// - 本类<b>不接线 DI</b>（由 <see cref="RequestToolApprovalTool"/> 惰性组装，切片 S4 边界）；
///   授予服务未注入时按需构造实例级 <see cref="AgentFullAccessGrantService"/>（进程内存活、重启即失效）。
/// </para>
/// </summary>
public sealed class ToolApprovalPortalService
{
    /// <summary>分类结果 wire 值：分类器不可用 / 未产生有效裁决（§14.5 deferred，区别于 deny）。</summary>
    public const string OutcomeDeferred = "deferred";

    /// <summary>
    /// 门户人工规则操作（rules_update add）写入策展器时使用的稳定溯源标识。
    /// 策展器产出的规则统一为 Source=Classifier（§14.12.2 终局权威），
    /// 该 id 让审计与规则溯源能区分「门户人工添加」与「分类器自动沉淀」。
    /// </summary>
    public const string ManualRuleClassifierId = "portal";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IToolApprovalAllowlistStore _allowlistStore;
    private readonly IToolApprovalAuditStore _auditStore;
    private readonly IToolCallClassifier? _classifier;
    private readonly TimeProvider _timeProvider;
    private readonly ClassificationRuleCurator _curator;
    private IAgentFullAccessGrantService? _fullAccessService;

    public ToolApprovalPortalService(
        IToolApprovalAllowlistStore allowlistStore,
        IToolApprovalAuditStore auditStore,
        IToolCallClassifier? classifier = null,
        IAgentFullAccessGrantService? fullAccessGrantService = null,
        TimeProvider? timeProvider = null)
    {
        _allowlistStore = allowlistStore ?? throw new ArgumentNullException(nameof(allowlistStore));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _classifier = classifier;
        _fullAccessService = fullAccessGrantService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _curator = new ClassificationRuleCurator(allowlistStore, auditStore, timeProvider);
    }

    /// <summary>授予服务：优先注入实现；未注入时惰性构造实例级服务（进程内共享状态，重启即失效）。</summary>
    private IAgentFullAccessGrantService FullAccessService =>
        _fullAccessService ??= new AgentFullAccessGrantService(_auditStore, _timeProvider);

    // —— §14.5 classify ——

    /// <summary>请求分类器裁决（四选一 + 逐分类可信度 + 理由），不建工单；仅永久类结论落规则。</summary>
    public async Task<string> ClassifyAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_classifier is null)
        {
            // §14.7：分类器未配置 ⇒ deferred（不是拒绝、不是放行；不落规则）。
            await SavePortalAuditAsync(
                request,
                ToolApprovalAuditEventType.ClassifierUnavailable,
                "portal classify degraded to deferred: classifier_not_configured (no IToolCallClassifier registered).",
                ct).ConfigureAwait(false);
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "classify",
                ["outcome"] = OutcomeDeferred,
                ["deferred"] = true,
                ["reason_code"] = "classifier.not_configured",
                ["reason"] = "No classifier is configured; the planned call was not evaluated. " +
                             "This is a dependency wait, not a denial. Retry later or manage rules instead.",
                ["rule_id"] = null,
                ["rule_created"] = false,
            });
        }

        var context = BuildClassificationContext(request);
        var verdict = await _classifier.ClassifyAsync(context, ct).ConfigureAwait(false);

        if (verdict.Outcome == ClassificationOutcome.Unknown)
        {
            // §14.5/§14.7：Unknown ⇒ deferred。绝不渲染成 allow/deny；不落规则、不授予完全访问。
            await SavePortalAuditAsync(
                request,
                ToolApprovalAuditEventType.ClassifierUnavailable,
                $"portal classify degraded to deferred: {verdict.ReasonCode ?? "unknown"} | {verdict.Reason}",
                ct,
                verdict.ClassifierId).ConfigureAwait(false);
            return Serialize(ClassifyPayload(verdict, OutcomeDeferred, deferred: true));
        }

        await SavePortalAuditAsync(
            request,
            ToolApprovalAuditEventType.ClassifierInvoked,
            $"portal classify: outcome={verdict.Outcome}; classifier_id={verdict.ClassifierId}"
            + $"; model={verdict.ClassifierModel ?? "none"}; latency_ms={FormatMs(verdict.LatencyMs)} | {verdict.Reason}",
            ct,
            verdict.ClassifierId,
            ResolveDecidedConfidence(verdict)).ConfigureAwait(false);

        string? ruleId = null;
        var ruleCreated = false;
        string? degradeReason = null;
        var conflictDetected = false;
        string? conflictingRuleId = null;
        if (verdict.Outcome is ClassificationOutcome.AllowPermanent or ClassificationOutcome.DenyPermanent)
        {
            // 永久类才落规则：幂等 / 冲突 deny 胜 / 尽窄 / 溯源全部由策展器保证（§14.12）。
            var curated = await _curator.CurateAsync(verdict, context, ct).ConfigureAwait(false);
            ruleId = curated.RuleId;
            ruleCreated = curated.Applied;
            degradeReason = curated.DegradeReason;
            conflictDetected = curated.ConflictDetected;
            conflictingRuleId = curated.ConflictingRuleId;
        }

        var payload = ClassifyPayload(verdict, WireOutcome(verdict.Outcome), deferred: false);
        payload["rule_id"] = ruleId;
        payload["rule_created"] = ruleCreated;
        payload["rule_degraded"] = degradeReason is not null;
        payload["degrade_reason"] = degradeReason;
        payload["conflict_detected"] = conflictDetected;
        payload["conflicting_rule_id"] = conflictingRuleId;
        return Serialize(payload);
    }

    // —— §14.1 rules_list / rules_update ——

    /// <summary>列出可见规则（黑名单与白名单都可见）：全局规则 + 本工作区规则；零网络、本地确定性数据。</summary>
    public async Task<string> RulesListAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var all = await _allowlistStore.ListAsync(ct).ConfigureAwait(false);
        var visible = all
            .Where(rule => rule.WorkspaceId is null
                           || string.Equals(rule.WorkspaceId, request.Identity.WorkspaceId, StringComparison.Ordinal))
            .Select(ToRulePayload)
            .ToList();

        return Serialize(new Dictionary<string, object?>
        {
            ["action"] = "rules_list",
            ["count"] = visible.Count,
            ["workspace_id"] = request.Identity.WorkspaceId,
            ["rules"] = visible,
        });
    }

    /// <summary>更新规则：add（经策展器落规则，含尽窄/冲突/溯源）或 disable（软禁用，保留审计链）。</summary>
    public async Task<string> RulesUpdateAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var op = (request.RuleOp ?? string.Empty).Trim();
        if (op.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            return await RuleAddAsync(request, ct).ConfigureAwait(false);
        }

        if (op.Equals("disable", StringComparison.OrdinalIgnoreCase))
        {
            return await RuleDisableAsync(request, ct).ConfigureAwait(false);
        }

        // 工具层已做参数校验；此处为防御性兜底（不伪造成功）。
        return Serialize(ErrorPayload("rules_update", "rule_op must be 'add' or 'disable'."));
    }

    private async Task<string> RuleAddAsync(ToolApprovalPortalRequest request, CancellationToken ct)
    {
        if (!TryParseRuleEffect(request.RuleEffect, out var effect))
        {
            return Serialize(ErrorPayload("rules_update", "rule_effect must be 'allow' or 'deny' when rule_op=add."));
        }

        var reason = FirstNonEmpty(request.AllowlistReason, request.Purpose)
                     ?? "portal manual rule operation (no reason given)";
        var context = BuildClassificationContext(request);

        // 规则操作本身就是一次显式裁决输入：以「永久类」合成结论驱动策展器，
        // 幂等 / 同键冲突 deny 胜 / 尽窄 6 条 / 溯源 / 审计（含 RuleConflictDetected）全部由策展器统一保证。
        // 产出规则 Source=**Human**（§14.12.2：人工/内置写入 ⇒ 候选权威）：命中 deny 时管线仍会给分类器
        // 一次覆盖机会——按既定优先级「分类器拥有最终否决/放行权，包括覆盖 deny」，人工黑名单不得被升级为
        // "连分类器也无权覆盖的终局封锁"；只有分类器自身永久裁决（Source=Classifier）才是终局、命中后不再
        // 回调仲裁（§14.13.5 防循环）。
        var verdict = new ClassificationVerdict
        {
            Outcome = effect == ToolApprovalRuleEffect.Allow
                ? ClassificationOutcome.AllowPermanent
                : ClassificationOutcome.DenyPermanent,
            Reason = $"portal manual rule op (op=add, effect={WireEffect(effect)}): {reason}",
            ClassifierId = ManualRuleClassifierId,
            ClassifierModel = null,
        };

        var curated = await _curator
            .CurateAsync(verdict, context, ct, ToolApprovalAllowlistRuleSource.Human)
            .ConfigureAwait(false);

        var payload = new Dictionary<string, object?>
        {
            ["action"] = "rules_update",
            ["op"] = "add",
            ["effect"] = WireEffect(effect),
            ["applied"] = curated.Applied,
            ["rule_id"] = curated.RuleId,
            ["degraded"] = curated.Degraded,
            ["degrade_reason"] = curated.DegradeReason,
            ["conflict_detected"] = curated.ConflictDetected,
            ["conflicting_rule_id"] = curated.ConflictingRuleId,
            ["note"] = curated.Applied
                ? "Rule stored with classifier authority (Source=Classifier): identical calls take the rule fast path and do not consult the arbiter again. Use rules_update op=disable to retire it."
                : null,
        };
        if (!curated.Applied && !string.IsNullOrEmpty(curated.DegradeReason))
        {
            payload["error"] = curated.DegradeReason;
        }

        return Serialize(payload);
    }

    private async Task<string> RuleDisableAsync(ToolApprovalPortalRequest request, CancellationToken ct)
    {
        var ruleId = (request.RuleId ?? string.Empty).Trim();
        var rule = await _allowlistStore.GetAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null)
        {
            // 明确失败：不存在 ⇒ 不落库、不伪造成功。
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "rules_update",
                ["op"] = "disable",
                ["applied"] = false,
                ["rule_id"] = ruleId,
                ["error"] = "rule_not_found",
            });
        }

        if (!string.Equals(rule.WorkspaceId, request.Identity.WorkspaceId, StringComparison.Ordinal))
        {
            // 作用域隔离：只允许禁用本工作区规则（全局内置规则与其它工作区规则一律拒绝）。
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "rules_update",
                ["op"] = "disable",
                ["applied"] = false,
                ["rule_id"] = ruleId,
                ["error"] = "rule_not_in_this_workspace",
            });
        }

        var reason = FirstNonEmpty(request.AllowlistReason, request.Purpose)
                     ?? "portal manual rule op (op=disable)";
        var curated = await _curator.DisableRuleAsync(ruleId, $"portal manual rule op (op=disable): {reason}", ct)
            .ConfigureAwait(false);

        return Serialize(new Dictionary<string, object?>
        {
            ["action"] = "rules_update",
            ["op"] = "disable",
            ["applied"] = curated.Applied,
            ["rule_id"] = ruleId,
            ["status"] = curated.Applied ? "disabled" : null,
            ["error"] = curated.Applied ? null : (curated.DegradeReason ?? "disable_failed"),
        });
    }

    // —— §14.5/§14.6 full_access_* ——

    /// <summary>申请临时完全访问：必须经分类器裁决；仅放行类结论授予；TTL 服务端计时，&gt;300 秒拒绝（不截断）。</summary>
    public async Task<string> FullAccessRequestAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ClassificationVerdict? verdict = null;
        if (_classifier is not null)
        {
            verdict = await _classifier.ClassifyAsync(BuildClassificationContext(request), ct).ConfigureAwait(false);
            await SavePortalAuditAsync(
                request,
                verdict.Outcome == ClassificationOutcome.Unknown
                    ? ToolApprovalAuditEventType.ClassifierUnavailable
                    : ToolApprovalAuditEventType.ClassifierInvoked,
                $"portal full_access_request: outcome={verdict.Outcome}; classifier_id={verdict.ClassifierId}"
                + $"; reason_code={verdict.ReasonCode ?? "none"} | {verdict.Reason}",
                ct,
                verdict.ClassifierId).ConfigureAwait(false);
        }
        else
        {
            await SavePortalAuditAsync(
                request,
                ToolApprovalAuditEventType.ClassifierUnavailable,
                "portal full_access_request: classifier_not_configured (no IToolCallClassifier registered); "
                + "the grant request below must be denied fail-closed (§14.7).",
                ct).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var requestedSeconds = request.FullAccessDurationSeconds ?? AgentFullAccessGrantService.DefaultTtlSeconds;
        var grant = new AgentFullAccessGrant
        {
            // GrantId 留空由授予服务生成；绝对时刻由服务端重算（客户端时钟不可信，仅时长参与判定）。
            GrantId = string.Empty,
            WorkspaceId = request.Identity.WorkspaceId,
            AgentInstanceId = request.Identity.AgentInstanceId,
            SessionId = request.Identity.SessionId,
            GrantedAtUtc = now,
            ExpiresAtUtc = now + TimeSpan.FromSeconds(requestedSeconds),
            GrantedByClassifierId = verdict?.ClassifierId ?? "unavailable",
            Outcome = verdict?.Outcome ?? ClassificationOutcome.Unknown,
            Reason = verdict?.Reason
                     ?? "classifier unavailable: requesting the service fail-closed denial "
                        + "(full access is a relaxation gate; §14.7).",
        };

        try
        {
            var effective = await FullAccessService.GrantAsync(grant, ct).ConfigureAwait(false);
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "full_access_request",
                ["granted"] = true,
                ["grant_id"] = effective.GrantId,
                ["workspace_id"] = effective.WorkspaceId,
                ["agent_instance_id"] = effective.AgentInstanceId,
                ["granted_at_utc"] = effective.GrantedAtUtc,
                ["expires_at_utc"] = effective.ExpiresAtUtc,
                ["ttl_seconds"] = (effective.ExpiresAtUtc - effective.GrantedAtUtc).TotalSeconds,
                ["outcome"] = WireOutcome(effective.Outcome),
                ["classifier_id"] = effective.GrantedByClassifierId,
                ["reason"] = effective.Reason,
                ["scope_note"] = "Scope = workspace_id + agent_instance_id (no cross-agent, no cross-workspace). " +
                                 "Server-side TTL; expires automatically; not persisted across restarts. " +
                                 "Only the approval/authorization gate is relaxed — role tool whitelists, " +
                                 "sub-agent exposure, capability policy, and sandbox boundaries stay in force.",
            });
        }
        catch (AgentFullAccessGrantRejectedException ex)
        {
            // typed 拒绝：reason_code 区分「超上限被拒」与「其它失败」（§14.5：拒绝，绝不截断）。
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "full_access_request",
                ["granted"] = false,
                ["reason_code"] = ex.ReasonCode,
                ["duration_exceeded"] = string.Equals(
                    ex.ReasonCode,
                    AgentFullAccessGrantService.ReasonDurationExceeded,
                    StringComparison.Ordinal),
                ["reason"] = ex.Message,
            });
        }
    }

    /// <summary>查询当前是否持有生效授予 + 到期时刻（服务端计时；到期在读取时判定并自动失效）。</summary>
    public async Task<string> FullAccessStatusAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var active = await FullAccessService
            .GetActiveAsync(request.Identity.WorkspaceId, request.Identity.AgentInstanceId, ct)
            .ConfigureAwait(false);

        return Serialize(new Dictionary<string, object?>
        {
            ["action"] = "full_access_status",
            ["active"] = active is not null,
            ["grant_id"] = active?.GrantId,
            ["granted_at_utc"] = active?.GrantedAtUtc,
            ["expires_at_utc"] = active?.ExpiresAtUtc,
            ["ttl_remaining_seconds"] = active is null
                ? null
                : (active.ExpiresAtUtc - _timeProvider.GetUtcNow()).TotalSeconds,
            ["outcome"] = active is null ? null : WireOutcome(active.Outcome),
            ["workspace_id"] = request.Identity.WorkspaceId,
            ["agent_instance_id"] = request.Identity.AgentInstanceId,
        });
    }

    /// <summary>撤销本 Agent 当前生效的授予；立即失效；无生效授予时幂等成功（不报错）。</summary>
    public async Task<string> FullAccessRevokeAsync(ToolApprovalPortalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var active = await FullAccessService
            .GetActiveAsync(request.Identity.WorkspaceId, request.Identity.AgentInstanceId, ct)
            .ConfigureAwait(false);
        if (active is null)
        {
            return Serialize(new Dictionary<string, object?>
            {
                ["action"] = "full_access_revoke",
                ["revoked"] = false,
                ["workspace_id"] = request.Identity.WorkspaceId,
                ["agent_instance_id"] = request.Identity.AgentInstanceId,
                ["reason"] = "no_active_grant (idempotent success)",
            });
        }

        var revoked = await FullAccessService.RevokeAsync(active.GrantId, ct).ConfigureAwait(false);
        return Serialize(new Dictionary<string, object?>
        {
            ["action"] = "full_access_revoke",
            ["revoked"] = revoked,
            ["grant_id"] = active.GrantId,
            ["workspace_id"] = request.Identity.WorkspaceId,
            ["agent_instance_id"] = request.Identity.AgentInstanceId,
            ["reason"] = revoked ? "revoked; effective immediately" : "grant already inactive (idempotent success)",
        });
    }

    // —— 内部辅助 ——

    private ToolCallClassificationContext BuildClassificationContext(ToolApprovalPortalRequest request) => new()
    {
        ToolId = request.ToolId,
        CommandName = request.CommandName,
        ArgumentsJson = request.RequestedArgumentsJson,
        WorkingDirectory = request.WorkingDirectory,
        Shell = request.Shell,
        OperationContext = request.OperationContext ?? string.Empty,
        Purpose = request.Purpose ?? string.Empty,
        Necessity = request.Necessity ?? string.Empty,
        FactBasis = request.FactBasis,
        TargetResources = request.TargetResources,
        IsIrreversibleOperation = request.IsIrreversibleOperation,
        MayDamageOrDeleteData = request.MayDamageOrDeleteData,
        WorkspaceId = request.Identity.WorkspaceId,
        SessionId = request.Identity.SessionId,
        AgentInstanceId = request.Identity.AgentInstanceId,
        UserId = request.Identity.UserId,
    };

    private async Task SavePortalAuditAsync(
        ToolApprovalPortalRequest request,
        ToolApprovalAuditEventType eventType,
        string reason,
        CancellationToken ct,
        string? classifierId = null,
        double? classifierConfidence = null)
    {
        await _auditStore.SaveAsync(new ToolApprovalAuditEvent
        {
            EventId = $"portal-{Guid.NewGuid():N}",
            EventType = eventType,
            WorkspaceId = request.Identity.WorkspaceId,
            SessionId = request.Identity.SessionId,
            AgentInstanceId = request.Identity.AgentInstanceId,
            UserId = request.Identity.UserId,
            ToolId = request.ToolId,
            Command = request.CommandName,
            ArgumentsJson = request.RequestedArgumentsJson,
            Reason = reason,
            CreatedAtUtc = _timeProvider.GetUtcNow(),
            ClassifierId = classifierId,
            ClassifierConfidence = classifierConfidence,
        }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> ClassifyPayload(
        ClassificationVerdict verdict,
        string wireOutcome,
        bool deferred)
        => new()
        {
            ["action"] = "classify",
            ["outcome"] = wireOutcome,
            ["deferred"] = deferred,
            ["per_outcome_confidence"] = verdict.PerOutcomeConfidence,
            ["reason"] = verdict.Reason,
            ["reason_code"] = verdict.ReasonCode,
            ["classifier_id"] = verdict.ClassifierId,
            ["classifier_model"] = verdict.ClassifierModel,
            ["latency_ms"] = verdict.LatencyMs,
        };

    private static Dictionary<string, object?> ToRulePayload(ToolApprovalAllowlistRule rule) => new()
    {
        ["rule_id"] = rule.RuleId,
        ["workspace_id"] = rule.WorkspaceId,
        ["tool_id"] = rule.ToolId,
        ["command"] = rule.Command,
        ["arguments_json"] = rule.ArgumentsJson,
        ["effect"] = WireEffect(rule.Effect),
        ["source"] = WireSource(rule.Source),
        ["status"] = rule.Status == ToolApprovalAllowlistRuleStatus.Enabled ? "enabled" : "disabled",
        ["hit_count"] = rule.HitCount,
        ["last_hit_at_utc"] = rule.LastHitAtUtc,
        ["created_at_utc"] = rule.CreatedAtUtc,
        ["updated_at_utc"] = rule.UpdatedAtUtc,
        ["disabled_at_utc"] = rule.DisabledAtUtc,
        ["reason"] = rule.Reason,
        ["working_directory"] = rule.WorkingDirectory,
        ["shell"] = rule.Shell,
        ["approved_by_agent_instance_id"] = rule.ApprovedByAgentInstanceId,
        ["approved_by_user_id"] = rule.ApprovedByUserId,
        ["approval_ticket_id"] = rule.ApprovalTicketId,
        ["source_classifier_id"] = rule.SourceClassifierId,
        ["classifier_model"] = rule.ClassifierModel,
        ["outcome_confidence"] = rule.OutcomeConfidence,
        ["created_by_session_id"] = rule.CreatedBySessionId,
        ["first_seen_at_utc"] = rule.FirstSeenAtUtc,
        ["last_seen_at_utc"] = rule.LastSeenAtUtc,
        ["expires_at_utc"] = rule.ExpiresAtUtc,
    };

    private static Dictionary<string, object?> ErrorPayload(string action, string error) => new()
    {
        ["action"] = action,
        ["applied"] = false,
        ["error"] = error,
    };

    private static string WireOutcome(ClassificationOutcome outcome) => outcome switch
    {
        ClassificationOutcome.AllowOnce => "allow_once",
        ClassificationOutcome.AllowPermanent => "allow_permanent",
        ClassificationOutcome.DenyOnce => "deny_once",
        ClassificationOutcome.DenyPermanent => "deny_permanent",
        _ => OutcomeDeferred,
    };

    private static bool TryParseRuleEffect(string? value, out ToolApprovalRuleEffect effect)
    {
        if (string.Equals(value, "allow", StringComparison.OrdinalIgnoreCase))
        {
            effect = ToolApprovalRuleEffect.Allow;
            return true;
        }

        if (string.Equals(value, "deny", StringComparison.OrdinalIgnoreCase))
        {
            effect = ToolApprovalRuleEffect.Deny;
            return true;
        }

        effect = ToolApprovalRuleEffect.Allow;
        return false;
    }

    private static string WireEffect(ToolApprovalRuleEffect effect)
        => effect == ToolApprovalRuleEffect.Deny ? "deny" : "allow";

    private static string WireSource(ToolApprovalAllowlistRuleSource source) => source switch
    {
        ToolApprovalAllowlistRuleSource.AuditAgent => "audit_agent",
        ToolApprovalAllowlistRuleSource.Human => "human",
        ToolApprovalAllowlistRuleSource.Classifier => "classifier",
        _ => "built_in",
    };

    /// <summary>取「已裁决分类」对应的逐分类可信度（无映射时为 null，仅作审计溯源）。</summary>
    private static double? ResolveDecidedConfidence(ClassificationVerdict verdict)
    {
        if (verdict.PerOutcomeConfidence is null)
        {
            return null;
        }

        return verdict.PerOutcomeConfidence.TryGetValue(WireOutcome(verdict.Outcome), out var confidence)
            ? confidence
            : null;
    }

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first
            : !string.IsNullOrWhiteSpace(second) ? second
            : null;

    private static string FormatMs(double? latencyMs)
        => latencyMs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Serialize(Dictionary<string, object?> payload)
        => JsonSerializer.Serialize(payload, JsonOptions);
}
