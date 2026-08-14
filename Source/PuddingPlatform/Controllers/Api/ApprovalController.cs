using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Chat UI P0#1 — 审批卡片后端。
/// <para>
/// 用户对会话中等待中的工具审批请求（<c>approval.requested</c>）做出决定，
/// 将结果写入 conversation_events（<c>approval.resolved</c>），
/// 并通过既有 ADR-057 SSE 流（SessionEventsController / SessionEventStreamService）
/// 原样透传给前端，无需改动 SSE 帧格式。
/// </para>
/// <para>
/// 端点：<c>POST /api/sessions/{sessionId}/decide</c>，返回 200/400/404/409。
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/sessions")]
public sealed class ApprovalController(
    IConversationEventStore eventStore,
    ISessionStateManager ssm,
    ILogger<ApprovalController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>审批允许的决定值。</summary>
    public static class Decisions
    {
        public const string AllowOnce = "allow_once";
        public const string AlwaysAllow = "always_allow";
        public const string Deny = "deny";

        public static readonly string[] All = [AllowOnce, AlwaysAllow, Deny];
    }

    /// <summary>
    /// 对会话中等待中的审批请求做出决定。
    /// </summary>
    /// <remarks>
    /// 流程：校验请求 → 定位 approval.requested → 过期/重复决定校验 →
    /// 追加 approval.resolved 事件（commit 后由 ICommittedEventSignal 唤醒 SSE 实时推送）。
    /// </remarks>
    [HttpPost("{sessionId}/decide")]
    public async Task<ActionResult> Decide(
        string sessionId,
        [FromBody] DecideApprovalRequest? request,
        CancellationToken ct)
    {
        // ── 1. 请求校验（400 invalid_decision）────────────────────
        if (request is null || string.IsNullOrWhiteSpace(request.ApprovalId))
        {
            return BadRequest(new
            {
                errorCode = "invalid_decision",
                message = "approvalId is required.",
            });
        }

        var decision = request.Decision?.Trim();
        if (decision is null || !Decisions.All.Contains(decision, StringComparer.Ordinal))
        {
            return BadRequest(new
            {
                errorCode = "invalid_decision",
                message = "decision must be one of: allow_once, always_allow, deny.",
            });
        }

        var approvalId = request.ApprovalId.Trim();

        // ── 2. 会话可接受决定性校验（409 conversation_frozen）─────
        if (await IsConversationFrozenAsync(sessionId, ct))
        {
            return Conflict(new
            {
                errorCode = "conversation_frozen",
                message = "This conversation is no longer accepting decisions.",
            });
        }

        // ── 3. 定位待处理的审批请求（404 approval_not_found）──────
        var pending = await FindApprovalEventAsync(
            sessionId, approvalId, ApprovalEventTypes.ApprovalRequested, ct);
        if (pending is null)
        {
            return NotFound(new
            {
                errorCode = "approval_not_found",
                message = $"Approval '{approvalId}' was not found in session '{sessionId}'.",
            });
        }

        // ── 4. 过期校验（409 approval_expired）─────────────────────
        if (TryGetExpiresAt(pending.Payload, out var requestedExpiresAt)
            && DateTimeOffset.TryParse(
                requestedExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            && expires <= DateTimeOffset.UtcNow)
        {
            logger.LogInformation(
                "[Approval] Rejected expired decision session={Session} approval={ApprovalId} expiresAt={ExpiresAt}",
                sessionId, approvalId, requestedExpiresAt);
            return Conflict(new
            {
                errorCode = "approval_expired",
                message = $"Approval '{approvalId}' has expired.",
                expiresAt = requestedExpiresAt,
            });
        }

        // ── 5. 幂等：重复决定（409 approval_already_resolved）──────
        var resolved = await FindApprovalEventAsync(
            sessionId, approvalId, ApprovalEventTypes.ApprovalResolved, ct);
        if (resolved is not null)
        {
            return Conflict(new
            {
                errorCode = "approval_already_resolved",
                message = $"Approval '{approvalId}' has already been resolved.",
                decision = ReadPayloadString(resolved.Payload, "decision"),
                decidedAt = ReadPayloadString(resolved.Payload, "decidedAt"),
            });
        }

        // ── 6. 追加 approval.resolved 事件 ─────────────────────────
        var now = DateTimeOffset.UtcNow;
        var decidedBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "single-user";
        var payload = JsonSerializer.SerializeToElement(new
        {
            approvalId,
            decision,
            reason = request.Reason,
            decidedBy,
            decidedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            expiresAt = (string?)null,
        }, JsonOpts);

        var evt = new NewConversationEvent(
            EventId: $"approval:{approvalId}:resolved:{Guid.NewGuid():N}",
            Type: ApprovalEventTypes.ApprovalResolved,
            SchemaVersion: 1,
            WorkspaceId: pending.WorkspaceId,
            TurnId: pending.TurnId,
            CommandId: pending.CommandId,
            RunId: pending.RunId,
            MessageId: pending.MessageId,
            CorrelationId: approvalId,
            CausationId: pending.EventId,
            ProducerEventId: pending.EventId,
            Payload: payload,
            TraceId: pending.TraceId,
            ProducerComponent: "chat.acceptance");

        try
        {
            var result = await eventStore.AppendAsync(
                sessionId,
                expectedVersion: -1,
                [evt],
                EventWriteCondition.ForRun($"approval:{approvalId}", 0),
                ct);

            logger.LogInformation(
                "[Approval] Resolved session={Session} approval={ApprovalId} decision={Decision} seq={Seq}",
                sessionId, approvalId, decision, result.LastSequence);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[Approval] Failed to persist approval.resolved session={Session} approval={ApprovalId}",
                sessionId, approvalId);
            return Problem(
                title: "Approval decision failed",
                detail: "写入审批结果失败，请稍后重试。");
        }

        // ── 7. 200 响应 ────────────────────────────────────────────
        // approved 为兼容契约字段；status 为前端 SessionApprovalDecisionResult 声明字段。
        var approved = decision != Decisions.Deny;
        return Ok(new
        {
            approved,
            status = approved ? "approved" : "denied",
            approvalId,
            decision,
            expiresAt = (string?)null,
        });
    }

    private async Task<bool> IsConversationFrozenAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var state = await ssm.GetSessionStateAsync(sessionId, ct);
            return state is SessionState.Destroyed
                or SessionState.Terminated
                or SessionState.Faulted;
        }
        catch (Exception ex)
        {
            // 未知会话/未初始化状态 —— 不据此拒绝，交给 approval 查找决定 404。
            logger.LogDebug(ex, "[Approval] Session state unresolved session={Session}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// 在会话事件日志中按类型与 approvalId 查找最近的审批事件（反向读取，取最新一条）。
    /// </summary>
    private async Task<ConversationEvent?> FindApprovalEventAsync(
        string sessionId,
        string approvalId,
        string type,
        CancellationToken ct)
    {
        try
        {
            var page = await eventStore.ReadByTypePrefixBackwardAsync(
                sessionId,
                "approval.",
                long.MaxValue,
                limit: 200,
                ct);

            foreach (var evt in page.Events)
            {
                if (!string.Equals(evt.Type, type, StringComparison.Ordinal))
                    continue;
                var id = ReadPayloadString(evt.Payload, "approvalId");
                if (string.Equals(id, approvalId, StringComparison.Ordinal))
                    return evt;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Approval] Failed to read approval events session={Session} type={Type}",
                sessionId, type);
        }

        return null;
    }

    private static bool TryGetExpiresAt(JsonElement payload, out string? expiresAt)
    {
        expiresAt = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("expiresAt", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        expiresAt = property.GetString();
        return !string.IsNullOrWhiteSpace(expiresAt);
    }

    private static string? ReadPayloadString(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>
/// POST /api/sessions/{sessionId}/decide 请求体。
/// </summary>
public sealed record DecideApprovalRequest(
    string? ApprovalId,
    string? Decision,
    string? Reason);
