using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingFullTextIndex.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 基于 <c>session_event_log</c> 的原始会话日志查询服务。
/// <para>
/// 这是证据层服务：返回完整事件坐标和原始 data，不做摘要、不做记忆提纯。
/// 当前实现优先保证 workspace scope 和复用接口，后续可在该服务内部替换为 FTS 或文件索引。
/// </para>
/// </summary>
public sealed class RawSessionLogService : IRawSessionLogService
{
    private const int MaxListLimit = 500;
    private const int MaxMessageLimit = 1_000;
    private const int MaxSearchLimit = 100;
    private const int MaxScanRows = 10_000;
    private static readonly string[] TranscriptFallbackEventTypes = ["delta", "usage", "done"];

    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly IFullTextSearchEngine? _ftsEngine;
    private readonly PuddingDataPaths? _dataPaths;

    public RawSessionLogService(
        IDbContextFactory<PlatformDbContext> dbFactory,
        IFullTextSearchEngine? ftsEngine = null,
        PuddingDataPaths? dataPaths = null)
    {
        _dbFactory = dbFactory;
        _ftsEngine = ftsEngine;
        _dataPaths = dataPaths;
    }

    public async Task<RawSessionLogDayList> ListDaysAsync(
        string workspaceId,
        string? fromDay = null,
        string? toDay = null,
        int limit = 31,
        string? agentInstanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return new RawSessionLogDayList(Array.Empty<RawSessionLogDaySummary>());

        var rows = await LoadWorkspaceRowsAsync(workspaceId, sessionId: null, agentInstanceId, ct);
        var filtered = rows
            .Where(r => IsInDayRange(GetDay(r.RecordedAt), fromDay, toDay))
            .ToList();

        var days = filtered
            .GroupBy(r => GetDay(r.RecordedAt))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderByDescending(g => g.Key)
            .Take(Clamp(limit, 1, MaxListLimit))
            .Select(g => new RawSessionLogDaySummary(
                g.Key,
                g.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).Count(),
                g.Count()))
            .ToList();

        return new RawSessionLogDayList(days);
    }

    public async Task<RawSessionLogSessionList> ListSessionsAsync(
        string workspaceId,
        string day,
        int limit = 100,
        string? agentInstanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(day))
            return new RawSessionLogSessionList(Array.Empty<RawSessionLogSessionSummary>());

        var rows = await LoadWorkspaceRowsAsync(workspaceId, sessionId: null, agentInstanceId, ct);
        var sessions = rows
            .Where(r => string.Equals(GetDay(r.RecordedAt), day, StringComparison.Ordinal))
            .GroupBy(r => r.SessionId)
            .OrderByDescending(g => g.Max(x => x.RecordedAt))
            .Take(Clamp(limit, 1, MaxListLimit))
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.SequenceNum).ToList();
                return new RawSessionLogSessionSummary(
                    g.Key,
                    workspaceId,
                    day,
                    ordered.Count,
                    ordered.First().SequenceNum,
                    ordered.Last().SequenceNum,
                    ordered.First().RecordedAt,
                    ordered.Last().RecordedAt);
            })
            .ToList();

        return new RawSessionLogSessionList(sessions);
    }

    public async Task<RawSessionLogSearchResult> GrepAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.Query))
            return new RawSessionLogSearchResult(Array.Empty<RawSessionLogMatch>(), HasMore: false);

        var limit = Clamp(request.Limit, 1, MaxSearchLimit);
        var rows = await LoadWorkspaceRowsAsync(request.WorkspaceId, request.SessionId, request.AgentInstanceId, ct);
        var filtered = rows
            .Where(r => IsInDayRange(GetDay(r.RecordedAt), request.Day ?? request.FromDay, request.Day ?? request.ToDay))
            .OrderByDescending(r => r.RecordedAt)
            .ThenByDescending(r => r.SequenceNum)
            .ToList();

        var matches = request.Regex
            ? RegexMatches(filtered, request.Query, limit)
            : TextMatches(filtered, request.Query, limit);

        return new RawSessionLogSearchResult(
            matches.Take(limit).ToList(),
            HasMore: matches.Count > limit);
    }

    public async Task<RawSessionLogSearchResult> GrepMessagesAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.Query))
            return new RawSessionLogSearchResult(Array.Empty<RawSessionLogMatch>(), HasMore: false);

        var limit = Clamp(request.Limit, 1, MaxSearchLimit);
        var messages = await LoadMessageTranscriptAsync(
            request.WorkspaceId,
            request.SessionId,
            request.AgentInstanceId,
            before: null,
            limit: MaxScanRows,
            ct);

        var filtered = messages
            .Where(m => IsInDayRange(GetDay(m.CreatedAt), request.Day ?? request.FromDay, request.Day ?? request.ToDay))
            .OrderByDescending(m => m.CreatedAt)
            .ToList();

        var matches = request.Regex
            ? RegexMessageMatches(filtered, request.Query, limit)
            : TextMessageMatches(filtered, request.Query, limit);

        return new RawSessionLogSearchResult(
            matches.Take(limit).ToList(),
            HasMore: matches.Count > limit);
    }

    public async Task<RawSessionLogMessagePage> ReadMessagesAsync(
        string workspaceId,
        string sessionId,
        string? agentInstanceId = null,
        long? before = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return new RawSessionLogMessagePage(Array.Empty<RawSessionLogMessage>(), HasMore: false, NextCursor: null);

        var pageSize = Clamp(limit, 1, MaxMessageLimit);
        var messages = await LoadMessageTranscriptAsync(workspaceId, sessionId, agentInstanceId, before, pageSize + 1, ct);
        var hasMore = messages.Count > pageSize;
        var page = messages
            .Take(pageSize)
            .OrderBy(m => ParseRecordedAtMillis(m.CreatedAt))
            .ToList();

        return new RawSessionLogMessagePage(
            page,
            hasMore,
            hasMore ? page.Min(m => ParseRecordedAtMillis(m.CreatedAt)) : null);
    }

    public async Task<RawSessionLogReadResult> ReadSessionAsync(
        string workspaceId,
        string sessionId,
        long? afterSequence = null,
        int limit = 100,
        string? agentInstanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return new RawSessionLogReadResult(Array.Empty<RawSessionLogEvent>(), HasMore: false, NextSequence: null);

        var pageSize = Clamp(limit, 1, MaxListLimit);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.SessionId == sessionId);

        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(e => e.AgentInstanceId == agentInstanceId);

        if (afterSequence is not null)
            query = query.Where(e => e.SequenceNum > afterSequence.Value);

        var rows = await query
            .OrderBy(e => e.SequenceNum)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        var events = rows
            .Take(pageSize)
            .Select(ToEvent)
            .ToList();

        return new RawSessionLogReadResult(
            events,
            hasMore,
            hasMore ? events.LastOrDefault()?.SequenceNum : null);
    }

    /// <summary>按 ChatMessages.Id 查询单条消息。</summary>
    public async Task<RawSessionLogMessage?> GetMessageByIdAsync(
        string workspaceId,
        long messageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.Id == messageId)
            .FirstOrDefaultAsync(ct);

        return row is not null ? ToMessage(row, workspaceId) : null;
    }

    private async Task<List<RawSessionLogMessage>> LoadMessageTranscriptAsync(
        string workspaceId,
        string? sessionId,
        string? agentInstanceId,
        long? before,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessionIds = await GetWorkspaceSessionIdsAsync(db, workspaceId, sessionId, agentInstanceId, ct);
        if (sessionIds.Count == 0)
            return [];

        var pageSize = Clamp(limit, 1, MaxScanRows);
        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => sessionIds.Contains(m.SessionId));

        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(m => m.AgentInstanceId == agentInstanceId);

        if (before is not null)
            query = query.Where(m => m.CreatedAt < before.Value);

        var materialized = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        if (materialized.Count > 0)
        {
            var workspaceBySession = sessionIds.ToDictionary(id => id, _ => workspaceId, StringComparer.Ordinal);
            return materialized
                .Select(m => ToMessage(m, workspaceBySession[m.SessionId]))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        return await BuildFallbackMessagesFromEventLogAsync(db, workspaceId, sessionId, agentInstanceId, before, pageSize, ct);
    }

    private static async Task<List<string>> GetWorkspaceSessionIdsAsync(
        PlatformDbContext db,
        string workspaceId,
        string? sessionId,
        string? agentInstanceId,
        CancellationToken ct)
    {
        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);

        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(e => e.AgentInstanceId == agentInstanceId);

        return await query
            .Select(e => e.SessionId)
            .Distinct()
            .Take(MaxScanRows)
            .ToListAsync(ct);
    }

    private static async Task<List<RawSessionLogMessage>> BuildFallbackMessagesFromEventLogAsync(
        PlatformDbContext db,
        string workspaceId,
        string sessionId,
        string? agentInstanceId,
        long? before,
        int limit,
        CancellationToken ct)
    {
        var events = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId
                && e.SessionId == sessionId
                && (string.IsNullOrWhiteSpace(agentInstanceId) || e.AgentInstanceId == agentInstanceId)
                && TranscriptFallbackEventTypes.Contains(e.EventType))
            .OrderBy(e => e.SequenceNum)
            .Take(MaxScanRows)
            .ToListAsync(ct);

        var messages = new List<RawSessionLogMessage>();
        var replyBuilder = new StringBuilder();
        string? firstRecordedAt = null;
        long firstSequence = 0;
        long lastSequence = 0;
        string? lastRecordedAt = null;

        foreach (var ev in events)
        {
            firstRecordedAt ??= ev.RecordedAt;
            if (firstSequence == 0) firstSequence = ev.SequenceNum;
            lastSequence = ev.SequenceNum;
            lastRecordedAt = ev.RecordedAt;

            if (ev.EventType == "delta")
            {
                var delta = TryReadStringProperty(ev.Data, "delta");
                if (!string.IsNullOrEmpty(delta))
                    replyBuilder.Append(delta);
                continue;
            }

            if (ev.EventType != "done")
                continue;

            var reply = TryReadStringProperty(ev.Data, "reply");
            var content = !string.IsNullOrWhiteSpace(reply)
                ? reply
                : replyBuilder.ToString();

            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add(new RawSessionLogMessage(
                    $"fallback-{firstSequence}-{ev.SequenceNum}",
                    sessionId,
                    workspaceId,
                    "agent",
                    content,
                    firstRecordedAt ?? ev.RecordedAt,
                    $"session-message:{GetDay(firstRecordedAt ?? ev.RecordedAt)}:{sessionId}:fallback-{firstSequence}-{ev.SequenceNum}"));
            }

            replyBuilder.Clear();
            firstRecordedAt = null;
            firstSequence = 0;
        }

        if (replyBuilder.Length > 0)
        {
            var createdAt = firstRecordedAt ?? lastRecordedAt ?? string.Empty;
            messages.Add(new RawSessionLogMessage(
                $"fallback-{firstSequence}-{lastSequence}",
                sessionId,
                workspaceId,
                "agent",
                replyBuilder.ToString(),
                createdAt,
                $"session-message:{GetDay(createdAt)}:{sessionId}:fallback-{firstSequence}-{lastSequence}"));
        }

        var filtered = before is null
            ? messages
            : messages.Where(m => ParseRecordedAtMillis(m.CreatedAt) < before.Value).ToList();

        return filtered
            .OrderByDescending(m => ParseRecordedAtMillis(m.CreatedAt))
            .Take(limit)
            .ToList();
    }

    private async Task<List<SessionEventLogEntity>> LoadWorkspaceRowsAsync(
        string workspaceId,
        string? sessionId,
        string? agentInstanceId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => e.SessionId == sessionId);

        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(e => e.AgentInstanceId == agentInstanceId);

        return await query
            .OrderByDescending(e => e.RecordedAt)
            .Take(MaxScanRows)
            .ToListAsync(ct);
    }

    private static List<RawSessionLogMatch> TextMatches(
        IReadOnlyList<SessionEventLogEntity> rows,
        string query,
        int limit)
    {
        var matches = new List<RawSessionLogMatch>();
        foreach (var row in rows)
        {
            var haystack = BuildSearchText(row);
            var index = haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            matches.Add(ToMatch(row, BuildSnippet(haystack, index, query.Length)));
            if (matches.Count > limit) break;
        }
        return matches;
    }

    private static List<RawSessionLogMatch> RegexMatches(
        IReadOnlyList<SessionEventLogEntity> rows,
        string pattern,
        int limit)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            return new List<RawSessionLogMatch>();
        }

        var matches = new List<RawSessionLogMatch>();
        foreach (var row in rows)
        {
            var haystack = BuildSearchText(row);
            var match = regex.Match(haystack);
            if (!match.Success) continue;

            matches.Add(ToMatch(row, BuildSnippet(haystack, match.Index, match.Length)));
            if (matches.Count > limit) break;
        }
        return matches;
    }

    private static string BuildSearchText(SessionEventLogEntity row)
        => $"{row.EventType}\n{row.Data}";

    private static RawSessionLogMatch ToMatch(SessionEventLogEntity row, string snippet)
        => new(
            row.SessionId,
            row.WorkspaceId,
            GetDay(row.RecordedAt),
            row.SequenceNum,
            row.EventType,
            row.RecordedAt,
            snippet,
            BuildEvidenceRef(row));

    private static RawSessionLogEvent ToEvent(SessionEventLogEntity row)
        => new(
            row.SessionId,
            row.WorkspaceId,
            GetDay(row.RecordedAt),
            row.SequenceNum,
            row.EventType,
            row.Data,
            row.RecordedAt,
            BuildEvidenceRef(row));

    private static RawSessionLogMessage ToMessage(ChatMessageEntity row, string workspaceId)
        => new(
            row.Id.ToString(),
            row.SessionId,
            workspaceId,
            row.Role,
            row.Content,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).UtcDateTime.ToString("O"),
            $"session-message:{GetDay(DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).UtcDateTime.ToString("O"))}:{row.SessionId}:{row.Id}");

    private static string BuildEvidenceRef(SessionEventLogEntity row)
        => $"session-log:{GetDay(row.RecordedAt)}:{row.SessionId}:{row.SequenceNum}";

    private static string GetDay(string recordedAt)
        => string.IsNullOrWhiteSpace(recordedAt)
            ? string.Empty
            : recordedAt.Length >= 10 ? recordedAt[..10] : recordedAt;

    private static bool IsInDayRange(string day, string? fromDay, string? toDay)
    {
        if (string.IsNullOrWhiteSpace(day)) return false;
        if (!string.IsNullOrWhiteSpace(fromDay) && string.CompareOrdinal(day, fromDay) < 0) return false;
        if (!string.IsNullOrWhiteSpace(toDay) && string.CompareOrdinal(day, toDay) > 0) return false;
        return true;
    }

    private static List<RawSessionLogMatch> TextMessageMatches(
        IReadOnlyList<RawSessionLogMessage> messages,
        string query,
        int limit)
    {
        var matches = new List<RawSessionLogMatch>();
        foreach (var message in messages)
        {
            var index = message.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            matches.Add(ToMessageMatch(message, BuildSnippet(message.Content, index, query.Length)));
            if (matches.Count > limit) break;
        }
        return matches;
    }

    private static List<RawSessionLogMatch> RegexMessageMatches(
        IReadOnlyList<RawSessionLogMessage> messages,
        string pattern,
        int limit)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            return [];
        }

        var matches = new List<RawSessionLogMatch>();
        foreach (var message in messages)
        {
            var match = regex.Match(message.Content);
            if (!match.Success) continue;

            matches.Add(ToMessageMatch(message, BuildSnippet(message.Content, match.Index, match.Length)));
            if (matches.Count > limit) break;
        }
        return matches;
    }

    private static RawSessionLogMatch ToMessageMatch(RawSessionLogMessage message, string snippet)
        => new(
            message.SessionId,
            message.WorkspaceId,
            GetDay(message.CreatedAt),
            ParseMessageSequence(message.MessageId),
            "message",
            message.CreatedAt,
            snippet,
            message.EvidenceRef,
            message.Content);

    private static string BuildSnippet(string text, int matchIndex, int matchLength)
    {
        const int context = 120;
        var start = Math.Max(0, matchIndex - context);
        var end = Math.Min(text.Length, matchIndex + Math.Max(matchLength, 1) + context);
        var snippet = text[start..end].Replace("\r", " ").Replace("\n", " ");
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static long ParseMessageSequence(string messageId)
        => long.TryParse(messageId, out var parsed)
            ? parsed
            : 0;

    private static long ParseRecordedAtMillis(string recordedAt)
        => DateTimeOffset.TryParse(recordedAt, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0;

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    // ── FTS 搜索（Lucene + jieba 分词）──

    /// <summary>
    /// 在 Agent 私有 .md 消息日志中通过 Lucene 全文检索。
    /// 需要 agent_instance_id 定位日志目录。
    /// </summary>
    public async Task<RawSessionLogSearchResult> GrepFtsAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default)
    {
        if (_ftsEngine == null || _dataPaths == null)
            return new RawSessionLogSearchResult([], false);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.Query)
            || string.IsNullOrWhiteSpace(request.AgentInstanceId))
            return new RawSessionLogSearchResult([], false);

        var limit = Clamp(request.Limit, 1, MaxSearchLimit);
        var messageRoot = _dataPaths.AgentInstanceMessageLogsRoot(request.AgentInstanceId);

        if (!Directory.Exists(messageRoot))
            return new RawSessionLogSearchResult([], false);

        // 按需建索引（首次调用 ~几百ms，后续命中缓存）
        if (!_ftsEngine.HasIndex(messageRoot))
        {
            var indexResult = await _ftsEngine.BuildIndexAsync(messageRoot, "*.md", ct);
            if (!indexResult.Success)
                return new RawSessionLogSearchResult([], false);
        }

        // Lucene 全文搜索
        var searchResult = await _ftsEngine.SearchAsync(request.Query, messageRoot, limit, null, null, ct);
        if (!searchResult.Success)
            return new RawSessionLogSearchResult([], false);

        // 转换 Lucene 结果 → RawSessionLogMatch
        var matches = searchResult.Matches
            .Select(m => LuceneMatchToSessionLogMatch(m, request.WorkspaceId, request.AgentInstanceId!, messageRoot))
            .Where(m => IsInDayRange(m.Day, request.Day ?? request.FromDay, request.Day ?? request.ToDay))
            .Take(limit)
            .ToList();

        return new RawSessionLogSearchResult(matches, searchResult.TotalMatches > limit);
    }

    private static RawSessionLogMatch LuceneMatchToSessionLogMatch(
        FullTextSearchMatch match, string workspaceId, string agentId, string root)
    {
        var relativePath = Path.GetRelativePath(root, match.FilePath);
        // 路径结构: {date}/{session}.md  →  提取 date
        var day = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault() ?? string.Empty;
        var sessionId = Path.GetFileNameWithoutExtension(match.FilePath);

        return new RawSessionLogMatch(
            sessionId,
            workspaceId,
            day,
            match.LineNumber,    // SequenceNum 用行号代替（.md 文件无 DB sequence）
            "message",           // EventType 固定为 message
            day,                 // RecordedAt 用日期
            match.LineText,      // Snippet 就是命中行原文
            $"session-log-fts:{day}:{sessionId}:{match.LineNumber}");
    }
}
