using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingCode.Tools;
using PuddingRuntime.Thresholds;

namespace PuddingRuntime.Classification;

/// <summary>
/// 规则唯一键（方案 v2 §14.12.1）：<c>(workspace_id, tool_id, subject, working_directory, shell)</c>。
/// <para>
/// 命中必须<b>逐字节相等</b>：<c>*</c> / <c>?</c> / 正则 / 路径前缀 / <c>startsWith</c> 语义一律不参与匹配。
/// <see cref="Subject"/> 对命令类工具是规范化后的精确命令串，对非命令类工具是
/// <c>args_sha256:&lt;hex&gt;</c>（规范化 JSON 的 SHA-256）。
/// </para>
/// </summary>
public readonly record struct RuleKey(
    string WorkspaceId,
    string ToolId,
    string Subject,
    string? WorkingDirectory,
    string? Shell);

/// <summary>§14.12.5 尽窄校验的 6 条拒绝项编号（None 表示全部通过）。</summary>
public enum NarrownessViolation
{
    None = 0,

    /// <summary>§14.12.5-1：命令含 shell 元字符（<c>| &amp; ; &gt; &lt; ` $ ( ) { }</c> 或换行）。</summary>
    ShellMetacharacter = 1,

    /// <summary>§14.12.5-2：含通配符或正则元字符。</summary>
    WildcardOrRegex = 2,

    /// <summary>§14.12.5-3：命令是裸解释器或其 <c>-c</c>/<c>-e</c>/<c>-Command</c> 内联脚本形式。</summary>
    BareInterpreter = 3,

    /// <summary>§14.12.5-4：参数含不可静态求值的占位符（运行时变量决定的路径/端口等）。</summary>
    NonStaticPlaceholder = 4,

    /// <summary>§14.12.5-5：目标路径越出 workspace 根。</summary>
    PathOutsideWorkspace = 5,

    /// <summary>§14.12.5-6：不可逆操作且无已备份证据（S1 上下文契约无 BackupTaken 字段，按未备份处理）。</summary>
    IrreversibleWithoutBackup = 6,
}

/// <summary>尽窄校验结果：报告 6 条拒绝项中<b>具体命中哪一条</b>（按编号顺序返回首个命中）。</summary>
public sealed record NarrownessCheck
{
    /// <summary>全部通过（无任何命中）。</summary>
    public static NarrownessCheck Ok { get; } = new();

    public bool Passed => Violation == NarrownessViolation.None;

    public NarrownessViolation Violation { get; init; }

    /// <summary>命中的具体证据（哪个字符 / 哪条路径 / 哪个占位符）。</summary>
    public string? Detail { get; init; }

    public static NarrownessCheck Fail(NarrownessViolation violation, string detail) => new()
    {
        Violation = violation,
        Detail = detail,
    };
}

/// <summary>规则策展结果。<see cref="Degraded"/> 仅表示「按尽窄/异常契约退化为单次裁决」；单次类裁决（AllowOnce/DenyOnce）<b>不是</b>降级。</summary>
public sealed record CuratedRuleOutcome
{
    /// <summary>是否落库（创建或幂等更新）了一条规则。</summary>
    public bool Applied { get; init; }

    /// <summary>本次写入的规则 id；未落库为 null。</summary>
    public string? RuleId { get; init; }

    /// <summary>是否被降级（尽窄命中或 store 异常，fail-closed）。</summary>
    public bool Degraded { get; init; }

    /// <summary>降级的具体原因（尽窄条目编号 + 证据，或 store 错误摘要）。</summary>
    public string? DegradeReason { get; init; }

    /// <summary>同键存在相反 Effect 的冲突（§14.12.4，生效 Deny 由消费端执行）。</summary>
    public bool ConflictDetected { get; init; }

    /// <summary>与之冲突的<b>既有</b>规则 id。</summary>
    public string? ConflictingRuleId { get; init; }
}

/// <summary>
/// 规则策展器（方案 v2 §14.12，切片 S2）：把分类器「永久类」裁决翻译成确定性、可审计、可撤销的规则数据。
/// <para>
/// 设计约束（§14.12.9）：纯函数核心（键规范化 / 冲突 / 尽窄）+ store IO 分离；时间经注入
/// <see cref="TimeProvider"/> 获取，无静态可变状态；store 异常不冒泡（fail-closed，绝不伪造成功）。
/// 幂等读改写放在策展器内（既有 <see cref="IToolApprovalAllowlistStore"/> 只有 Save/Get/List，不改 store 契约）。
/// 本切片<b>不</b>接线 DI、不修改 <c>InMemoryToolApprovalService.CheckAsync</c> 既有判定顺序（§14.12.8）。
/// </para>
/// </summary>
public sealed class ClassificationRuleCurator
{
    /// <summary>§14.12.7：置信度低于该阈值（含缺失，缺失按保守处理）时建议 30 天有效期。</summary>
    public const double SuggestedExpiryConfidenceThreshold = 0.95;

    private static readonly TimeSpan SuggestedExpiry = TimeSpan.FromDays(30);

    /// <summary>§14.12.5-3 列举的裸解释器（严格按规格，不自行扩充）。</summary>
    private static readonly HashSet<string> BareInterpreterNames = new(StringComparer.OrdinalIgnoreCase)
    { "bash", "sh", "pwsh", "powershell", "cmd", "python", "node" };

    /// <summary>§14.12.5-3 列举的内联脚本旗标（严格按规格，不自行扩充）。</summary>
    private static readonly HashSet<string> InlineScriptFlags = new(StringComparer.OrdinalIgnoreCase)
    { "-c", "-e", "-command" };

    /// <summary>§14.12.5-1 列举的 shell 元字符 + 换行。</summary>
    private static readonly char[] ShellMetacharacters =
        ['|', '&', ';', '>', '<', '`', '$', '(', ')', '{', '}', '\n', '\r'];

    /// <summary>
    /// §14.12.5-2 的通配/正则元字符子集。<c>.</c> 与 <c>+</c> 刻意排除：它们是普通路径、扩展名与
    /// 编译器名（g++/c++）的常规字符，纳入会把几乎所有正常构建命令误判为「泛化」，
    /// 使尽窄校验失去可用性；真正被 GuardFall 实测利用的重写类（glob、字符类、锚点）全部保留。
    /// </summary>
    private static readonly char[] WildcardOrRegexMetacharacters =
        ['*', '?', '[', ']', '^'];

    private static readonly Regex CmdEnvVariablePattern =
        new("%[A-Za-z0-9_:.-]+%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IToolApprovalAllowlistStore _allowlistStore;
    private readonly IToolApprovalAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 生效的规则沉淀置信度门槛：构造期经判据端口解析一次（未注入端口 ⇒ 退回既有常量
    /// <see cref="SuggestedExpiryConfidenceThreshold"/>）。缺失 / 低于门槛 ⇒ 建议 30 天有效期的
    /// 保守语义不变，只改取值来源。
    /// </summary>
    private readonly double _suggestedExpiryConfidenceThreshold;

    public ClassificationRuleCurator(
        IToolApprovalAllowlistStore allowlistStore,
        IToolApprovalAuditStore auditStore,
        TimeProvider? timeProvider = null,
        IAcceptanceThresholdPolicyProvider? thresholdPolicyProvider = null)
    {
        _allowlistStore = allowlistStore ?? throw new ArgumentNullException(nameof(allowlistStore));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // S2a：门槛「取值来源」改为经判据端口解析；端口未注入 ⇒ 退回既有常量（行为逐位不变）。
        _suggestedExpiryConfidenceThreshold = thresholdPolicyProvider
            ?.Resolve(AcceptanceThresholdPolicyIds.SuggestedExpiryConfidence)
            .RequiredConfidence
            ?? SuggestedExpiryConfidenceThreshold;
    }

    /// <summary>
    /// §14.12.1 subject 规范化：命令 ⇒ 去首尾空白 → 内部连续空白折叠为单空格 → 路径分隔符统一 <c>/</c>；
    /// 非命令 ⇒ <c>args_sha256:</c> + SHA-256(规范化 JSON：键排序、忽略空白)。
    /// </summary>
    public static string NormalizeSubject(string? command, string? argumentsJson)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            return CollapseWhitespace(command.Trim()).Replace('\\', '/');
        }

        var canonical = CanonicalizeJson(argumentsJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "args_sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>由分类上下文构造规则唯一键（工作目录做分隔符/尾分隔符同一性规范化；shell 仅去首尾空白）。</summary>
    public static RuleKey BuildKey(ToolCallClassificationContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RuleKey(
            WorkspaceId: ctx.WorkspaceId,
            ToolId: ctx.ToolId,
            Subject: NormalizeSubject(ctx.CommandName, ctx.ArgumentsJson),
            WorkingDirectory: NormalizeDirectory(ctx.WorkingDirectory),
            Shell: NormalizeShell(ctx.Shell));
    }

    /// <summary>
    /// §14.12.5 尽窄校验（纯函数）：按编号顺序返回 6 条拒绝项中首个命中项；全部通过返回 <see cref="NarrownessCheck.Ok"/>。
    /// 第 1–3 条仅针对命令类输入（§14.12.5 逐条均以「命令」为主语）；第 4–6 条作用于上下文。
    /// </summary>
    public static NarrownessCheck CheckNarrowness(ToolCallClassificationContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var isCommandTool = !string.IsNullOrWhiteSpace(ctx.CommandName);

        if (isCommandTool)
        {
            // 元字符/通配检查使用「原始」命令文本：规范化会把换行折叠成空格，从而漏掉 §14.12.5-1 的换行条款。
            var rawCommand = ctx.CommandName!;

            var meta = rawCommand.IndexOfAny(ShellMetacharacters);
            if (meta >= 0)
            {
                return NarrownessCheck.Fail(
                    NarrownessViolation.ShellMetacharacter,
                    $"命令含 shell 元字符 '{EscapedChar(rawCommand[meta])}'（§14.12.5-1）。");
            }

            var wild = rawCommand.IndexOfAny(WildcardOrRegexMetacharacters);
            if (wild >= 0)
            {
                return NarrownessCheck.Fail(
                    NarrownessViolation.WildcardOrRegex,
                    $"命令含通配/正则元字符 '{rawCommand[wild]}'（§14.12.5-2）。");
            }

            var bare = DetectBareInterpreter(NormalizeSubject(rawCommand, ctx.ArgumentsJson));
            if (bare is not null)
            {
                return NarrownessCheck.Fail(
                    NarrownessViolation.BareInterpreter,
                    $"命令是裸解释器或其内联脚本形式：{bare}（§14.12.5-3）。");
            }
        }

        var placeholder = DetectNonStaticPlaceholder(ctx.ArgumentsJson);
        if (placeholder is not null)
        {
            return NarrownessCheck.Fail(
                NarrownessViolation.NonStaticPlaceholder,
                $"参数含不可静态求值的占位符：{placeholder}（§14.12.5-4）。");
        }

        var outside = DetectOutsideWorkspacePath(ctx);
        if (outside is not null)
        {
            return NarrownessCheck.Fail(
                NarrownessViolation.PathOutsideWorkspace,
                $"目标路径越出 workspace 根：{outside}（§14.12.5-5）。");
        }

        if (ctx.IsIrreversibleOperation)
        {
            return NarrownessCheck.Fail(
                NarrownessViolation.IrreversibleWithoutBackup,
                "操作标记为不可逆且上下文未携带已备份证据（S1 契约无 BackupTaken 字段，按未备份处理）（§14.12.5-6）。");
        }

        return NarrownessCheck.Ok;
    }

    /// <summary>
    /// 策展一次分类器裁决：仅 <see cref="ClassificationOutcome.AllowPermanent"/> / <see cref="ClassificationOutcome.DenyPermanent"/>
    /// 落规则；单次类与 <see cref="ClassificationOutcome.Unknown"/> 不沉淀（<see cref="CuratedRuleOutcome.Degraded"/>=false）。
    /// 尽窄命中 ⇒ 降级且绝不落规则；store 异常 ⇒ fail-closed 不冒泡。
    /// </summary>
    /// <param name="source">
    /// 规则来源——它决定的是**权威等级**（§14.12.2 权威矩阵），不是记账字段：
    /// <list type="bullet">
    /// <item><see cref="ToolApprovalAllowlistRuleSource.Classifier"/>（默认）：分类器自身产出的永久裁决 ⇒ **终局**，
    /// 命中后管线复用该裁决、不再回调仲裁分类器（§14.13.5 防循环）。</item>
    /// <item><see cref="ToolApprovalAllowlistRuleSource.Human"/> / <see cref="ToolApprovalAllowlistRuleSource.BuiltIn"/>：
    /// 人工/内置写入的规则 ⇒ **候选**——命中 deny 时管线**仍须给分类器一次覆盖机会**（§11.3：分类器拥有最终
    /// 否决/放行权，包括覆盖 deny）。若在这里误标为 Classifier，就把一条人工黑名单升级成了"连分类器也无权
    /// 覆盖的终局封锁"，与既定优先级相反。</item>
    /// </list>
    /// 同时 <c>SourceClassifierId</c> 仅对分类器来源非空（人工规则不得冒充分类器产物）。
    /// </param>
    public async Task<CuratedRuleOutcome> CurateAsync(
        ClassificationVerdict verdict,
        ToolCallClassificationContext ctx,
        CancellationToken ct = default,
        ToolApprovalAllowlistRuleSource source = ToolApprovalAllowlistRuleSource.Classifier)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(ctx);

        if (verdict.Outcome is not (ClassificationOutcome.AllowPermanent or ClassificationOutcome.DenyPermanent))
        {
            // 单次类不是降级；Unknown 无裁决可沉淀。一律不动 store。
            return new CuratedRuleOutcome { Applied = false };
        }

        var narrowness = CheckNarrowness(ctx);
        if (!narrowness.Passed)
        {
            return new CuratedRuleOutcome
            {
                Applied = false,
                Degraded = true,
                DegradeReason = FormatDegradeReason(narrowness),
            };
        }

        var effect = verdict.Outcome == ClassificationOutcome.AllowPermanent
            ? ToolApprovalRuleEffect.Allow
            : ToolApprovalRuleEffect.Deny;

        try
        {
            var now = _timeProvider.GetUtcNow();
            var key = BuildKey(ctx);
            var isCommandTool = !string.IsNullOrWhiteSpace(ctx.CommandName);

            var sameKeyRules = (await _allowlistStore.ListAsync(ct).ConfigureAwait(false))
                .Where(rule => MatchesKey(rule, key))
                .ToList();

            // §14.12.3：幂等只在 Enabled 行上进行；Disabled 行视为已退役（append-only 精神，不复活）。
            var sameEffect = sameKeyRules.FirstOrDefault(r =>
                r.Effect == effect && r.Status == ToolApprovalAllowlistRuleStatus.Enabled);
            var opposite = sameKeyRules.FirstOrDefault(r =>
                r.Effect != effect && r.Status == ToolApprovalAllowlistRuleStatus.Enabled);

            ToolApprovalAllowlistRule saved;
            bool isUpdate = sameEffect is not null;
            if (isUpdate)
            {
                // 同键同 Effect ⇒ 更新既有行（刷新溯源 + HitCount++），不新增。
                saved = sameEffect! with
                {
                    Reason = verdict.Reason,
                    SourceClassifierId = source == ToolApprovalAllowlistRuleSource.Classifier ? verdict.ClassifierId : null,
                    ClassifierModel = verdict.ClassifierModel,
                    OutcomeConfidence = ResolveConfidence(verdict),
                    LastSeenAtUtc = now,
                    HitCount = sameEffect.HitCount + 1,
                    ExpiresAtUtc = SuggestExpiryAtUtc(verdict, now),
                };
            }
            else
            {
                saved = new ToolApprovalAllowlistRule
                {
                    RuleId = NewId(),
                    WorkspaceId = key.WorkspaceId,
                    ToolId = key.ToolId,
                    Command = isCommandTool ? key.Subject : null,
                    ArgumentsJson = isCommandTool ? null : ctx.ArgumentsJson,
                    // 来源由调用方显式给出（§14.12.2 权威矩阵）：分类器永久裁决 ⇒ 终局；
                    // 人工/内置写入 ⇒ 候选（命中 deny 仍须给分类器一次覆盖机会）。
                    // 「分类器永久权威」由 SourceClassifierId 非空表达；审计角色（AuditAgent）正在下线，不再使用。
                    Source = source,
                    Status = ToolApprovalAllowlistRuleStatus.Enabled,
                    Effect = effect,
                    Reason = verdict.Reason,
                    ApprovedByAgentInstanceId = ctx.AgentInstanceId,
                    ApprovedByUserId = ctx.UserId,
                    CreatedBySessionId = ctx.SessionId,
                    SourceClassifierId = source == ToolApprovalAllowlistRuleSource.Classifier ? verdict.ClassifierId : null,
                    ClassifierModel = verdict.ClassifierModel,
                    OutcomeConfidence = ResolveConfidence(verdict),
                    FirstSeenAtUtc = now,
                    LastSeenAtUtc = now,
                    ExpiresAtUtc = SuggestExpiryAtUtc(verdict, now),
                    WorkingDirectory = key.WorkingDirectory,
                    Shell = key.Shell,
                    CreatedAtUtc = now,
                    HitCount = 1,
                };
            }

            await _allowlistStore.SaveAsync(saved, ct).ConfigureAwait(false);

            // 审计：创建/更新（deny 更新复用 AllowlistRuleUpdated：枚举无 DenylistRuleUpdated，且不得新增成员）。
            var auditType = effect == ToolApprovalRuleEffect.Allow
                ? (isUpdate ? ToolApprovalAuditEventType.AllowlistRuleUpdated : ToolApprovalAuditEventType.AllowlistRuleCreated)
                : (isUpdate ? ToolApprovalAuditEventType.AllowlistRuleUpdated : ToolApprovalAuditEventType.DenylistRuleCreated);
            await SaveCuratorAuditAsync(auditType, ctx, saved, verdict, now, ct).ConfigureAwait(false);

            if (opposite is not null)
            {
                // §14.12.4：同键存在相反 Effect ⇒ 生效 Deny（由消费端执行），此处落冲突审计并携带信号。
                await SaveConflictAuditAsync(ctx, saved, opposite, now, ct).ConfigureAwait(false);
                return new CuratedRuleOutcome
                {
                    Applied = true,
                    RuleId = saved.RuleId,
                    ConflictDetected = true,
                    ConflictingRuleId = opposite.RuleId,
                };
            }

            return new CuratedRuleOutcome { Applied = true, RuleId = saved.RuleId };
        }
        catch (OperationCanceledException)
        {
            // 取消是调用方意图，不属于「store 异常吞掉」契约，照常传播。
            throw;
        }
        catch (Exception ex)
        {
            // 行为要求 6：store 异常不得冒泡，fail-closed，绝不伪造成功。
            return new CuratedRuleOutcome
            {
                Applied = false,
                Degraded = true,
                DegradeReason = $"store_error: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// §14.12.7 撤销语义：<c>rule_op=disable</c> ⇒ <c>Status=Disabled</c>（不硬删除，保留完整审计链）。
    /// 已禁用规则重复禁用为幂等成功；规则不存在 ⇒ 不落库不伪造成功。
    /// </summary>
    public async Task<CuratedRuleOutcome> DisableRuleAsync(string ruleId, string reason, CancellationToken ct = default)
    {
        try
        {
            var rule = await _allowlistStore.GetAsync(ruleId, ct).ConfigureAwait(false);
            if (rule is null)
            {
                return new CuratedRuleOutcome { Applied = false, Degraded = true, DegradeReason = "rule_not_found" };
            }

            if (rule.Status == ToolApprovalAllowlistRuleStatus.Disabled)
            {
                return new CuratedRuleOutcome { Applied = true, RuleId = rule.RuleId };
            }

            var now = _timeProvider.GetUtcNow();
            var disabled = rule with
            {
                Status = ToolApprovalAllowlistRuleStatus.Disabled,
                DisabledAtUtc = now,
                UpdatedAtUtc = now,
            };
            await _allowlistStore.SaveAsync(disabled, ct).ConfigureAwait(false);

            await _auditStore.SaveAsync(new ToolApprovalAuditEvent
            {
                EventId = NewId(),
                EventType = disabled.Effect == ToolApprovalRuleEffect.Deny
                    ? ToolApprovalAuditEventType.DenylistRuleDisabled
                    : ToolApprovalAuditEventType.AllowlistRuleDisabled,
                WorkspaceId = disabled.WorkspaceId,
                ToolId = disabled.ToolId,
                Command = disabled.Command,
                ArgumentsJson = disabled.ArgumentsJson,
                AllowlistRuleId = disabled.RuleId,
                AllowlistRuleHitCount = disabled.HitCount,
                Effect = disabled.Effect,
                Source = disabled.Source,
                Reason = reason,
                CreatedAtUtc = now,
            }, ct).ConfigureAwait(false);

            return new CuratedRuleOutcome { Applied = true, RuleId = disabled.RuleId };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CuratedRuleOutcome
            {
                Applied = false,
                Degraded = true,
                DegradeReason = $"store_error: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // —— 键匹配与规范化辅助（纯函数） ——

    /// <summary>存储行是否与键逐字节相等（行侧 subject / wd / shell 先做同一规范化再比较；命中不含任何通配语义）。</summary>
    private static bool MatchesKey(ToolApprovalAllowlistRule rule, RuleKey key)
    {
        if (!string.Equals(rule.WorkspaceId, key.WorkspaceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(rule.ToolId, key.ToolId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(NormalizeSubject(rule.Command, rule.ArgumentsJson), key.Subject, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(NormalizeDirectory(rule.WorkingDirectory), key.WorkingDirectory, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(NormalizeShell(rule.Shell), key.Shell, StringComparison.Ordinal);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                previousWasSpace = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>规范化 JSON：键按 Ordinal 排序、去掉全部空白；非 JSON 参数退化为 trim 后原串（确定性优先）。</summary>
    private static string CanonicalizeJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "";
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is null)
            {
                return argumentsJson.Trim();
            }

            var sb = new StringBuilder(argumentsJson.Length);
            WriteCanonicalJson(node, sb);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }
    }

    private static void WriteCanonicalJson(JsonNode node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                var firstProperty = true;
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        sb.Append(',');
                    }

                    firstProperty = false;
                    sb.Append(JsonValue.Create(property.Key)?.ToJsonString());
                    sb.Append(':');
                    if (property.Value is null)
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        WriteCanonicalJson(property.Value, sb);
                    }
                }

                sb.Append('}');
                break;

            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    if (array[i] is null)
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        WriteCanonicalJson(array[i]!, sb);
                    }
                }

                sb.Append(']');
                break;

            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 同一性规范化（非泛化）：分隔符统一 + 去尾分隔符，同一目录得到同一键分量。
        return path.Trim().Replace('\\', '/').TrimEnd('/');
    }

    private static string? NormalizeShell(string? shell) =>
        string.IsNullOrWhiteSpace(shell) ? null : shell.Trim();

    /// <summary>§14.12.5-3：首 token（剥路径与 .exe 后缀）是解释器，且无参数或紧跟内联脚本旗标。</summary>
    private static string? DetectBareInterpreter(string normalizedSubject)
    {
        var tokens = normalizedSubject.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var executable = tokens[0];
        var lastSlash = executable.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            executable = executable[(lastSlash + 1)..];
        }

        if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            executable = executable[..^4];
        }

        if (!BareInterpreterNames.Contains(executable))
        {
            return null;
        }

        return tokens.Length == 1 || InlineScriptFlags.Contains(tokens[1])
            ? tokens[0]
            : null;
    }

    /// <summary>§14.12.5-4：扫描 JSON 字符串值中的 ${…}、$(…)、{{…}}、$env:…、%VAR% 占位符。</summary>
    private static string? DetectNonStaticPlaceholder(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        List<string> values;
        try
        {
            var node = JsonNode.Parse(argumentsJson);
            values = [];
            if (node is not null)
            {
                CollectStringValues(node, values);
            }
        }
        catch (JsonException)
        {
            values = [argumentsJson];
        }

        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (value.Contains("${", StringComparison.Ordinal))
            {
                return "${...}";
            }

            if (value.Contains("$(", StringComparison.Ordinal))
            {
                return "$(...)";
            }

            if (value.Contains("{{", StringComparison.Ordinal))
            {
                return "{{...}}";
            }

            if (value.Contains("$env:", StringComparison.OrdinalIgnoreCase))
            {
                return "$env:...";
            }

            if (CmdEnvVariablePattern.IsMatch(value))
            {
                return "%VAR%";
            }
        }

        return null;
    }

    private static void CollectStringValues(JsonNode node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        CollectStringValues(property.Value, into);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        CollectStringValues(item, into);
                    }
                }

                break;

            default:
                if (node is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    into.Add(text);
                }

                break;
        }
    }

    /// <summary>§14.12.5-5：目标资源中出现越出 workspace 根的路径样字符串（绝对路径前缀比较 / 相对 .. 越根）。</summary>
    private static string? DetectOutsideWorkspacePath(ToolCallClassificationContext ctx)
    {
        if (ctx.TargetResources.Count == 0)
        {
            return null;
        }

        var root = NormalizeDirectory(ctx.WorkingDirectory);
        if (root is null)
        {
            // 无 workspace 根可比对时本条事实不成立（不虚构违规）。
            return null;
        }

        foreach (var raw in ctx.TargetResources)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var candidate = raw.Trim().Trim('"');
            if (!LooksLikePath(candidate) || IsUnderRoot(candidate, root))
            {
                continue;
            }

            return raw;
        }

        return null;
    }

    private static bool LooksLikePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith("\\\\") ||
        value.Contains('/') ||
        value.Contains('\\') ||
        (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');

    /// <summary>绝对路径按根前缀段比较（OrdinalIgnoreCase，Windows 优先）；相对路径解析 .. 段不得越过根。</summary>
    private static bool IsUnderRoot(string candidate, string root)
    {
        var candidateSegments = SplitSegments(candidate);
        var rootSegments = SplitSegments(root);
        var isAbsolute = candidate.StartsWith('/') || candidate.StartsWith("\\\\")
            || (candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':');

        if (!isAbsolute)
        {
            var depth = 0;
            foreach (var segment in candidateSegments)
            {
                if (segment == "..")
                {
                    depth--;
                    if (depth < 0)
                    {
                        return false;
                    }
                }
                else if (segment is not ("." or ""))
                {
                    depth++;
                }
            }

            return true;
        }

        if (candidateSegments.Count < rootSegments.Count)
        {
            return false;
        }

        for (var i = 0; i < rootSegments.Count; i++)
        {
            if (!string.Equals(candidateSegments[i], rootSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> SplitSegments(string path) =>
        [.. path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)];

    // —— 溯源与审计 ——

    private static double? ResolveConfidence(ClassificationVerdict verdict)
    {
        if (verdict.PerOutcomeConfidence is not { Count: > 0 })
        {
            return null;
        }

        var outcomeKey = verdict.Outcome switch
        {
            ClassificationOutcome.AllowPermanent => "allow_permanent",
            ClassificationOutcome.DenyPermanent => "deny_permanent",
            ClassificationOutcome.AllowOnce => "allow_once",
            ClassificationOutcome.DenyOnce => "deny_once",
            _ => "unknown",
        };

        return verdict.PerOutcomeConfidence.TryGetValue(outcomeKey, out var confidence) ? confidence : null;
    }

    /// <summary>§14.12.7：置信度 &lt; 门槛（含缺失，保守处理）⇒ 建议 30 天有效期；启动清理不在本切片。
    /// 门槛经判据端口解析（<see cref="IAcceptanceThresholdPolicyProvider"/>），未注入时等于既有常量。</summary>
    private DateTimeOffset? SuggestExpiryAtUtc(ClassificationVerdict verdict, DateTimeOffset now)
    {
        var confidence = ResolveConfidence(verdict);
        return confidence is double value && value >= _suggestedExpiryConfidenceThreshold
            ? null
            : now + SuggestedExpiry;
    }

    private static string FormatDegradeReason(NarrownessCheck check) => check.Violation switch
    {
        NarrownessViolation.ShellMetacharacter => $"narrow_1_shell_metacharacter: {check.Detail}",
        NarrownessViolation.WildcardOrRegex => $"narrow_2_wildcard_or_regex: {check.Detail}",
        NarrownessViolation.BareInterpreter => $"narrow_3_bare_interpreter: {check.Detail}",
        NarrownessViolation.NonStaticPlaceholder => $"narrow_4_non_static_placeholder: {check.Detail}",
        NarrownessViolation.PathOutsideWorkspace => $"narrow_5_path_outside_workspace: {check.Detail}",
        NarrownessViolation.IrreversibleWithoutBackup => $"narrow_6_irreversible_without_backup: {check.Detail}",
        _ => "narrow_unknown",
    };

    private static string EscapedChar(char ch) => ch switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => ch.ToString(),
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task SaveCuratorAuditAsync(
        ToolApprovalAuditEventType eventType,
        ToolCallClassificationContext ctx,
        ToolApprovalAllowlistRule rule,
        ClassificationVerdict verdict,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _auditStore.SaveAsync(new ToolApprovalAuditEvent
        {
            EventId = NewId(),
            EventType = eventType,
            WorkspaceId = ctx.WorkspaceId,
            SessionId = ctx.SessionId,
            AgentInstanceId = ctx.AgentInstanceId,
            UserId = ctx.UserId,
            ToolId = rule.ToolId,
            Command = rule.Command,
            ArgumentsJson = rule.ArgumentsJson,
            AllowlistRuleId = rule.RuleId,
            AllowlistRuleHitCount = rule.HitCount,
            Effect = rule.Effect,
            Source = rule.Source,
            ReviewerModel = verdict.ClassifierModel,
            Reason = verdict.Reason,
            CreatedAtUtc = now,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>§14.12.4 冲突审计：事件主体描述既有冲突规则，Reason 携带两侧 rule_id 与来源（既有事件形状无第二组 id 字段）。</summary>
    private async Task SaveConflictAuditAsync(
        ToolCallClassificationContext ctx,
        ToolApprovalAllowlistRule newRule,
        ToolApprovalAllowlistRule existingRule,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _auditStore.SaveAsync(new ToolApprovalAuditEvent
        {
            EventId = NewId(),
            EventType = ToolApprovalAuditEventType.RuleConflictDetected,
            WorkspaceId = ctx.WorkspaceId,
            SessionId = ctx.SessionId,
            AgentInstanceId = ctx.AgentInstanceId,
            UserId = ctx.UserId,
            ToolId = existingRule.ToolId,
            Command = existingRule.Command,
            ArgumentsJson = existingRule.ArgumentsJson,
            AllowlistRuleId = existingRule.RuleId,
            AllowlistRuleHitCount = existingRule.HitCount,
            Effect = ToolApprovalRuleEffect.Deny,
            Source = existingRule.Source,
            Reason = $"same-key conflict: new rule {newRule.RuleId}({newRule.Source}/{newRule.Effect}) "
                + $"vs existing rule {existingRule.RuleId}({existingRule.Source}/{existingRule.Effect}); "
                + "deny wins per §14.12.4",
            CreatedAtUtc = now,
        }, ct).ConfigureAwait(false);
    }
}
