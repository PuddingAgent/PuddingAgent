using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-05 workspace slice: binds the workspace tab to the shared WorkspaceService (the operation the Web
/// controller also uses). Team and user lookups are read-only projections used to fill the pickers.
/// </summary>
internal sealed class DesktopWorkspaceSettings(IDesktopKernel kernel) : IWorkspaceSettings
{
    private Task<T> Workspaces<T>(string operationId,
        Func<WorkspaceService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<WorkspaceService>(), token), cancellationToken);

    public Task<IReadOnlyList<WorkspaceSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Workspaces("workspaces.list", async (service, token) =>
        {
            var workspaces = await service.ListAsync(token);
            return (IReadOnlyList<WorkspaceSummary>)workspaces.Select(Map).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceTeamOption>> ListTeamsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("workspaces.teams.list", async (scope, token) =>
        {
            var teams = await scope.Services.GetRequiredService<PlatformDbContext>().Teams.AsNoTracking()
                .OrderBy(team => team.Id).ToListAsync(token);
            return (IReadOnlyList<WorkspaceTeamOption>)teams
                .Select(team => new WorkspaceTeamOption(team.TeamId, team.Name)).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceUserOption>> ListUsersAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("workspaces.users.list", async (scope, token) =>
        {
            var users = await scope.Services.GetRequiredService<PlatformDbContext>().AppUsers.AsNoTracking()
                .OrderBy(user => user.Id).ToListAsync(token);
            return (IReadOnlyList<WorkspaceUserOption>)users
                .Select(user => new WorkspaceUserOption(user.UserId, user.Username, user.DisplayName ?? "")).ToArray();
        }, cancellationToken);

    public Task CreateAsync(WorkspaceCreateRequest create, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.create", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.CreateAsync(new WorkspaceCreateDraft(
                create.WorkspaceId.Trim(), create.TeamId, create.Name.Trim(), create.Description,
                create.TeamAccessPolicy, create.CompanyAccessPolicy, Nullable(create.UserProfile)), token));
            return true;
        }, cancellationToken);

    public Task SaveAsync(WorkspaceEdit edit, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.save", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.UpdateAsync(edit.WorkspaceId, new WorkspaceMetaUpdate(
                edit.Name.Trim(), edit.Description, edit.TeamAccessPolicy, edit.CompanyAccessPolicy,
                edit.IsEnabled, Nullable(edit.UserProfile)), token));
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.delete", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.DeleteAsync(workspaceId, token));
            return true;
        }, cancellationToken);

    public Task SetFrozenAsync(string workspaceId, bool frozen, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.freeze", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.SetFrozenAsync(workspaceId, frozen, token));
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.members.list", async (service, token) =>
        {
            var result = await service.ListMembersAsync(workspaceId, token);
            DesktopSkillHubSettings.Require(result);
            return (IReadOnlyList<WorkspaceMember>)result.Value!
                .Select(member => new WorkspaceMember(member.Id, member.UserId, member.Username,
                    member.DisplayName ?? "", member.AccessLevel)).ToArray();
        }, cancellationToken);

    public Task AddMemberAsync(string workspaceId, string userId, string accessLevel, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.members.add", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.AddMemberAsync(workspaceId, userId, accessLevel, token));
            return true;
        }, cancellationToken);

    public Task RemoveMemberAsync(string workspaceId, int memberId, CancellationToken cancellationToken = default)
        => Workspaces("workspaces.members.remove", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.RemoveMemberAsync(workspaceId, memberId, token));
            return true;
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static WorkspaceSummary Map(PuddingPlatform.Data.Dtos.WorkspaceWithPermDto workspace) => new(
        workspace.WorkspaceId, workspace.Slug, workspace.TeamId, workspace.TeamName, workspace.Name,
        workspace.Description ?? "", workspace.UserProfile ?? "",
        workspace.TeamAccessPolicy, workspace.CompanyAccessPolicy,
        workspace.IsEnabled, workspace.IsFrozen, workspace.MemberCount, workspace.CreatedAt);
}
