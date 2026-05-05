using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine;

/// <summary>
/// 记忆引擎主类——负责：
/// 1. Recall（召回）：从 Session/Workspace 存储中提取相关记忆，拼接为系统提示注入片段。
/// 2. WriteBack（写回）：从 LLM 回复中解析 "REMEMBER:..." 标记，写入相应存储。
/// </summary>
public sealed class MemoryEngine
{
    private static readonly Regex RememberPattern =
        new(@"\bREMEMBER\[(?<tag>[^\]]*)\]:\s*(?<content>.+?)(?=\bREMEMBER\[|$)",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly SessionMemoryStore _sessionStore;
    private readonly WorkspaceMemoryStore _workspaceStore;
    private readonly MemoryBoundaryService _boundary;

    public MemoryEngine(
        SessionMemoryStore sessionStore,
        WorkspaceMemoryStore workspaceStore,
        MemoryBoundaryService boundary)
    {
        _sessionStore = sessionStore;
        _workspaceStore = workspaceStore;
        _boundary = boundary;
    }

    // ── Recall ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 召回与当前 Session / Workspace 相关的记忆，构造为可注入系统提示的文本块。
    /// 若没有记忆则返回 null（无需注入空块）。
    /// </summary>
    public string? BuildMemoryContext(string sessionId, string? workspaceId)
    {
        var sb = new StringBuilder();

        var sessionMems = _sessionStore.Recall(sessionId);
        if (sessionMems.Count > 0)
        {
            sb.AppendLine("## Session Memory (short-term)");
            foreach (var m in sessionMems.TakeLast(20))
                sb.AppendLine($"- [{m.Tag}] {m.Content}");
        }

        if (!string.IsNullOrEmpty(workspaceId))
        {
            var wsMems = _workspaceStore.Recall(workspaceId);
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
        string source)
    {
        if (string.IsNullOrEmpty(llmReply)) return;

        foreach (Match match in RememberPattern.Matches(llmReply))
        {
            var tag = match.Groups["tag"].Value.Trim();
            var content = match.Groups["content"].Value.Trim();
            if (string.IsNullOrEmpty(content)) continue;

            var isWorkspace = tag.Equals("workspace", StringComparison.OrdinalIgnoreCase);
            if (isWorkspace && !string.IsNullOrEmpty(workspaceId) && _boundary.CanWriteWorkspace(source))
            {
                _workspaceStore.Write(workspaceId, new MemoryEntry
                {
                    SessionId = sessionId,
                    WorkspaceId = workspaceId,
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
                    WorkspaceId = workspaceId,
                    Tag = string.IsNullOrEmpty(tag) ? "general" : tag,
                    Content = content,
                    Source = source,
                    Scope = MemoryScope.Session,
                });
            }
        }
    }

    // ── 显式写入 API ─────────────────────────────────────────────────────────

    /// <summary>显式写入一条 Session 记忆（如工具调用结果摘要）。</summary>
    public void WriteSession(string sessionId, string tag, string content, string source, string? workspaceId = null) =>
        _sessionStore.Write(sessionId, new MemoryEntry
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            Tag = tag,
            Content = content,
            Source = source,
            Scope = MemoryScope.Session,
        });

    /// <summary>显式写入一条 Workspace 记忆（需要来源受信任）。</summary>
    public bool WriteWorkspace(string workspaceId, string tag, string content, string source, string sessionId = "")
    {
        if (!_boundary.CanWriteWorkspace(source)) return false;
        _workspaceStore.Write(workspaceId, new MemoryEntry
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            Tag = tag,
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
}
