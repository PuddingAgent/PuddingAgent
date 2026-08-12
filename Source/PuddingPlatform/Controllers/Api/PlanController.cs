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
/// Chat UI P1#5 — Plan 模式卡片后端。
/// <para>
/// 用户对会话中等待中的计划提案（<c>plan.proposal</c>）做出决定，
/// 将结果写入 conversation_events（<c>plan.finalized</c>），
/// 并通过既有 ADR-057 SSE 流（SessionEventsController / SessionEventStreamService）
/// 原样透传给前端，无需改动 SSE 帧格式。
/// </para>
/// <para>
/// 决定值：approve_and_build（批准并构建）/ manual（逐步执行）/
/// keep_planning（继续完善计划）。其中可选的 steps 数组携带用户在
/// EditablePlanCard 上编辑后的最终步骤（每步可编辑/删除/拖拽排序），
/// 随 plan.finalized 事件持久化，供执行侧消费。
/// </para>
/// <para>
/// 端点：<c>POST /api/sessions/{sessionId}/plan-decide</c>，返回 200/400/404/409。
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/sessions")]
public sealed class PlanController(
    IConversationEventStore eventStore,
    ISessionStateManager ssm,
    ILogger<PlanController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 对会话中等待中的计划提案做出决定。
    /// </summary>
    /// <remarks>
    /// 流程：校验请求 → 定位 plan.proposal → 重复决定校验 →
    /// 追加 plan.finalized 事件（commit 后由 ICommittedEventSignal 唤醒 SSE 实时推送）。
    /// </remarks>
    [HttpPost("{sessionId}/plan-decide")]
    public async Task<ActionResult> Decide(
        string sessionId,
        [FromBody] DecidePlanRequest? request,
        CancellationToken ct)
    {
        // ── 1. 请求校验（400 invalid_decision）────────────────────
        if (request is null || string.IsNullOrWhiteSpace(request.PlanId))
        {
            return BadRequest(new
            {
                errorCode = "invalid_decision",
                message = "planId is required.",
            });
        }

        var decision = request.Decision?.Trim();
        if (decision is null || !PlanDecisions.All.Contains(decision, StringComparer.Ordinal))
        {
            return BadRequest(new
            {
                errorCode = "invalid_decision",
                message = "decision must be one of: approve_and_build, manual, keep_planning.",
            });
        }

        var planId = request.PlanId.Trim();

        // ── 2. 会话可接受决定性校验（409 conversation_frozen）─────
        if (await IsConversationFrozenAsync(sessionId, ct))
        {
            return Conflict(new
            {
                errorCode = "conversation_frozen",
                message = "This conversation is no longer accepting plan decisions.",
            });
        }

        // ── 3. 定位待处理的计划提案（404 plan_not_found）──────────
        var pending = await FindPlanEventAsync(
            sessionId, planId, PlanEventTypes.PlanProposal, ct);
        if (pending is null)
        {
            return NotFound(new
            {
                errorCode = "plan_not_found",
                message = $"Plan '{planId}' was not found in session '{sessionId}'.",
            });
        }

        // ── 4. 幂等：重复决定（409 plan_already_finalized）────────
        var finalized = await FindPlanEventAsync(
            sessionId, planId, PlanEventTypes.PlanFinalized, ct);
        if (finalized is not null)
        {
            return Conflict(new
            {
                errorCode = "plan_already_finalized",
                message = $"Plan '{planId}' has already been finalized.",
                decision = ReadPayloadString(finalized.Payload, "decision"),
                decidedAt = ReadPayloadString(finalized.Payload, "decidedAt"),
            });
        }

        // ── 5. 用户编辑后的步骤（可选；每项需 id + title）──────────
        JsonElement? stepsElement = null;
        if (request.Steps is { Count: > 0 })
        {
            var normalizedSteps = request.Steps
                .Where(step => !string.IsNullOrWhiteSpace(step.Id)
                               && !string.IsNullOrWhiteSpace(step.Title))
                .Select(step => new
                {
                    id = step.Id!.Trim(),
                    title = step.Title!.Trim(),
                    description = string.IsNullOrWhiteSpace(step.Description)
                        ? null
                        : step.Description.Trim(),
                })
                .ToArray();
            if (normalizedSteps.Length > 0)
            {
                stepsElement = JsonSerializer.SerializeToElement(normalizedSteps, JsonOpts);
            }
        }

        // ── 6. 追加 plan.finalized 事件 ─────────────────────────
        var now = DateTimeOffset.UtcNow;
        var decidedBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "single-user";
        var payload = JsonSerializer.SerializeToElement(new
        {
            planId,
            decision,
            steps = stepsElement,
            decidedBy,
            decidedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        }, JsonOpts);

        var evt = new NewConversationEvent(
            EventId: $"plan:{planId}:finalized:{Guid.NewGuid():N}",
            Type: PlanEventTypes.PlanFinalized,
            SchemaVersion: 1,
            WorkspaceId: pending.WorkspaceId,
            TurnId: pending.TurnId,
            CommandId: pending.CommandId,
            RunId: pending.RunId,
            MessageId: pending.MessageId,
            CorrelationId: planId,
            CausationId: pending.EventId,
            ProducerEventId: pending.EventId,
            Payload: payload);

        try
        {
            var result = await eventStore.AppendAsync(
                sessionId,
                expectedVersion: -1,
                [evt],
                EventWriteCondition.ForRun($"plan:{planId}", 0),
                ct);

            logger.LogInformation(
                "[Plan] Finalized session={Session} plan={PlanId} decision={Decision} seq={Seq}",
                sessionId, planId, decision, result.LastSequence);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[Plan] Failed to persist plan.finalized session={Session} plan={PlanId}",
                sessionId, planId);
            return Problem(
                title: "Plan decision failed",
                detail: "写入计划决定结果失败，请稍后重试。");
        }

        // ── 7. 200 响应 ────────────────────────────────────────────
        return Ok(new
        {
            status = "finalized",
            planId,
            decision,
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
            // 未知会话/未初始化状态 —— 不据此拒绝，交给 plan 查找决定 404。
            logger.LogDebug(ex, "[Plan] Session state unresolved session={Session}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// 在会话事件日志中按类型与 planId 查找最近的计划事件（反向读取，取最新一条）。
    /// </summary>
    private async Task<ConversationEvent?> FindPlanEventAsync(
        string sessionId,
        string planId,
        string type,
        CancellationToken ct)
    {
        try
        {
            var page = await eventStore.ReadByTypePrefixBackwardAsync(
                sessionId,
                "plan.",
                long.MaxValue,
                limit: 200,
                ct);

            foreach (var evt in page.Events)
            {
                if (!string.Equals(evt.Type, type, StringComparison.Ordinal))
                    continue;
                var id = ReadPayloadString(evt.Payload, "planId");
                if (string.Equals(id, planId, StringComparison.Ordinal))
                    return evt;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Plan] Failed to read plan events session={Session} type={Type}",
                sessionId, type);
        }

        return null;
    }

    private static string? ReadPayloadString(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>
/// POST /api/sessions/{sessionId}/plan-decide 请求体。
/// </summary>
public sealed record DecidePlanRequest(
    string? PlanId,
    string? Decision,
    IReadOnlyList<DecidePlanStepRequest>? Steps);

/// <summary>
/// plan-decide 请求中用户编辑后的步骤项（id/title 必填，description 可选）。
/// </summary>
public sealed record DecidePlanStepRequest(
    string? Id,
    string? Title,
    string? Description);
