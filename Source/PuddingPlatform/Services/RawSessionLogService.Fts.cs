using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingFullTextIndex.Contracts;

namespace PuddingPlatform.Services;

public sealed partial class RawSessionLogService
{
    private const int MaxFtsDays = 31;
    private const int MaxFtsFilesPerDay = 512;
    private const int MaxFtsResultChars = 6000;

    /// <summary>Explicit, bounded historical evidence search. Never supplies a current decision.</summary>
    public async Task<RawSessionLogSearchResult> GrepFtsAsync(
        RawSessionLogSearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.AgentInstanceId)
            || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2048 || request.Regex)
            return new([], false, "contract_error", "FTS requires workspace, agent and a plain-text query of at most 2048 characters; use regex grep separately.");
        if (!TryGetFtsDays(request, out var from, out var to))
            return new([], false, "contract_error", "Use day or from_day/to_day in yyyy-MM-dd format, with a range of at most 31 days.");
        var fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        RawSessionLogSearchResult Result(List<RawSessionLogMatch> matches, bool more = false,
            string? status = null, string? error = null) => new(matches, more,
                status ?? (matches.Count == 0 ? "no_match" : "ok"), error,
                more ? "partial" : "indexed_markdown", fromText, toText);
        if (_ftsEngine is null || _dataPaths is null)
            return Result([], status: "unavailable", error: "Full-text search is not configured.");
        if (request.AgentInstanceId is "." or ".." || request.AgentInstanceId.IndexOfAny(['/', '\\', ':']) >= 0)
            return Result([], status: "contract_error", error: "Invalid agent identity.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var matches = new List<RawSessionLogMatch>();
        try
        {
            // Use the catalog, not a distinct scan over GB of event payloads, to constrain ownership.
            await using var db = await _dbFactory.CreateDbContextAsync(token);
            var sessions = db.ConversationCatalogs.AsNoTracking().Where(c =>
                c.WorkspaceId == request.WorkspaceId && c.AgentId == request.AgentInstanceId);
            if (!string.IsNullOrWhiteSpace(request.SessionId))
                sessions = sessions.Where(c => c.ConversationId == request.SessionId);
            var ids = await sessions.Select(c => c.ConversationId).Take(MaxFtsFilesPerDay + 1).ToListAsync(token);
            if (ids.Count > MaxFtsFilesPerDay)
                return Result([], status: "contract_error", error: "Too many conversations; specify session_id.");
            if (ids.Count == 0)
                return Result([], status: "unavailable", error: "No conversation catalog entry matches this workspace/agent/session; no private files were searched.");
            var allowed = ids.ToHashSet(StringComparer.Ordinal);
            var root = _dataPaths.AgentInstanceMessageLogsRoot(request.AgentInstanceId);
            var limit = Math.Clamp(request.Limit, 1, 20);
            var resultChars = 0;
            for (var day = to; day >= from; day = day.AddDays(-1))
            {
                token.ThrowIfCancellationRequested();
                var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var directory = Path.Combine(root, dayText);
                if (!Directory.Exists(directory)) continue;
                var paths = new List<string>();
                var scannedFiles = 0;
                foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    if (++scannedFiles > MaxFtsFilesPerDay)
                        return Result(matches, true, "scope_too_large", "Daily index exceeds 512 files; use canonical message search for this scope.");
                    if (allowed.Contains(Path.GetFileNameWithoutExtension(file))) paths.Add(file);
                }
                if (paths.Count == 0) continue;
                // Refresh only selected day shards, never rebuild the agent's entire history.
                var indexed = await _ftsEngine.BuildIndexAsync(directory, "*.md", token);
                token.ThrowIfCancellationRequested();
                if (!indexed.Success)
                    return Result(matches, true, "unavailable", indexed.Error ?? "Index refresh failed.");
                if (!_ftsEngine.HasIndex(directory)) continue; // Successful refresh of empty/removed documents.
                var found = await _ftsEngine.SearchAsync(request.Query, directory,
                    limit - matches.Count + 1, ct: token,
                    scope: new FullTextSearchScope(paths, LiteralQuery: true));
                token.ThrowIfCancellationRequested();
                if (!found.Success)
                    return Result(matches, true, "unavailable", found.Error ?? "Index search failed.");
                foreach (var hit in found.Matches)
                {
                    if (matches.Count == limit) return Result(matches, true);
                    var sessionId = Path.GetFileNameWithoutExtension(hit.FilePath);
                    var start = Math.Max(0, hit.LineText.IndexOf(request.Query, StringComparison.OrdinalIgnoreCase) - 100);
                    var snippet = hit.LineText.Substring(start, Math.Min(320, hit.LineText.Length - start));
                    if (start > 0) snippet = "…" + snippet;
                    if (start + 320 < hit.LineText.Length) snippet += "…";
                    var match = new RawSessionLogMatch(sessionId, request.WorkspaceId, dayText,
                        hit.LineNumber, "markdown_line", dayText, snippet,
                        $"session-log-fts:{dayText}:{sessionId}:{hit.LineNumber}");
                    var chars = JsonSerializer.Serialize(match).Length;
                    if (resultChars + chars > MaxFtsResultChars) return Result(matches, true);
                    matches.Add(match);
                    resultChars += chars;
                }
                if (matches.Count == limit && (found.TotalMatches > found.Matches.Count || day > from))
                    return Result(matches, true);
            }
            return Result(matches);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return Result(matches, true, "timeout", "History search exceeded its 10-second cooperative budget; narrow the range."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result(matches, true, "unavailable", "History index or files could not be read: " + ex.Message); }
    }

    private static bool TryGetFtsDays(RawSessionLogSearchRequest request, out DateOnly from, out DateOnly to)
    {
        to = DateOnly.FromDateTime(DateTime.Now);
        from = to.AddDays(-6);
        static bool Parse(string value, out DateOnly date) => DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        if (!string.IsNullOrWhiteSpace(request.Day))
        {
            if (!string.IsNullOrWhiteSpace(request.FromDay) || !string.IsNullOrWhiteSpace(request.ToDay)
                || !Parse(request.Day, out to)) return false;
            from = to;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.ToDay) && !Parse(request.ToDay, out to)) return false;
            from = DateOnly.FromDayNumber(Math.Max(1, to.DayNumber - 6));
            if (!string.IsNullOrWhiteSpace(request.FromDay) && !Parse(request.FromDay, out from)) return false;
        }
        return from.DayNumber > 0 && to >= from && to.DayNumber - from.DayNumber < MaxFtsDays;
    }
}
