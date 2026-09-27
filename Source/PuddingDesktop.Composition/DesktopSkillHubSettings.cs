using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Skills;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-07 (overview + events slice): reads the SKILL Hub through ISkillHubService, which is registered
/// scoped, so it is resolved from the per-operation Core scope rather than captured.
///
/// Read-only in this slice: publish, evolve, retire and install write paths are not exposed yet.
/// </summary>
internal sealed class DesktopSkillHubSettings(IDesktopKernel kernel) : ISkillHubSettings
{
    private Task<T> Hub<T>(string operationId, Func<ISkillHubService, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<ISkillHubService>(), token), cancellationToken);

    public Task<SkillHubOverview> ReadOverviewAsync(CancellationToken cancellationToken = default)
        => Hub("skills.overview", async (hub, token) =>
        {
            var stats = await hub.GetStatsAsync(token);
            return new SkillHubOverview(
                stats.TotalSkills, stats.ActiveSkills, stats.RetiredSkills, stats.TotalVersions,
                stats.TotalInstalls, stats.DistinctAgents, stats.EvolvedSkills,
                stats.EvolutionActionCounts.Select(count => new SkillActionCount(count.Action, count.Count)).ToArray(),
                stats.TopInstalled.Select(skill => new SkillTopEntry(skill.SkillId, skill.Name, skill.LatestVersion,
                    skill.Status, skill.InstallCount, skill.VersionCount)).ToArray(),
                stats.GeneratedAt);
        }, cancellationToken);

    public Task<IReadOnlyList<SkillHubSkillSummary>> ListSkillsAsync(
        string? query, string? tag, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
        => Hub("skills.library.list", async (hub, token) =>
        {
            var skills = await hub.ListSkillsAsync(query, tag, status, page, pageSize, token);
            return (IReadOnlyList<SkillHubSkillSummary>)skills.Select(skill => new SkillHubSkillSummary(
                skill.SkillId, skill.Name, skill.Summary ?? "", skill.Description ?? "",
                skill.Tags ?? [], skill.Keywords ?? [],
                skill.LatestVersion, skill.Status, skill.Visibility,
                skill.VersionCount, skill.InstallCount, skill.PublishCount,
                skill.LatestContentHash ?? "", skill.CreatedAt, skill.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<SkillHubEvent>> ListEventsAsync(string? skillId, int limit, CancellationToken cancellationToken = default)
        => Hub("skills.events.list", async (hub, token) =>
        {
            var events = await hub.ListEventsAsync(skillId, limit, token);
            return (IReadOnlyList<SkillHubEvent>)events.Select(entry => new SkillHubEvent(
                entry.Id, entry.SkillId, entry.Version ?? "", entry.EventType,
                entry.ActorKind, entry.ActorId ?? "", entry.WorkspaceId ?? "", entry.PayloadJson ?? "",
                entry.CreatedAt)).ToArray();
        }, cancellationToken);
}
