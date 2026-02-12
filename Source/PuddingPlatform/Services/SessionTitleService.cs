namespace PuddingPlatform.Services;

/// <summary>Builds stable default chat session titles per workspace and agent.</summary>
public sealed class SessionTitleService(
    PlatformApiClient api,
    ILogger<SessionTitleService> logger)
{
    /// <summary>
    /// Returns the next title in the "{base}{n}" sequence, such as "默认助手1".
    /// Existing unsuffixed titles are treated as sequence number 1.
    /// </summary>
    public async Task<string> BuildDefaultTitleAsync(
        string workspaceId,
        string agentTemplateId,
        string? titleBase,
        CancellationToken ct = default)
    {
        var normalizedBase = NormalizeTitleBase(titleBase);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var hotSessions = await api.GetSessionsAsync(workspaceId, ct);
            foreach (var session in hotSessions.Where(s =>
                         s.WorkspaceId.Equals(workspaceId, StringComparison.Ordinal)
                         && s.AgentTemplateId.Equals(agentTemplateId, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(s.Title)))
            {
                titles[session.SessionId] = session.Title!;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[SessionTitle] Failed to read hot sessions workspace={WorkspaceId} agentTemplate={AgentTemplateId}",
                workspaceId,
                agentTemplateId);
        }

        var maxSequence = 0;
        foreach (var title in titles.Values)
        {
            if (TryReadSequence(title, normalizedBase, out var sequence))
                maxSequence = Math.Max(maxSequence, sequence);
        }

        return $"{normalizedBase}{maxSequence + 1}";
    }

    private static string NormalizeTitleBase(string? titleBase)
    {
        var normalized = (titleBase ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "对话" : normalized;
    }

    private static bool TryReadSequence(string title, string titleBase, out int sequence)
    {
        sequence = 0;
        var normalized = title.Trim();
        if (normalized.Equals(titleBase, StringComparison.Ordinal))
        {
            sequence = 1;
            return true;
        }

        if (!normalized.StartsWith(titleBase, StringComparison.Ordinal))
            return false;

        var suffix = normalized[titleBase.Length..].Trim();
        return int.TryParse(suffix, out sequence) && sequence > 0;
    }
}
