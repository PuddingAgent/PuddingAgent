using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// ISessionProjectionStore 的 SQLite 实现。
/// <para>
/// 维护 lightweight session_projection_cursors 表（session_id PK + projected_through_sequence）。
/// 使用 ConcurrentDictionary 内存缓存避免每次查询 SQLite。
/// </para>
/// </summary>
public sealed class SessionProjectionStore : ISessionProjectionStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, long> _cache = new();
    private bool _tableEnsured;

    public SessionProjectionStore(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    private async ValueTask EnsureTableAsync(CancellationToken ct)
    {
        if (_tableEnsured) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS session_projection_cursors (" +
            "session_id TEXT PRIMARY KEY," +
            "projected_through_sequence INTEGER NOT NULL DEFAULT 0" +
            ")", ct);
        _tableEnsured = true;
    }

    public async Task<long> GetProjectedCursorAsync(string sessionId, CancellationToken ct)
    {
        if (_cache.TryGetValue(sessionId, out var cached))
            return cached;

        await EnsureTableAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT projected_through_sequence FROM session_projection_cursors WHERE session_id = @sid";
        var p = cmd.CreateParameter(); p.ParameterName = "@sid"; p.Value = sessionId;
        cmd.Parameters.Add(p);
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct);
        var result = await cmd.ExecuteScalarAsync(ct);

        var cursor = result is long l ? l : 0L;
        _cache[sessionId] = cursor;
        return cursor;
    }

    public async Task SetProjectedCursorAsync(string sessionId, long sequence, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO session_projection_cursors (session_id, projected_through_sequence) " +
            "VALUES ({0}, {1}) " +
            "ON CONFLICT(session_id) DO UPDATE SET projected_through_sequence = MAX(projected_through_sequence, {1})",
            sessionId, sequence);

        _cache[sessionId] = sequence;
    }
}
