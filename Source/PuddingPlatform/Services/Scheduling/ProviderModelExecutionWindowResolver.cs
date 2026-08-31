using PuddingCode.Abstractions;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Resolves automatic-work admission from the Agent's effective provider/model
/// route and the versioned price windows in llm.providers.json. Unknown routes,
/// stale profiles and malformed windows fail closed.
/// </summary>
public sealed class ProviderModelExecutionWindowResolver(
    ILlmConfigService llmConfigService,
    IWorkspaceAgentCatalog agentCatalog) : IExecutionWindowResolver
{
    private static readonly TimeSpan AnytimeDecisionTtl = TimeSpan.FromSeconds(30);

    public async Task<ExecutionWindowDecision> EvaluateAsync(
        string workspaceId,
        string agentId,
        TaskExecutionWindow requestedWindow,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        if (requestedWindow == TaskExecutionWindow.Anytime)
        {
            return Decision(
                ExecutionWindowVerdict.Allow,
                "allowed_anytime",
                now,
                now.Add(AnytimeDecisionTtl));
        }

        var agent = (await agentCatalog.ListAgentsAsync(workspaceId, ct))
            .FirstOrDefault(item => string.Equals(item.AgentId, agentId, StringComparison.Ordinal));
        if (agent is null || !agent.IsEnabled || agent.IsFrozen)
        {
            return Decision(
                ExecutionWindowVerdict.Unknown,
                "execution_window_agent_route_unknown",
                now,
                now);
        }

        var providerId = agent.PreferredProviderId?.Trim();
        var modelId = agent.PreferredModelId?.Trim();
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)
            || llmConfigService.Resolve(providerId, modelId) is null)
        {
            return Decision(
                ExecutionWindowVerdict.Unknown,
                "execution_window_provider_model_unknown",
                now,
                now,
                providerId: providerId,
                modelId: modelId);
        }

        var model = llmConfigService.GetAllModels().FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
            && !item.IsDeprecated);
        if (model is null
            || string.IsNullOrWhiteSpace(model.PriceWindowProfileVersion)
            || model.PriceWindows.Count == 0)
        {
            return Decision(
                ExecutionWindowVerdict.Unknown,
                requestedWindow == TaskExecutionWindow.OffPeakOnly
                    ? "execution_window_route_profile_unknown"
                    : "execution_window_inherited_policy_unknown",
                now,
                now,
                providerId: providerId,
                modelId: modelId);
        }

        var intervals = new List<WindowInterval>();
        var invalidWindow = false;
        foreach (var window in model.PriceWindows.Where(item => item.IsOffPeak))
        {
            if (!TryExpandWindow(window, now, intervals))
                invalidWindow = true;
        }

        if (intervals.Count == 0)
        {
            return Decision(
                ExecutionWindowVerdict.Unknown,
                invalidWindow
                    ? "execution_window_profile_invalid"
                    : "execution_window_off_peak_profile_empty",
                now,
                now,
                providerId,
                modelId,
                profileVersion: model.PriceWindowProfileVersion);
        }

        var active = intervals
            .Where(item => item.StartUtc <= now && now < item.EndUtc)
            .OrderBy(item => item.EndUtc)
            .FirstOrDefault();
        if (active is not null)
        {
            return Decision(
                ExecutionWindowVerdict.Allow,
                requestedWindow == TaskExecutionWindow.OffPeakOnly
                    ? "allowed_off_peak"
                    : "allowed_inherited_off_peak",
                now,
                active.EndUtc,
                providerId,
                modelId,
                active.WindowKey,
                model.PriceWindowProfileVersion);
        }

        var next = intervals
            .Where(item => item.StartUtc > now)
            .OrderBy(item => item.StartUtc)
            .FirstOrDefault();
        if (next is null)
        {
            return Decision(
                ExecutionWindowVerdict.Unknown,
                invalidWindow
                    ? "execution_window_profile_invalid"
                    : "execution_window_profile_expired",
                now,
                now,
                providerId,
                modelId,
                profileVersion: model.PriceWindowProfileVersion);
        }

        return Decision(
            ExecutionWindowVerdict.Defer,
            "execution_window_peak_period",
            now,
            next.StartUtc,
            providerId,
            modelId,
            next.WindowKey,
            model.PriceWindowProfileVersion,
            next.StartUtc);
    }

    private static bool TryExpandWindow(
        LlmPriceWindowInfo window,
        DateTimeOffset now,
        ICollection<WindowInterval> output)
    {
        if (string.IsNullOrWhiteSpace(window.WindowKey)
            || !TimeOnly.TryParseExact(window.StartLocalTime, "HH:mm", out var startTime)
            || !TimeOnly.TryParseExact(window.EndLocalTime, "HH:mm", out var endTime)
            || startTime == endTime
            || !TryResolveTimeZone(window.TimeZoneId, out var zone))
        {
            return false;
        }

        var days = new HashSet<DayOfWeek>();
        foreach (var value in window.DaysOfWeek)
        {
            if (!Enum.TryParse<DayOfWeek>(value, ignoreCase: true, out var day))
                return false;
            days.Add(day);
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        for (var dayOffset = -1; dayOffset <= 8; dayOffset++)
        {
            var localDate = DateOnly.FromDateTime(localNow.Date).AddDays(dayOffset);
            if (days.Count > 0 && !days.Contains(localDate.DayOfWeek))
                continue;

            var startLocal = localDate.ToDateTime(startTime, DateTimeKind.Unspecified);
            var endDate = endTime > startTime ? localDate : localDate.AddDays(1);
            var endLocal = endDate.ToDateTime(endTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal))
                return false;

            DateTimeOffset startUtc;
            DateTimeOffset endUtc;
            try
            {
                startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
                endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (window.EffectiveAtUtc is { } effective && startUtc < effective)
                startUtc = effective;
            if (window.ExpiresAtUtc is { } expires && endUtc > expires)
                endUtc = expires;
            if (endUtc <= startUtc || endUtc <= now.AddDays(-1))
                continue;

            output.Add(new WindowInterval(window.WindowKey, startUtc, endUtc));
        }

        return true;
    }

    private static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = null!;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            // Continue with platform-id conversion below.
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        string? alternate = null;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId))
            alternate = windowsId;
        else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId))
            alternate = ianaId;
        if (string.IsNullOrWhiteSpace(alternate))
            return false;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(alternate);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static ExecutionWindowDecision Decision(
        ExecutionWindowVerdict verdict,
        string code,
        DateTimeOffset now,
        DateTimeOffset validUntilUtc,
        string? providerId = null,
        string? modelId = null,
        string? windowKey = null,
        string? profileVersion = null,
        DateTimeOffset? nextEligibleAtUtc = null) => new()
    {
        Verdict = verdict,
        Code = code,
        EvaluatedAtUtc = now,
        ValidUntilUtc = validUntilUtc,
        NextEligibleAtUtc = nextEligibleAtUtc,
        ProviderId = providerId,
        ModelId = modelId,
        WindowKey = windowKey,
        ProfileVersion = profileVersion,
    };

    private sealed record WindowInterval(
        string WindowKey,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);
}
