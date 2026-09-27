using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-12 team slice: binds the teams card to Core's TeamService. Core's guards (no delete while workspaces
/// remain, no None whitelist level, protected default workspace) are surfaced rather than bypassed.
/// </summary>
internal sealed class DesktopTeamSettings(IDesktopKernel kernel) : ITeamSettings
{
    private Task<T> Teams<T>(string operationId,
        Func<TeamService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<TeamService>(), token), cancellationToken);

    public Task<IReadOnlyList<TeamSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Teams("teams.list", async (service, token) =>
        {
            var teams = await service.ListAsync(token);
            return (IReadOnlyList<TeamSummary>)teams.Select(team => new TeamSummary(team.Id, team.TeamId,
                team.Name, team.Description ?? "", team.IsEnabled, team.MemberCount, team.WorkspaceCount,
                team.CreatedAt)).ToArray();
        }, cancellationToken);

    public Task CreateAsync(TeamEdit create, CancellationToken cancellationToken = default)
        => Teams("teams.create", async (service, token) =>
        {
            Require(await service.CreateAsync(new TeamDraft(create.TeamId.Trim(), create.Name.Trim(),
                Nullable(create.Description), create.IsEnabled), token));
            return true;
        }, cancellationToken);

    public Task UpdateAsync(TeamEdit edit, CancellationToken cancellationToken = default)
        => Teams("teams.update", async (service, token) =>
        {
            Require(await service.UpdateAsync(edit.TeamId, new TeamDraft(edit.TeamId, edit.Name.Trim(),
                Nullable(edit.Description), edit.IsEnabled), token));
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string teamId, CancellationToken cancellationToken = default)
        => Teams("teams.delete", async (service, token) =>
        {
            Require(await service.DeleteAsync(teamId, token));
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<TeamMember>> ListMembersAsync(string teamId, CancellationToken cancellationToken = default)
        => Teams("teams.members.list", async (service, token) =>
        {
            var result = await service.ListMembersAsync(teamId, token);
            Require(result);
            return (IReadOnlyList<TeamMember>)result.Value!
                .Select(member => new TeamMember(member.UserId, member.Username, member.DisplayName ?? "", member.Role))
                .ToArray();
        }, cancellationToken);

    public Task AddMemberAsync(TeamMemberAdd add, CancellationToken cancellationToken = default)
        => Teams("teams.members.add", async (service, token) =>
        {
            Require(await service.AddMemberAsync(add.TeamId, add.UserId, add.Role, token));
            return true;
        }, cancellationToken);

    public Task RemoveMemberAsync(string teamId, string userId, CancellationToken cancellationToken = default)
        => Teams("teams.members.remove", async (service, token) =>
        {
            Require(await service.RemoveMemberAsync(teamId, userId, token));
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<TeamWorkspace>> ListWorkspacesAsync(string teamId, CancellationToken cancellationToken = default)
        => Teams("teams.workspaces.list", async (service, token) =>
        {
            var result = await service.ListWorkspacesAsync(teamId, token);
            Require(result);
            return (IReadOnlyList<TeamWorkspace>)result.Value!.Select(Map).ToArray();
        }, cancellationToken);

    public Task CreateWorkspaceAsync(TeamWorkspaceEdit create, CancellationToken cancellationToken = default)
        => Teams("teams.workspaces.create", async (service, token) =>
        {
            Require(await service.CreateWorkspaceAsync(create.TeamId, Draft(create), token));
            return true;
        }, cancellationToken);

    public Task UpdateWorkspaceAsync(TeamWorkspaceEdit edit, CancellationToken cancellationToken = default)
        => Teams("teams.workspaces.update", async (service, token) =>
        {
            Require(await service.UpdateWorkspaceAsync(edit.WorkspaceId, Draft(edit), token));
            return true;
        }, cancellationToken);

    public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Teams("teams.workspaces.delete", async (service, token) =>
        {
            Require(await service.DeleteWorkspaceAsync(workspaceId, token));
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<TeamWhitelistEntry>> ListWorkspaceMembersAsync(
        string workspaceId, CancellationToken cancellationToken = default)
        => Teams("teams.workspaceMembers.list", async (service, token) =>
        {
            var result = await service.ListWorkspaceMembersAsync(workspaceId, token);
            Require(result);
            return (IReadOnlyList<TeamWhitelistEntry>)result.Value!
                .Select(member => new TeamWhitelistEntry(member.Id, member.UserId, member.Username,
                    member.DisplayName ?? "", member.AccessLevel)).ToArray();
        }, cancellationToken);

    public Task AddWorkspaceMemberAsync(TeamWorkspaceMemberAdd add, CancellationToken cancellationToken = default)
        => Teams("teams.workspaceMembers.add", async (service, token) =>
        {
            Require(await service.AddWorkspaceMemberAsync(add.WorkspaceId, add.UserId, add.AccessLevel, token));
            return true;
        }, cancellationToken);

    public Task RemoveWorkspaceMemberAsync(string workspaceId, int memberId, CancellationToken cancellationToken = default)
        => Teams("teams.workspaceMembers.remove", async (service, token) =>
        {
            Require(await service.RemoveWorkspaceMemberAsync(workspaceId, memberId, token));
            return true;
        }, cancellationToken);

    private static TeamWorkspaceDraft Draft(TeamWorkspaceEdit edit) => new(
        edit.WorkspaceId.Trim(), edit.Name.Trim(), Nullable(edit.Description), Nullable(edit.UserProfile),
        edit.TeamAccessPolicy, edit.CompanyAccessPolicy, edit.IsEnabled);

    private static TeamWorkspace Map(PuddingPlatform.Data.Dtos.WorkspaceWithPermDto workspace) => new(
        workspace.Id, workspace.WorkspaceId, workspace.Slug, workspace.TeamId, workspace.TeamName,
        workspace.Name, workspace.Description ?? "", workspace.UserProfile ?? "",
        workspace.TeamAccessPolicy, workspace.CompanyAccessPolicy,
        workspace.IsEnabled, workspace.IsFrozen, workspace.MemberCount, workspace.CreatedAt);

    /// <summary>A conflict stays distinguishable for the caller.</summary>
    private static void Require<T>(PuddingCode.Skills.SkillHubResult<T> result) where T : class
    {
        if (result.IsOk) return;
        throw result.Status == PuddingCode.Skills.SkillHubStatus.Conflict
            ? new InvalidOperationException($"冲突：{result.Error}")
            : new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
