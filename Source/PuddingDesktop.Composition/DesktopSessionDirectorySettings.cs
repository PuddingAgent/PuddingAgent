using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-14 session directory: reads sessions from Core's in-process <see cref="ISessionRepository"/> (the same
/// singleton the session services use), then applies the page's own filters and paging, which Core's
/// repository does not offer.
/// </summary>
internal sealed class DesktopSessionDirectorySettings(IDesktopKernel kernel) : ISessionDirectorySettings
{
    public Task<SessionDirectoryPage> ListAsync(SessionFilter filter, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("sessions.list", async (scope, token) =>
        {
            var normalized = SessionDirectoryText.Normalize(filter);
            // Core 只支持渠道/用户/工作区三个查询条件；其余筛选与分页在本页完成。
            var records = await scope.Services.GetRequiredService<ISessionRepository>().QueryAsync(
                Empty(normalized.ChannelId), Empty(normalized.UserId), Empty(normalized.WorkspaceId), token);
            return SessionDirectoryText.Apply(records.Select(Map).ToArray(), normalized);
        }, cancellationToken);

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static SessionDirectoryEntry Map(SessionRecord record) => new(
        record.SessionId, record.WorkspaceId, record.AgentTemplateId, record.ChannelId, record.OwnerUserId,
        record.SessionType.ToString(), record.SessionRole.ToString(), record.Status.ToString(), record.Title ?? "",
        record.RuntimeNodeId ?? "", record.AgentInstanceId ?? "", record.ParentSessionId ?? "",
        record.RootSessionId ?? "", record.PrincipalKind ?? "", record.PrincipalId ?? "",
        record.CreatedAt, record.LastActiveAt);
}
