using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine;

/// <summary>
/// 记忆引擎主类——负责：
/// 1. Recall（召回）：从 Session/Workspace 存储中提取相关记忆，拼接为系统提示注入片段。
/// 2. WriteBack（写回）：从 LLM 回复中解析 "REMEMBER:..." 标记，写入相应存储。
/// </summary>
public sealed class MemoryEngine : IMemoryEngine
{
    private const double MillisecondsPerDay = 24d * 60d * 60d * 1000d;

    private static readonly Regex RememberPattern =
        new(@"\bREMEMBER\[(?<tag>[^\]]*)\]:\s*(?<content>.+?)(?=\bREMEMBER\[|$)",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly SessionMemoryStore _sessionStore;
    private readonly WorkspaceMemoryStore _workspaceStore;
    private readonly MemoryBoundaryService _boundary;
    private readonly IDbContextFactory<MemoryDbContext>? _dbContextFactory;
    private readonly IMemoryIndexer? _memoryIndexer;
    private readonly IMemoryLlmClient? _memoryLlmClient;

    public MemoryEngine(
        SessionMemoryStore sessionStore,
        WorkspaceMemoryStore workspaceStore,
        MemoryBoundaryService boundary,
        IDbContextFactory<MemoryDbContext>? dbContextFactory = null,
        IMemoryIndexer? memoryIndexer = null,
        IMemoryLlmClient? memoryLlmClient = null)
    {
        _sessionStore = sessionStore;
        _workspaceStore = workspaceStore;
        _boundary = boundary;
        _dbContextFactory = dbContextFactory;
        _memoryIndexer = memoryIndexer;
        _memoryLlmClient = memoryLlmClient;
    }

    // ── Recall ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 召回与当前 Session / Workspace 相关的记忆，构造为可注入系统提示的文本块。
    /// 若没有记忆则返回 null（无需注入空块）。
    /// </summary>
    public string? BuildMemoryContext(
        string sessionId,
        string? workspaceId,
        string? agentId,
        string? parentSessionId = null)
    {
        var sb = new StringBuilder();

        var sessionIds = new List<string> { sessionId };
        if (!string.IsNullOrWhiteSpace(parentSessionId)
            && !string.Equals(parentSessionId, sessionId, StringComparison.Ordinal))
        {
            sessionIds.Add(parentSessionId);
        }

        var sessionMems = _sessionStore.Recall(sessionIds, agentId);
        if (sessionMems.Count > 0)
        {
            sb.AppendLine("## Session Memory (short-term)");
            foreach (var m in sessionMems.TakeLast(20))
                sb.AppendLine($"- [{m.Tag}] {m.Content}");
        }

        if (!string.IsNullOrEmpty(workspaceId))
        {
            var wsMems = _workspaceStore.Recall(workspaceId, agentId, tag: null);
            if (wsMems.Count > 0)
            {
                sb.AppendLine("## Workspace Memory (long-term)");
                foreach (var m in wsMems.Take(30))
                    sb.AppendLine($"- [{m.Tag}] {m.Content}");
            }
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// 主动召回：记忆 LLM 理解意图 → TagTree 检索 → 五维评分排序 → 时间过滤。
    /// </summary>
    public async Task<string?> RecallWithIntentAsync(
        string userMessage,
        string workspaceId,
        string agentId,
        string? sessionId = null,
        int maxTokens = 2000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
        {
            return BuildMemoryContext(sessionId ?? string.Empty, workspaceId, agentId);
        }

        if (_dbContextFactory is null)
        {
            return BuildMemoryContext(sessionId ?? string.Empty, workspaceId, agentId);
        }

        MemoryQueryIntent? intent = null;
        if (_memoryLlmClient is not null && !string.IsNullOrWhiteSpace(userMessage))
        {
            try
            {
                intent = await _memoryLlmClient.ParseIntentAsync(userMessage, ct);
            }
            catch
            {
                intent = null;
            }
        }

        List<MemoryEntity> candidates;
        if (intent is not null)
        {
            candidates = await SearchByIntentAsync(workspaceId, agentId, intent, ct);
            if (candidates.Count == 0)
            {
                var fallbackQuery = string.IsNullOrWhiteSpace(intent.SearchQuery) ? userMessage : intent.SearchQuery;
                candidates = await Fts5SearchAsync(workspaceId, agentId, fallbackQuery, ct);
            }
        }
        else
        {
            candidates = await Fts5SearchAsync(workspaceId, agentId, userMessage, ct);
        }

        var keywordTerms = BuildKeywordTerms(intent, userMessage);
        var ranked = RankAndFilter(candidates, intent?.TimeRange, keywordTerms);
        if (ranked.Count == 0)
        {
            return BuildMemoryContext(sessionId ?? string.Empty, workspaceId, agentId);
        }

        return FormatForContextInjection(ranked, maxTokens);
    }

    // ── WriteBack ────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 LLM 回复文本和 AgentInstance 来源中解析 REMEMBER 标记并写入存储。
    /// 格式: REMEMBER[tag]: content
    ///   - tag 为 "workspace" 时写 Workspace（需要来源受信任）
    ///   - 其他 tag 写 Session
    /// </summary>
    public void WriteBack(
        string llmReply,
        string sessionId,
        string? workspaceId,
        string source,
        string? agentId = null,
        string? parentSessionId = null)
    {
        if (string.IsNullOrEmpty(llmReply)) return;

        var effectiveAgentId = string.IsNullOrWhiteSpace(agentId) ? source : agentId;

        foreach (Match match in RememberPattern.Matches(llmReply))
        {
            var rawTag = match.Groups["tag"].Value.Trim();
            var content = match.Groups["content"].Value.Trim();
            if (string.IsNullOrEmpty(content)) continue;

            var isWorkspace = rawTag.Equals("workspace", StringComparison.OrdinalIgnoreCase);
            var normalizedTag = isWorkspace ? "workspace" : NormalizeTagPath(rawTag);

            if (isWorkspace && !string.IsNullOrEmpty(workspaceId) && _boundary.CanWriteWorkspace(source))
            {
                _workspaceStore.Write(workspaceId, new MemoryEntry
                {
                    SessionId = sessionId,
                    ParentSessionId = parentSessionId,
                    WorkspaceId = workspaceId,
                    AgentId = effectiveAgentId,
                    Tag = "workspace",
                    Content = content,
                    Source = source,
                    Scope = MemoryScope.Workspace,
                });
            }
            else
            {
                _sessionStore.Write(sessionId, new MemoryEntry
                {
                    SessionId = sessionId,
                    ParentSessionId = parentSessionId,
                    WorkspaceId = workspaceId,
                    AgentId = effectiveAgentId,
                    Tag = normalizedTag,
                    Content = content,
                    Source = source,
                    Scope = MemoryScope.Session,
                });
            }
        }
    }

    // ── 显式写入 API ─────────────────────────────────────────────────────────

    /// <summary>显式写入一条 Session 记忆（如工具调用结果摘要）。</summary>
    public void WriteSession(
        string sessionId,
        string tag,
        string content,
        string source,
        string? workspaceId = null,
        string? agentId = null,
        string? parentSessionId = null) =>
        _sessionStore.Write(sessionId, new MemoryEntry
        {
            SessionId = sessionId,
            ParentSessionId = parentSessionId,
            WorkspaceId = workspaceId,
            AgentId = string.IsNullOrWhiteSpace(agentId) ? source : agentId,
            Tag = NormalizeTagPath(tag),
            Content = content,
            Source = source,
            Scope = MemoryScope.Session,
        });

    /// <summary>显式写入一条 Workspace 记忆（需要来源受信任）。</summary>
    public bool WriteWorkspace(
        string workspaceId,
        string tag,
        string content,
        string source,
        string sessionId = "",
        string? agentId = null,
        string? parentSessionId = null)
    {
        if (!_boundary.CanWriteWorkspace(source)) return false;
        _workspaceStore.Write(workspaceId, new MemoryEntry
        {
            SessionId = sessionId,
            ParentSessionId = parentSessionId,
            WorkspaceId = workspaceId,
            AgentId = string.IsNullOrWhiteSpace(agentId) ? source : agentId,
            Tag = NormalizeTagPath(tag),
            Content = content,
            Source = source,
            Scope = MemoryScope.Workspace,
        });
        return true;
    }

    /// <summary>Session 结束时清理 Session 级记忆。</summary>
    public void ClearSession(string sessionId) => _sessionStore.Clear(sessionId);

    /// <summary>
    /// 使用 FTS5 对消息内容执行全文搜索。
    /// </summary>
    /// <param name="db">记忆数据库上下文。</param>
    /// <param name="query">FTS 查询表达式。</param>
    /// <param name="topK">返回条数上限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的消息列表。</returns>
    public async Task<IReadOnlyList<MessageEntity>> SearchMessagesAsync(
        MemoryDbContext db,
        string query,
        int topK = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedTopK = Math.Clamp(topK, 1, 200);

        var queryParameter = new SqliteParameter("@query", query.Trim());
        var limitParameter = new SqliteParameter("@topK", normalizedTopK);

        var rows = await db.Messages
            .FromSqlRaw(
                "SELECT m.* FROM Messages m JOIN Messages_fts fts ON m.rowid = fts.rowid WHERE Messages_fts MATCH @query ORDER BY bm25(Messages_fts) LIMIT @topK",
                queryParameter,
                limitParameter)
            .AsNoTracking()
            .ToListAsync(ct);

        return rows;
    }

    private async Task<List<MemoryEntity>> SearchByIntentAsync(
        string workspaceId,
        string agentId,
        MemoryQueryIntent intent,
        CancellationToken ct)
    {
        if (_dbContextFactory is null)
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var candidates = new List<MemoryEntity>(256);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(intent.TagPrefix))
        {
            var tagPrefix = NormalizeTagPath(intent.TagPrefix);
            if (!string.IsNullOrWhiteSpace(tagPrefix) && _memoryIndexer is not null)
            {
                var hits = await _memoryIndexer.SearchByTagPrefixAsync(workspaceId, agentId, tagPrefix, topK: 80, ct);
                if (hits.Count > 0)
                {
                    var hitIds = hits
                        .Select(h => h.MemoryId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    if (hitIds.Length > 0)
                    {
                        var byTagTree = await db.Memories
                            .AsNoTracking()
                            .Where(m => m.WorkspaceId == workspaceId
                                     && m.AgentId == agentId
                                     && m.SupersededBy == null
                                     && hitIds.Contains(m.MemoryId))
                            .ToListAsync(ct);
                        AddCandidates(candidates, seen, byTagTree);
                    }
                }
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(tagPrefix))
            {
                var byPrefix = await db.Memories
                    .AsNoTracking()
                    .Where(m => m.WorkspaceId == workspaceId
                             && m.AgentId == agentId
                             && m.SupersededBy == null
                             && (m.Tag == tagPrefix || m.Tag.StartsWith(tagPrefix + "/")))
                    .OrderByDescending(m => m.Importance)
                    .ThenByDescending(m => m.CreatedAt)
                    .Take(120)
                    .ToListAsync(ct);
                AddCandidates(candidates, seen, byPrefix);
            }
        }

        var recents = await db.Memories
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId
                     && m.AgentId == agentId
                     && m.SupersededBy == null)
            .OrderByDescending(m => m.CreatedAt)
            .Take(180)
            .ToListAsync(ct);
        AddCandidates(candidates, seen, recents);

        return candidates;
    }

    private async Task<List<MemoryEntity>> Fts5SearchAsync(
        string workspaceId,
        string agentId,
        string query,
        CancellationToken ct)
    {
        if (_dbContextFactory is null || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        IReadOnlyList<MessageEntity> hits;
        try
        {
            hits = await SearchMessagesAsync(db, BuildFtsQuery(query), topK: 80, ct);
        }
        catch
        {
            return [];
        }

        if (hits.Count == 0)
        {
            return [];
        }

        var sessionIds = hits
            .Select(x => x.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sessionIds.Length == 0)
        {
            return [];
        }

        var allowed = await db.Sessions
            .AsNoTracking()
            .Where(s => sessionIds.Contains(s.SessionId)
                     && s.WorkspaceId == workspaceId
                     && s.AgentId == agentId)
            .Select(s => s.SessionId)
            .ToListAsync(ct);
        if (allowed.Count == 0)
        {
            return [];
        }

        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        return hits
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && allowedSet.Contains(m.SessionId))
            .OrderByDescending(m => m.CreatedAt)
            .Take(80)
            .Select(MapMessageToMemoryCandidate)
            .ToList();
    }

    private static IReadOnlyList<RankedMemory> RankAndFilter(
        IReadOnlyList<MemoryEntity> candidates,
        string? timeRange,
        IReadOnlyList<string> keywordTerms)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var (minCreatedAt, maxCreatedAt) = ResolveTimeWindow(timeRange);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var memoryById = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.MemoryId))
            .GroupBy(x => x.MemoryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var ranked = new List<RankedMemory>(candidates.Count);
        foreach (var item in candidates)
        {
            if (item.ExpiresAt is { } expiresAt && expiresAt < now)
            {
                continue;
            }

            if (item.CreatedAt < minCreatedAt || item.CreatedAt > maxCreatedAt)
            {
                continue;
            }

            var importance = Math.Clamp(item.Importance, 0, 1);
            var confidence = Math.Clamp(item.Confidence, 0, 1);
            var freshness = CalculateFreshness(item.CreatedAt, now);
            var persistence = CalculatePersistence(item, memoryById);
            var keywordMatch = CalculateKeywordMatch(item, keywordTerms);

            var score = importance * 0.3
                      + confidence * 0.2
                      + freshness * 0.15
                      + persistence * 0.2
                      + keywordMatch * 0.15;

            ranked.Add(new RankedMemory(item, score));
        }

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.CreatedAt)
            .Take(50)
            .ToArray();
    }

    private static string? FormatForContextInjection(IReadOnlyList<RankedMemory> ranked, int maxTokens)
    {
        if (ranked.Count == 0)
        {
            return null;
        }

        var tokenBudget = Math.Clamp(maxTokens, 200, 4000);
        var charBudget = tokenBudget * 4;

        var sb = new StringBuilder();
        sb.AppendLine("## Memory Recall (intent-aware)");

        foreach (var item in ranked)
        {
            var content = (item.Memory.Content ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (content.Length == 0)
            {
                continue;
            }

            if (content.Length > 300)
            {
                content = content[..300] + "…";
            }

            var line = $"- [{item.Memory.Tag}] {content}";
            if (sb.Length + line.Length + Environment.NewLine.Length > charBudget)
            {
                break;
            }

            sb.AppendLine(line);
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static (long Min, long Max) ResolveTimeWindow(string? range)
    {
        var now = DateTimeOffset.UtcNow;
        return range?.Trim().ToLowerInvariant() switch
        {
            "recent" => (now.AddDays(-7).ToUnixTimeMilliseconds(), long.MaxValue),
            "recent_month" => (now.AddDays(-30).ToUnixTimeMilliseconds(), long.MaxValue),
            "months_ago" => (
                now.AddDays(-180).ToUnixTimeMilliseconds(),
                now.AddDays(-60).ToUnixTimeMilliseconds()),
            _ => (0, long.MaxValue),
        };
    }

    private static double CalculateFreshness(long createdAt, long now)
    {
        var ageMs = Math.Max(0, now - createdAt);
        var ageDays = ageMs / MillisecondsPerDay;
        return 1d / (1d + Math.Log(1d + ageDays));
    }

    private static double CalculatePersistence(
        MemoryEntity memory,
        IReadOnlyDictionary<string, MemoryEntity> memoryById)
    {
        if (string.IsNullOrWhiteSpace(memory.SupersededBy))
        {
            return 1;
        }

        var chainLength = 1;
        var current = memory.SupersededBy;
        var safety = 0;

        while (!string.IsNullOrWhiteSpace(current) && safety < 16)
        {
            safety++;
            if (!memoryById.TryGetValue(current, out var next))
            {
                break;
            }

            current = next.SupersededBy;
            if (!string.IsNullOrWhiteSpace(current))
            {
                chainLength++;
            }
        }

        return 1d / (1d + chainLength);
    }

    private static double CalculateKeywordMatch(MemoryEntity memory, IReadOnlyList<string> keywordTerms)
    {
        if (keywordTerms.Count == 0)
        {
            return 0.5;
        }

        var text = $"{memory.Tag} {memory.Content}".ToLowerInvariant();
        if (text.Length == 0)
        {
            return 0;
        }

        double weightedHits = 0;
        foreach (var term in keywordTerms)
        {
            var normalized = term.Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                continue;
            }

            var count = CountOccurrences(text, normalized);
            if (count <= 0)
            {
                continue;
            }

            weightedHits += Math.Min(1.0, 0.4 + count * 0.2);
        }

        return Math.Clamp(weightedHits / keywordTerms.Count, 0, 1);
    }

    private static int CountOccurrences(string text, string keyword)
    {
        if (keyword.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var hit = text.IndexOf(keyword, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            count++;
            index = hit + keyword.Length;
        }

        return count;
    }

    private static IReadOnlyList<string> BuildKeywordTerms(MemoryQueryIntent? intent, string userMessage)
    {
        var terms = new List<string>();

        if (intent is not null)
        {
            terms.AddRange(intent.Entities.Where(x => !string.IsNullOrWhiteSpace(x)));
            terms.AddRange(SplitTerms(intent.SearchQuery));
        }

        if (terms.Count == 0)
        {
            terms.AddRange(SplitTerms(userMessage));
        }

        return terms
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static IEnumerable<string> SplitTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(
                [' ', '\t', '\r', '\n', ',', '，', '.', '。', '!', '！', '?', '？', ';', '；', ':', '：', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string BuildFtsQuery(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return string.Empty;
        }

        var terms = SplitTerms(rawQuery)
            .Select(x => x.Replace("\"", string.Empty, StringComparison.Ordinal))
            .Where(x => x.Length > 0)
            .Take(8)
            .ToArray();

        if (terms.Length == 0)
        {
            return rawQuery.Trim();
        }

        return string.Join(' ', terms.Select(t => $"\"{t}\""));
    }

    private static MemoryEntity MapMessageToMemoryCandidate(MessageEntity message)
    {
        return new MemoryEntity
        {
            MemoryId = string.IsNullOrWhiteSpace(message.MessageId)
                ? Guid.NewGuid().ToString("N")
                : message.MessageId,
            Scope = "session",
            SessionId = message.SessionId,
            Tag = "message/fts",
            Content = message.Content ?? string.Empty,
            Importance = 0.45,
            Confidence = 0.55,
            CreatedAt = message.CreatedAt,
            ExpiresAt = null,
            SupersededBy = null,
        };
    }

    private static void AddCandidates(
        ICollection<MemoryEntity> destination,
        ISet<string> seen,
        IEnumerable<MemoryEntity> source)
    {
        foreach (var item in source)
        {
            var key = string.IsNullOrWhiteSpace(item.MemoryId)
                ? $"generated:{item.Tag}:{item.CreatedAt}:{item.Content.GetHashCode()}"
                : item.MemoryId;

            if (!seen.Add(key))
            {
                continue;
            }

            destination.Add(item);
        }
    }

    private static string NormalizeTagPath(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return "general";
        }

        var segments = rawTag
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        return segments.Length == 0
            ? "general"
            : string.Join('/', segments);
    }

    private sealed record RankedMemory(MemoryEntity Memory, double Score);
}
