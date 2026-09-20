using System.Collections.Concurrent;
using System.Globalization;
using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingRuntime.Classification;

/// <summary>
/// 临时「完全访问模式」授予被拒绝（方案 v2 §14.5 / §14.7）。
/// <para>
/// 这不是服务故障，而是<b>确定性 fail-closed 业务裁决</b>的 typed 传递通道：
/// TTL 超限、裁决结论非放行类、审计依赖不可用等场景统一抛出本异常，
/// 调用方（后续切片的门户工具 / 访问级别接线层）必须显式捕获并向 Agent 回显
/// <see cref="ReasonCode"/>，绝不允许把拒绝静默翻译成任何形式的放行。
/// </para>
/// </summary>
public sealed class AgentFullAccessGrantRejectedException : InvalidOperationException
{
    /// <summary>稳定协议原因码（对齐 <see cref="AgentFullAccessGrantService"/> 的公开常量）。</summary>
    public string ReasonCode { get; }

    public AgentFullAccessGrantRejectedException(string reasonCode, string reason)
        : base(reason)
    {
        ReasonCode = reasonCode;
    }
}

/// <summary>
/// <see cref="IAgentFullAccessGrantService"/> 的内存实现（方案 v2 §14.5–§14.8，切片 S5a）。
/// <para>
/// 行为契约：
/// - TTL 由<b>服务端时钟</b>计时：注入 <see cref="TimeProvider"/>（缺省 <see cref="TimeProvider.System"/>），
///   授予时刻 / 到期判定 / 审计时间戳一律取服务端 now，绝不信任调用方给出的绝对时刻
///   （调用方传入的 GrantedAtUtc / ExpiresAtUtc 仅用于计算请求时长，授予时被服务端重算覆盖）；
/// - 请求时长 ≤ 0 ⇒ 视为「未指定」，按默认 <see cref="DefaultTtlSeconds"/> 处理（而非拒绝）：
///   契约字段为 required，零/负值更可能是占位缺省而非恶意输入，且授予 0 秒（立即过期）的记录毫无意义；
///   请求时长 &gt; <see cref="MaxTtlSeconds"/> ⇒ <b>拒绝授予</b>（typed 异常 + 审计），绝不是静默截断到 300；
/// - 到期在<b>读取时</b>判定（<c>now &gt;= ExpiresAtUtc</c> 即失效，含恰好相等——安全边界优先）；
///   进程重启即失效：全部状态为实例字段，无持久化、无静态可变状态；
/// - 作用域 = <c>workspace_id + agent_instance_id</c> 精确匹配，不跨 Agent、不跨 workspace；
///   同一作用域重复授予时覆盖旧授予（一个 Agent 同时至多一份完全访问）；
/// - 授予前置（fail-closed）：仅 <see cref="ClassificationOutcome.AllowOnce"/> /
///   <see cref="ClassificationOutcome.AllowPermanent"/> 可授予；其余结论
///   （<see cref="ClassificationOutcome.Unknown"/>＝分类器不可用/未提供裁决、
///   <see cref="ClassificationOutcome.DenyOnce"/>、<see cref="ClassificationOutcome.DenyPermanent"/>）一律拒绝；
/// - 审计（<see cref="IToolApprovalAuditStore"/>，必落）：每次请求 ⇒ <c>FullAccessRequested</c>；
///   授予 ⇒ <c>FullAccessGranted</c>；拒绝 ⇒ <c>FullAccessDenied</c>；到期在被观测到时 ⇒
///   <c>FullAccessExpired</c>（内部标记保证同一份授予至多落一次）；撤销 ⇒ <c>FullAccessRevoked</c>（重复撤销幂等）；
/// - 异常处理：<see cref="OperationCanceledException"/> 是调用方意图，照常传播；
///   其余异常不冒泡——统一翻译为 fail-closed 拒绝（<see cref="AgentFullAccessGrantRejectedException"/>），
///   原始异常细节只进拒绝原因文本；
/// - 零网络：唯一外部依赖 = <see cref="IToolApprovalAuditStore"/>；无静态可变状态。
/// </para>
/// </summary>
public sealed class AgentFullAccessGrantService : IAgentFullAccessGrantService
{
    /// <summary>默认 TTL（秒）＝方案 v2 §14.5 的 300 秒。</summary>
    public const int DefaultTtlSeconds = 300;

    /// <summary>TTL 上限（秒）＝方案 v2 §14.9 MaxDurationSeconds 的定值（本切片不接配置解析，由 S5b 一并处理）。</summary>
    public const int MaxTtlSeconds = 300;

    /// <summary>拒绝原因码：请求时长超过上限（拒绝而非截断）。</summary>
    public const string ReasonDurationExceeded = "full_access.duration_exceeded";

    /// <summary>拒绝原因码：分类器裁决非放行类（含 Unknown＝分类器不可用/未提供裁决，fail-closed）。</summary>
    public const string ReasonOutcomeNotAllowed = "full_access.outcome_not_allowed";

    /// <summary>拒绝原因码：审计依赖不可用（审计是本服务的硬要求——无审计不授予）。</summary>
    public const string ReasonAuditStoreError = "full_access.audit_store_error";

    private readonly IToolApprovalAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>作用域键（workspaceId + agentInstanceId）→ 当前授予。实例字段：进程重启即失效。</summary>
    private readonly ConcurrentDictionary<string, AgentFullAccessGrant> _grantsByScope = new(StringComparer.Ordinal);

    /// <summary>授予 id → 作用域键（<see cref="RevokeAsync"/> 的查找索引）。</summary>
    private readonly ConcurrentDictionary<string, string> _scopeKeyByGrantId = new(StringComparer.Ordinal);

    /// <summary>已落 <c>FullAccessExpired</c> 审计的授予 id（at-most-once 幂等标记：先占位后落审计，失败不重试）。</summary>
    private readonly ConcurrentDictionary<string, byte> _expiredAuditedGrantIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 授予 / 撤销 / 到期清理共用的状态锁：保证「活跃表 + 撤销索引 + 幂等标记」的复合变更是原子的，
    /// 杜绝并发撤销误删新授予、并发授予丢失索引等竞态。锁内绝不做任何 IO（审计写入一律在锁外）。
    /// </summary>
    private readonly object _sync = new();

    public AgentFullAccessGrantService(IToolApprovalAuditStore auditStore, TimeProvider? timeProvider = null)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AgentFullAccessGrant?> GetActiveAsync(string workspaceId, string agentInstanceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        ArgumentNullException.ThrowIfNull(agentInstanceId);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var scopeKey = BuildScopeKey(workspaceId, agentInstanceId);
        AgentFullAccessGrant? active = null;
        AgentFullAccessGrant? expiredGrant = null;
        lock (_sync)
        {
            if (_grantsByScope.TryGetValue(scopeKey, out var grant))
            {
                // 服务端时钟读取时判定到期：now >= ExpiresAtUtc 即失效（含恰好相等——安全边界优先）。
                if (grant.ExpiresAtUtc <= now)
                {
                    _grantsByScope.TryRemove(scopeKey, out _);
                    _scopeKeyByGrantId.TryRemove(grant.GrantId, out _);
                    if (_expiredAuditedGrantIds.TryAdd(grant.GrantId, 0))
                    {
                        expiredGrant = grant;
                    }
                }
                else
                {
                    active = grant;
                }
            }
        }

        if (expiredGrant is not null)
        {
            try
            {
                await _auditStore.SaveAsync(Audit(
                    expiredGrant,
                    ToolApprovalAuditEventType.FullAccessExpired,
                    $"ttl 由服务端计时到期（expires_at_utc={FormatTimestamp(expiredGrant.ExpiresAtUtc)}），读取时判定失效"),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 调用方意图，照常传播。
            }
            catch
            {
                // at-most-once：幂等标记已占位，审计失败不重试（同一份授予只落一次）；
                // 失效结论由服务端时钟决定、不受审计故障影响；异常不冒泡。
            }
        }

        return active;
    }

    /// <inheritdoc />
    public async Task<AgentFullAccessGrant> GrantAsync(AgentFullAccessGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();

        // 请求时长只取调用方声明的时间差；绝对时刻由服务端在授予时重算（客户端时钟不可信）。
        var ttl = grant.ExpiresAtUtc - grant.GrantedAtUtc;
        if (ttl <= TimeSpan.Zero)
        {
            // ≤0 视为「未指定」，按默认 300 处理（而非拒绝）：占位缺省比恶意缩短更常见，
            // 且授予 0 秒（立即过期）的记录没有意义。该语义由测试（零/负时长用例）固定。
            ttl = TimeSpan.FromSeconds(DefaultTtlSeconds);
        }

        // ① 每次请求必落 FullAccessRequested（含后续被拒的请求）。审计是硬要求：落不上 ⇒ fail-closed 拒绝。
        try
        {
            await _auditStore.SaveAsync(Audit(
                grant,
                ToolApprovalAuditEventType.FullAccessRequested,
                $"requested_ttl_seconds={FormatSeconds(ttl)}"),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Reject(ReasonAuditStoreError, $"审计写入失败（FullAccessRequested），fail-closed 拒绝授予：{Describe(ex)}");
        }

        // ② 时长上限：超过 ⇒ 拒绝（绝不静默截断到 300）。
        if (ttl > TimeSpan.FromSeconds(MaxTtlSeconds))
        {
            await RejectAsync(grant, ct, ReasonDurationExceeded,
                $"请求 ttl={FormatSeconds(ttl)} 秒超过上限 {MaxTtlSeconds} 秒 ⇒ 拒绝授予（不是截断到 {MaxTtlSeconds}）。");
        }

        // ③ 授予前置（fail-closed）：仅放行类结论可授予；Unknown＝分类器不可用/未提供裁决，同样拒绝。
        if (grant.Outcome is not (ClassificationOutcome.AllowOnce or ClassificationOutcome.AllowPermanent))
        {
            await RejectAsync(grant, ct, ReasonOutcomeNotAllowed,
                $"分类器裁决 {grant.Outcome} 不是放行类结论（仅 AllowOnce/AllowPermanent 可授予；Unknown＝分类器不可用/未提供裁决），fail-closed 拒绝。");
        }

        // ④ 授予：绝对时刻一律由服务端重算（GrantedAtUtc=now，ExpiresAtUtc=now+ttl）。
        var grantId = string.IsNullOrWhiteSpace(grant.GrantId) ? $"fa-{Guid.NewGuid():N}" : grant.GrantId;
        var effective = grant with
        {
            GrantId = grantId,
            GrantedAtUtc = now,
            ExpiresAtUtc = now + ttl,
        };

        var scopeKey = BuildScopeKey(grant.WorkspaceId, grant.AgentInstanceId);
        AgentFullAccessGrant? displaced;
        lock (_sync)
        {
            _grantsByScope.TryGetValue(scopeKey, out displaced);
            _grantsByScope[scopeKey] = effective;
            if (displaced is not null && !string.Equals(displaced.GrantId, grantId, StringComparison.Ordinal))
            {
                // 同一作用域被新授予覆盖 ⇒ 同步清理旧授予的撤销索引，防止旧 grantId 撤销误伤新授予。
                _scopeKeyByGrantId.TryRemove(displaced.GrantId, out _);
            }

            _scopeKeyByGrantId[grantId] = scopeKey;
        }

        try
        {
            await _auditStore.SaveAsync(Audit(
                effective,
                ToolApprovalAuditEventType.FullAccessGranted,
                GrantedReason(effective)),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 授予已生效但调用方取消：不回滚（服务端状态以字典为准，可通过 GetActiveAsync 观测真实状态），取消照常传播。
            throw;
        }
        catch (Exception ex)
        {
            // 审计失败 ⇒ 「不可审计的完全访问」不可存在：回滚本授予（仅当未被并发覆盖），fail-closed 返回未授予。
            lock (_sync)
            {
                if (_grantsByScope.TryGetValue(scopeKey, out var current) && ReferenceEquals(current, effective))
                {
                    if (displaced is not null)
                    {
                        _grantsByScope[scopeKey] = displaced;
                        _scopeKeyByGrantId[displaced.GrantId] = scopeKey;
                    }
                    else
                    {
                        _grantsByScope.TryRemove(scopeKey, out _);
                    }
                }

                _scopeKeyByGrantId.TryRemove(grantId, out _);
            }

            throw Reject(ReasonAuditStoreError, $"审计写入失败（FullAccessGranted），授予已回滚（fail-closed 返回未授予）：{Describe(ex)}");
        }

        return effective;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string grantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grantId);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        AgentFullAccessGrant? revokedGrant = null; // 非 null ⇒ 本次调用实际完成撤销
        AgentFullAccessGrant? expiredGrant = null; // 非 null ⇒ 撤销时发现记录已到期（返回 false，补落一次 Expired）
        lock (_sync)
        {
            if (_scopeKeyByGrantId.TryGetValue(grantId, out var scopeKey)
                && _grantsByScope.TryRemove(scopeKey, out var grant)
                && grant is not null)
            {
                _scopeKeyByGrantId.TryRemove(grantId, out _);
                if (grant.ExpiresAtUtc <= now)
                {
                    // 撤销时记录已到期 ⇒ 本就失效：契约返回 false，并按到期语义落一次 Expired（幂等标记先行）。
                    if (_expiredAuditedGrantIds.TryAdd(grant.GrantId, 0))
                    {
                        expiredGrant = grant;
                    }
                }
                else
                {
                    revokedGrant = grant;
                }
            }
            else
            {
                // 记录不存在 / 已撤销 / 已被到期清理 ⇒ 幂等返回 false：不重复落审计、不报错。
                _scopeKeyByGrantId.TryRemove(grantId, out _);
            }
        }

        if (expiredGrant is not null)
        {
            try
            {
                await _auditStore.SaveAsync(Audit(
                    expiredGrant,
                    ToolApprovalAuditEventType.FullAccessExpired,
                    $"ttl 由服务端计时到期（expires_at_utc={FormatTimestamp(expiredGrant.ExpiresAtUtc)}），撤销请求发现记录已失效"),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // at-most-once：标记已占位，不重试、不冒泡。
            }
        }

        if (revokedGrant is not null)
        {
            try
            {
                await _auditStore.SaveAsync(Audit(
                    revokedGrant,
                    ToolApprovalAuditEventType.FullAccessRevoked,
                    $"revoked_at_utc={FormatTimestamp(now)}"),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 撤销事实已发生（记录已移除，无法也不会回滚）；审计缺失不改判、不报错（契约：撤销不报错）。
            }
        }

        return revokedGrant is not null;
    }

    /// <summary>落 <c>FullAccessDenied</c> 审计后抛出 typed 拒绝。审计落不上也不改判放行（fail-closed 不因故障松动）。</summary>
    private async Task RejectAsync(AgentFullAccessGrant grant, CancellationToken ct, string reasonCode, string reason)
    {
        try
        {
            await _auditStore.SaveAsync(DeniedAudit(grant, reasonCode, reason), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方意图，照常传播。
        }
        catch
        {
            reason += "（注：FullAccessDenied 审计写入亦失败，拒绝结论不变。）";
        }

        throw new AgentFullAccessGrantRejectedException(reasonCode, $"{reasonCode}: {reason}");
    }

    private AgentFullAccessGrantRejectedException Reject(string reasonCode, string reason)
        => new(reasonCode, $"{reasonCode}: {reason}");

    /// <summary>构造审计事件基础字段。分类器模型 / 可信度未随 <see cref="AgentFullAccessGrant"/> 契约传递
    /// （该契约只有 GrantedByClassifierId），经 Reason 透传上游授予理由，UserId 诚实留空（契约无此字段）。</summary>
    private ToolApprovalAuditEvent Audit(AgentFullAccessGrant grant, ToolApprovalAuditEventType eventType, string reason)
        => new()
        {
            EventId = $"faa-{Guid.NewGuid():N}",
            EventType = eventType,
            WorkspaceId = grant.WorkspaceId,
            SessionId = grant.SessionId,
            AgentInstanceId = grant.AgentInstanceId,
            UserId = null,
            Reason = reason,
            CreatedAtUtc = _timeProvider.GetUtcNow(),
        };

    private ToolApprovalAuditEvent DeniedAudit(AgentFullAccessGrant grant, string reasonCode, string reason)
        => Audit(grant, ToolApprovalAuditEventType.FullAccessDenied, $"{reasonCode}: {reason}")
            with { Decision = ToolApprovalDecision.Denied };

    /// <summary>Granted 审计的 Reason：写明 ttl 秒数、过期时刻（UTC）、分类器 id / 结论，并透传上游授予理由
    /// （分类器型号 / 可信度若上游愿意携带，可编码在上游 Reason 中随之入审计）。</summary>
    private static string GrantedReason(AgentFullAccessGrant grant)
        => $"ttl_seconds={FormatSeconds(grant.ExpiresAtUtc - grant.GrantedAtUtc)}; expires_at_utc={FormatTimestamp(grant.ExpiresAtUtc)};"
           + $" classifier_id={grant.GrantedByClassifierId}; outcome={grant.Outcome}"
           + (string.IsNullOrWhiteSpace(grant.Reason) ? "" : $" | {grant.Reason}");

    /// <summary>作用域键：<c>workspace_id + agent_instance_id</c> 精确匹配（不可见分隔符防拼接歧义，Ordinal 比较）。</summary>
    private static string BuildScopeKey(string workspaceId, string agentInstanceId)
        => $"{workspaceId}\u0001{agentInstanceId}";

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string FormatSeconds(TimeSpan ttl) => ttl.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToString("O", CultureInfo.InvariantCulture);
}
