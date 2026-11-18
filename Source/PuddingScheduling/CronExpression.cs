namespace PuddingScheduling;

/// <summary>
/// Minimal five-field cron expression: minute hour day-of-month month day-of-week.
///
/// Pure and clock-free on purpose: <see cref="NextOccurrence"/> takes the reference
/// instant and the time zone from the caller, so every date rule (month ends, leap
/// years, DST transitions) can be covered by deterministic unit tests. Date logic of
/// this kind cannot be trusted to code review.
///
/// Supported syntax in every field: <c>*</c>, <c>n</c>, <c>a-b</c>, <c>a,b,c</c>,
/// <c>*/n</c>, <c>a-b/n</c>. Day-of-week accepts 0-6 (0 = Sunday) and 7 as an alias
/// for Sunday.
/// </summary>
public sealed class CronExpression
{
    private const int MaxSearchYears = 5;

    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    /// <summary>True when the field was written as anything other than a bare <c>*</c>.</summary>
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    public string Source { get; }

    private CronExpression(
        string source,
        HashSet<int> minutes,
        HashSet<int> hours,
        HashSet<int> daysOfMonth,
        HashSet<int> months,
        HashSet<int> daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        Source = source;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Cron expression is empty.", nameof(expression));

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
            throw new ArgumentException($"Cron expression must have 5 fields, got {fields.Length}: '{expression}'.", nameof(expression));

        var minutes = ExpandField(fields[0], 0, 59, "minute");
        var hours = ExpandField(fields[1], 0, 23, "hour");
        var daysOfMonth = ExpandField(fields[2], 1, 31, "day-of-month");
        var months = ExpandField(fields[3], 1, 12, "month");

        var daysOfWeek = ExpandField(fields[4], 0, 7, "day-of-week");
        if (daysOfWeek.Remove(7)) daysOfWeek.Add(0);

        return new CronExpression(
            expression.Trim(),
            minutes, hours, daysOfMonth, months, daysOfWeek,
            dayOfMonthRestricted: fields[2] != "*",
            dayOfWeekRestricted: fields[4] != "*");
    }

    /// <summary>
    /// First matching instant strictly after <paramref name="afterUtc"/>.
    /// Returns null when nothing matches within <see cref="MaxSearchYears"/> —
    /// which happens for impossible expressions such as <c>0 0 30 2 *</c>.
    /// </summary>
    public DateTimeOffset? NextOccurrence(TimeZoneInfo timeZone, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        // Work in local wall-clock time, then convert back with the offset that
        // applies at that local instant. This keeps DST handling in one place.
        var local = TimeZoneInfo.ConvertTime(afterUtc, timeZone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);

        var limit = local.AddYears(MaxSearchYears);

        while (local < limit)
        {
            if (!_months.Contains(local.Month))
            {
                local = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(local))
            {
                local = local.Date.AddDays(1);
                continue;
            }

            if (!_hours.Contains(local.Hour))
            {
                local = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (!_minutes.Contains(local.Minute))
            {
                local = local.AddMinutes(1);
                continue;
            }

            // A wall-clock time that does not exist (spring-forward gap) must be skipped.
            if (timeZone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
                continue;
            }

            // Ambiguous times (fall-back overlap) resolve to the standard offset, so the
            // result is stable and the same instant is never returned twice.
            return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
        }

        return null;
    }

    internal bool IsOccurrence(TimeZoneInfo timeZone, DateTimeOffset utcMinute)
    {
        var local = TimeZoneInfo.ConvertTime(utcMinute, timeZone);
        return _months.Contains(local.Month)
            && DayMatches(local.DateTime)
            && _hours.Contains(local.Hour)
            && _minutes.Contains(local.Minute)
            && local.Offset == timeZone.GetUtcOffset(local.DateTime);
    }

    private bool DayMatches(DateTime local)
    {
        var dayOfMonthMatch = _daysOfMonth.Contains(local.Day);
        var dayOfWeekMatch = _daysOfWeek.Contains((int)local.DayOfWeek);

        // Classic cron semantics: with both day fields restricted the rule is OR,
        // otherwise it is AND (and the unrestricted one always matches).
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
            return dayOfMonthMatch || dayOfWeekMatch;

        return dayOfMonthMatch && dayOfWeekMatch;
    }

    private static HashSet<int> ExpandField(string field, int min, int max, string name)
    {
        var result = new HashSet<int>();

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part;
            var step = 1;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                var stepText = part[(slash + 1)..];
                if (!int.TryParse(stepText, out step) || step <= 0)
                    throw new ArgumentException($"Invalid step '{stepText}' in {name} field '{field}'.", name);
            }

            int from, to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(range[..dash], out from) || !int.TryParse(range[(dash + 1)..], out to))
                        throw new ArgumentException($"Invalid range '{range}' in {name} field '{field}'.", name);
                }
                else
                {
                    if (!int.TryParse(range, out from))
                        throw new ArgumentException($"Invalid value '{range}' in {name} field '{field}'.", name);
                    // "5/10" style means "from 5 to max, step 10".
                    to = slash >= 0 ? max : from;
                }
            }

            if (from < min || to > max || from > to)
                throw new ArgumentException($"{name} value out of range [{min},{max}] in '{field}'.", name);

            for (var v = from; v <= to; v += step)
                result.Add(v);
        }

        if (result.Count == 0)
            throw new ArgumentException($"Cron {name} field '{field}' matches nothing.", name);

        return result;
    }
}
