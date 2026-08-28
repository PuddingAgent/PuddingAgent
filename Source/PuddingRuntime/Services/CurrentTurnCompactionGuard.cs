using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// 压缩候选消息的最小投影。与存储实体解耦，保证 <see cref="CurrentTurnCompactionGuard"/>
/// 的判定是纯函数、可脱离数据库单测。
/// </summary>
public sealed record CurrentTurnCompactionGuardMessage(string MessageId, string Role, string? Content);

/// <summary>
/// P0-TXN Phase 1（事故1 不变量）：accepted 的 current user turn 不得落入压缩区间。
///
/// 事故背景：轮末自动压缩把含 <c>[CURRENT USER TURN input_sha256=…]</c> 围栏的当前轮
/// User 消息选入摘要区间，摘要落库后该围栏从活跃历史消失；下一轮
/// <c>AgentExecutionService.EnsureCurrentTurnInputPresent</c> fail-closed 抛出
/// 「Outbound LLM history is missing the accepted current user turn」，Run 报废。
///
/// 判定规则（保守）：
/// ① 被压缩区间内出现任一带围栏的 User 消息；或
/// ② 被压缩区间吞掉的消息中包含按 Sequence 判定的最后一条 User 消息
///    （accepted current turn 的保守等价物——即使围栏缺失或解析失败）。
/// 命中任一条 → 本次压缩必须中止（不写库、不生成摘要，原 history 完整保留）。
/// </summary>
public static class CurrentTurnCompactionGuard
{
    public const string FenceOpeningMarker = "[CURRENT USER TURN input_sha256=";

    /// <summary>内容是否携带 current turn 围栏开头标记。</summary>
    public static bool HasCurrentTurnFence(string? content)
        => !string.IsNullOrEmpty(content)
           && content.Contains(FenceOpeningMarker, StringComparison.Ordinal);

    /// <summary>是否为带围栏的 User 消息（accepted current turn 的存储形态）。</summary>
    public static bool IsCurrentTurnMessage(string? role, string? content)
        => IsUser(role) && HasCurrentTurnFence(content);

    /// <summary>
    /// 判定是否必须中止本次压缩。candidates 为按 Sequence 升序的全部压缩候选，
    /// messagesToCompact 为本次将被摘要吞掉的子集。
    /// </summary>
    public static bool ShouldAbortCompaction(
        IReadOnlyList<CurrentTurnCompactionGuardMessage>? candidates,
        IReadOnlyList<CurrentTurnCompactionGuardMessage>? messagesToCompact)
    {
        if (candidates is not { Count: > 0 } || messagesToCompact is not { Count: > 0 })
            return false;

        var lastUserMessageId = FindLastUserMessageId(candidates);
        if (lastUserMessageId is null)
            return false;

        foreach (var message in messagesToCompact)
        {
            if (IsCurrentTurnMessage(message.Role, message.Content))
                return true;
            if (IsUser(message.Role)
                && string.Equals(message.MessageId, lastUserMessageId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsUser(string? role)
        => string.Equals(role?.Trim(), "user", StringComparison.OrdinalIgnoreCase);

    private static string? FindLastUserMessageId(IReadOnlyList<CurrentTurnCompactionGuardMessage> candidates)
    {
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            if (IsUser(candidates[i].Role))
                return candidates[i].MessageId;
        }

        return null;
    }
}

/// <summary>
/// P0-TXN Phase 1（事故1 补救）：从 DB 原始历史找回 accepted current user turn。
///
/// 「原始历史」= 不过滤 <c>CompactedBy</c>（已被压缩软标记的行仍在库中）、
/// 不做 BuildContextFromDbAsync 的当前 Turn 排除。当前 Turn 消息经 canonical
/// transcript 镜像进 memory DB（Source=chat_transcript，Metadata="{turnId}\n{messageId}"），
/// 压缩只改标记不删行，因此库内始终可找回。
/// </summary>
public static class CurrentTurnDbRecovery
{
    /// <summary>
    /// 按 input_sha256 精确找回 accepted current turn 消息（同时含围栏 opening/closing
    /// 的最后一条 User 行）。找不到、DB 不可用或查询异常时返回 null（调用方继续 fail-closed）。
    /// </summary>
    public static async Task<ChatMessage?> TryFindMessageByInputHashAsync(
        IDbContextFactory<MemoryDbContext>? dbFactory,
        ICompactionChatMessageStore? canonicalMessageStore,
        string sessionId,
        string inputSha256,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (dbFactory is null
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(inputSha256))
        {
            return null;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await TrySynchronizeCanonicalTranscriptAsync(db, canonicalMessageStore, sessionId, logger, ct);

            var expectedOpening = $"[CURRENT USER TURN input_sha256={inputSha256}]";
            var expectedClosing = $"[/CURRENT USER TURN input_sha256={inputSha256}]";
            var entity = await db.Messages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId
                    && m.Role == "user"
                    && m.Content != null
                    && m.Content!.Contains(expectedOpening)
                    && m.Content!.Contains(expectedClosing))
                .OrderByDescending(m => m.Sequence)
                .FirstOrDefaultAsync(ct);
            if (entity is null)
                return null;

            return MapEntityToChatMessage(entity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[CurrentTurnRecovery] Raw DB lookup by input_sha256 failed session={SessionId} hash={InputHash}",
                sessionId,
                inputSha256);
            return null;
        }
    }

    /// <summary>
    /// CaptureCurrentTurn 解析失败时的补救：从 DB 原始历史重查含围栏的最后一条
    /// User 消息，返回自该消息起的原始片段（含其后同轮 tool/assistant 行）。
    /// 优先按 currentTurnId/currentMessageId 的 Metadata 身份匹配，兜底取最后一条围栏行。
    /// 找不到返回空列表（调用方记 warning 并维持原行为）。
    /// </summary>
    public static async Task<List<ChatMessage>> TryRecoverCurrentTurnTailAsync(
        IDbContextFactory<MemoryDbContext>? dbFactory,
        ICompactionChatMessageStore? canonicalMessageStore,
        string sessionId,
        string? currentMessageId,
        string? currentTurnId,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (dbFactory is null || string.IsNullOrWhiteSpace(sessionId))
            return [];

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await TrySynchronizeCanonicalTranscriptAsync(db, canonicalMessageStore, sessionId, logger, ct);

            var fenceRow = await FindFenceRowAsync(db, sessionId, currentMessageId, currentTurnId, ct);
            if (fenceRow is null)
                return [];

            var tail = await db.Messages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId && m.Sequence >= fenceRow.Sequence)
                .OrderBy(m => m.Sequence)
                .ToListAsync(ct);

            return tail.Select(MapEntityToChatMessage).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[CurrentTurnRecovery] Raw DB tail recovery failed session={SessionId} message={MessageId} turn={TurnId}",
                sessionId,
                currentMessageId ?? "<none>",
                currentTurnId ?? "<none>");
            return [];
        }
    }

    private static async Task<MessageEntity?> FindFenceRowAsync(
        MemoryDbContext db,
        string sessionId,
        string? currentMessageId,
        string? currentTurnId,
        CancellationToken ct)
    {
        // 精确身份优先：chat_transcript 镜像行以 Metadata="{turnId}\n{messageId}" 标注归属。
        if (!string.IsNullOrWhiteSpace(currentTurnId))
        {
            var turnPrefix = currentTurnId + "\n";
            var byTurn = await db.Messages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId
                    && m.Role == "user"
                    && m.Content != null
                    && m.Content!.Contains(CurrentTurnCompactionGuard.FenceOpeningMarker)
                    && m.Source == "chat_transcript"
                    && m.Metadata != null
                    && m.Metadata!.StartsWith(turnPrefix))
                .OrderByDescending(m => m.Sequence)
                .FirstOrDefaultAsync(ct);
            if (byTurn is not null)
                return byTurn;
        }

        if (!string.IsNullOrWhiteSpace(currentMessageId))
        {
            var messageSuffix = "\n" + currentMessageId;
            var byMessage = await db.Messages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId
                    && m.Role == "user"
                    && m.Content != null
                    && m.Content!.Contains(CurrentTurnCompactionGuard.FenceOpeningMarker)
                    && m.Source == "chat_transcript"
                    && m.Metadata != null
                    && m.Metadata!.EndsWith(messageSuffix))
                .OrderByDescending(m => m.Sequence)
                .FirstOrDefaultAsync(ct);
            if (byMessage is not null)
                return byMessage;
        }

        // 兜底：含围栏的最后一条 User 行（不要求 chat_transcript 身份标注）。
        return await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId
                && m.Role == "user"
                && m.Content != null
                && m.Content!.Contains(CurrentTurnCompactionGuard.FenceOpeningMarker))
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task TrySynchronizeCanonicalTranscriptAsync(
        MemoryDbContext db,
        ICompactionChatMessageStore? canonicalMessageStore,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        if (canonicalMessageStore is null)
            return;

        try
        {
            await CanonicalChatTranscriptSynchronizer.SynchronizeAsync(
                db,
                canonicalMessageStore,
                sessionId,
                fallbackWorkspaceId: null,
                fallbackAgentId: null,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 同步失败不阻断恢复：库内已有镜像仍然可查。
            logger.LogWarning(
                ex,
                "[CurrentTurnRecovery] Canonical transcript sync failed; falling back to persisted mirror session={SessionId}",
                sessionId);
        }
    }

    private static ChatMessage MapEntityToChatMessage(MessageEntity entity)
        => new(
            ParseChatRole(entity.Role),
            entity.Content ?? string.Empty,
            ContentParts: ContentPartsEnvelope.Decode(entity.AttachmentsJson));

    private static ChatRole ParseChatRole(string role)
    {
        var normalized = role.Trim().ToLowerInvariant();
        return normalized switch
        {
            "assistant" or "agent" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };
    }
}
