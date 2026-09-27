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

    public Task<SkillHubDetail?> ReadSkillAsync(string skillId, CancellationToken cancellationToken = default)
        => Hub("skills.library.detail", async (hub, token) =>
        {
            var detail = await hub.GetSkillAsync(skillId, token);
            return detail is null ? null : new SkillHubDetail(Summary(detail.Skill), detail.Versions.Select(Map).ToArray(),
                detail.RecentInstalls.Select(Map).ToArray());
        }, cancellationToken);

    public Task<SkillHubVersionContent?> ReadVersionAsync(string skillId, string version, CancellationToken cancellationToken = default)
        => Hub("skills.library.version", async (hub, token) =>
        {
            var content = await hub.GetVersionAsync(skillId, version, token);
            return content is null ? null : new SkillHubVersionContent(content.SkillId, content.Version, content.ContentHash,
                content.EvolutionAction, content.ParentVersion ?? "", content.RelatedSkillIds ?? [],
                content.PublishedByAgentId ?? "", content.PublishedByWorkspaceId ?? "", content.PublishNote ?? "",
                content.ContentBytes, content.CreatedAt, content.SkillMarkdown);
        }, cancellationToken);

    public Task SaveSkillMetaAsync(string skillId, SkillHubMetaEdit edit, CancellationToken cancellationToken = default)
        => Hub("skills.library.meta", async (hub, token) =>
        {
            Require(await hub.UpdateMetaAsync(skillId, new UpdateHubSkillMetaRequest(
                edit.Name, edit.Summary, edit.Description, [.. edit.Tags], [.. edit.Keywords],
                edit.Status, edit.Visibility), token));
            return true;
        }, cancellationToken);

    public Task RetireSkillAsync(string skillId, CancellationToken cancellationToken = default)
        => Hub("skills.library.retire", async (hub, token) =>
        {
            Require(await hub.RetireAsync(skillId, token));
            return true;
        }, cancellationToken);

    public Task PublishVersionAsync(SkillHubVersionPublish publish, CancellationToken cancellationToken = default)
        => Hub("skills.library.publish-version", async (hub, token) =>
        {
            Require(await hub.PublishVersionAsync(publish.SkillId, new PublishHubSkillRequest(
                publish.SkillId, publish.Name, null, null, [.. publish.Tags], null,
                publish.Version, publish.SkillMarkdown, null,
                publish.EvolutionAction, Nullable(publish.ParentVersion), null, null, null,
                Nullable(publish.PublishNote), null, publish.Visibility), token));
            return true;
        }, cancellationToken);

    public Task RegisterInstallAsync(SkillHubInstallRegistration registration, CancellationToken cancellationToken = default)
        => Hub("skills.installs.register", async (hub, token) =>
        {
            Require(await hub.RegisterInstallAsync(new RegisterInstallRequest(registration.SkillId,
                registration.AgentInstanceId, Nullable(registration.WorkspaceId), registration.InstalledVersion,
                Nullable(registration.ContentHash), Nullable(registration.InstalledBy)), token));
            return true;
        }, cancellationToken);

    public Task<SkillHubEvoMap?> ReadLineageAsync(string skillId, CancellationToken cancellationToken = default)
        => Hub("skills.evolution.lineage", async (hub, token) =>
        {
            var map = await hub.GetSkillLineageAsync(skillId, token);
            return map is null ? null : Map(map);
        }, cancellationToken);

    public Task<SkillHubEvoMap> ReadGlobalLineageAsync(IReadOnlyList<string>? skillIds, int limit,
        CancellationToken cancellationToken = default)
        => Hub("skills.evolution.global", async (hub, token) =>
            Map(await hub.GetLineageAsync(skillIds, limit, token)), cancellationToken);

    public Task<IReadOnlyList<SkillHubInstall>> ListInstallsAsync(string? agentInstanceId, string? skillId,
        int page, int pageSize, CancellationToken cancellationToken = default)
        => Hub("skills.installs.list", async (hub, token) =>
        {
            var installs = await hub.ListInstallsAsync(agentInstanceId, skillId, page, pageSize, token);
            return (IReadOnlyList<SkillHubInstall>)installs.Select(Map).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<SkillHubUpdate>> ListUpdatesAsync(string agentInstanceId, CancellationToken cancellationToken = default)
        => Hub("skills.installs.updates", async (hub, token) =>
        {
            var updates = await hub.ListUpdatesAsync(agentInstanceId, token);
            return (IReadOnlyList<SkillHubUpdate>)updates.Select(update => new SkillHubUpdate(
                update.SkillId, update.Name, update.InstalledVersion, update.LatestVersion,
                update.LatestEvolutionAction, update.LatestPublishedAt, update.PublishNote ?? "")).ToArray();
        }, cancellationToken);

    private static SkillHubEvoMap Map(EvoMapDto map) => new(
        map.Nodes.Select(node => new EvoMapNode(node.NodeId, node.SkillId, node.Version, node.EvolutionAction,
            node.ParentNodeId ?? "", node.Name, node.Status, node.PublishedByAgentId ?? "", node.CreatedAt,
            node.ContentBytes, node.InstallCount)).ToArray(),
        map.Edges.Select(edge => new EvoMapEdge(edge.FromNodeId, edge.ToNodeId, edge.Action)).ToArray(),
        map.GeneratedAt);

    /// <summary>Core returns a semantic result instead of throwing; surface it as a real failure.</summary>
    private static void Require<T>(SkillHubResult<T> result) where T : class
    {
        if (!result.IsOk) throw new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static SkillHubSkillSummary Summary(HubSkillSummaryDto skill) => new(
        skill.SkillId, skill.Name, skill.Summary ?? "", skill.Description ?? "",
        skill.Tags ?? [], skill.Keywords ?? [],
        skill.LatestVersion, skill.Status, skill.Visibility,
        skill.VersionCount, skill.InstallCount, skill.PublishCount,
        skill.LatestContentHash ?? "", skill.CreatedAt, skill.UpdatedAt);

    private static SkillHubVersion Map(HubSkillVersionDto version) => new(
        version.SkillId, version.Version, version.ContentHash, version.EvolutionAction,
        version.ParentVersion ?? "", version.RelatedSkillIds ?? [],
        version.PublishedByAgentId ?? "", version.PublishedByWorkspaceId ?? "",
        version.PublishNote ?? "", version.ContentBytes, version.CreatedAt);

    private static SkillHubInstall Map(HubSkillInstallDto install) => new(
        install.SkillId, install.AgentInstanceId, install.WorkspaceId ?? "", install.InstalledVersion,
        install.ContentHash ?? "", install.InstalledBy, install.InstalledAt, install.UpdatedAt);
}
