namespace PuddingScheduling;

/// <summary>One schedule's observed tick result. On failure, the persisted cursor stays at the original occurrence.</summary>
public sealed record ScheduleTickResult(
    Guid ScheduleId,
    TickPlan? Plan,
    ScheduleTickWrite? Write,
    Exception? Error);

/// <summary>Plans and commits a bounded scan using a single reading of the injected clock.</summary>
public sealed class ScheduleTickService(IScheduleStore store, TimeProvider clock)
{
    public async Task<IReadOnlyList<ScheduleTickResult>> RunDueAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowUtc = clock.GetUtcNow();
        var schedules = await store.ListDueAsync(nowUtc, limit, cancellationToken);
        var results = new List<ScheduleTickResult>(schedules.Count);
        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TickPlan? plan = null;
            try
            {
                var cron = CronExpression.Parse(schedule.CronExpression);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
                plan = ScheduleTickPlanner.Plan(cron, zone, schedule.ToState(), nowUtc);
                var write = await store.TryApplyTickAsync(schedule, plan, nowUtc, cancellationToken);
                results.Add(new ScheduleTickResult(schedule.Id, plan, write, null));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new ScheduleTickResult(schedule.Id, plan, null, exception));
            }
        }
        return results;
    }
}
