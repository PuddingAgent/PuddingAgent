using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// EF Core implementation of ISessionEventLogRepository.
/// </summary>
public sealed class SessionEventLogRepository : ISessionEventLogRepository
{
    private readonly PlatformDbContext _db;

    public SessionEventLogRepository(PlatformDbContext db) => _db = db;

    public async Task<bool> AnyForSessionAsync(string sessionId, CancellationToken ct = default)
        => await _db.SessionEventLogs.AnyAsync(e => e.SessionId == sessionId, ct);

    public async Task<IReadOnlyList<SessionEventRow>> GetByEventTypesAsync(
        string sessionId, string[] eventTypes, CancellationToken ct = default)
    {
        var events = await _db.SessionEventLogs.AsNoTracking()
            .Where(e => e.SessionId == sessionId && eventTypes.Contains(e.EventType))
            .OrderBy(e => e.SequenceNum)
            .ToListAsync(ct);

        return events.Select(e => new SessionEventRow
        {
            Id = e.Id,
            SessionId = e.SessionId,
            EventType = e.EventType,
            Data = e.Data,
            CreatedAt = DateTimeOffset.TryParse(e.RecordedAt, out var dt) ? dt.Ticks : 0,
            SequenceNum = e.SequenceNum,
        }).ToList();
    }

    public async Task<long> GetCountAsync(string sessionId, CancellationToken ct = default)
        => await _db.SessionEventLogs.LongCountAsync(e => e.SessionId == sessionId, ct);
}
