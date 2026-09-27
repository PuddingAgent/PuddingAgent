using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-12 role slice: binds the RBAC card to Core's RoleService. Core refuses to modify or delete built-in
/// roles, and this adapter surfaces that refusal rather than hiding the controls incorrectly.
/// </summary>
internal sealed class DesktopRoleSettings(IDesktopKernel kernel) : IRoleSettings
{
    private Task<T> Roles<T>(string operationId,
        Func<RoleService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<RoleService>(), token), cancellationToken);

    public Task<IReadOnlyList<PermissionRole>> ListAsync(CancellationToken cancellationToken = default)
        => Roles("roles.list", async (service, token) =>
        {
            var roles = await service.ListAsync(token);
            return (IReadOnlyList<PermissionRole>)roles.Select(role => new PermissionRole(
                role.Id, role.RoleId, role.Name, role.Description ?? "",
                role.Permissions ?? [], role.IsSystemRole, role.CreatedAt)).ToArray();
        }, cancellationToken);

    public Task CreateAsync(RoleEdit create, CancellationToken cancellationToken = default)
        => Save("roles.create", create, isCreate: true, cancellationToken);

    public Task UpdateAsync(RoleEdit edit, CancellationToken cancellationToken = default)
        => Save("roles.update", edit, isCreate: false, cancellationToken);

    private Task Save(string operationId, RoleEdit edit, bool isCreate, CancellationToken cancellationToken)
        => Roles(operationId, async (service, token) =>
        {
            var draft = new RoleDraft(edit.RoleId.Trim(), edit.Name.Trim(), Nullable(edit.Description),
                RoleText.Normalize(edit.Permissions));
            var result = isCreate
                ? await service.CreateAsync(draft, token)
                : await service.UpdateAsync(edit.RoleId, draft, token);
            if (!result.IsOk)
            {
                // A system-role refusal is a business rule, not a validation slip; keep it distinct.
                throw result.Status == PuddingCode.Skills.SkillHubStatus.Conflict
                    ? new InvalidOperationException($"角色冲突：{result.Error}")
                    : new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
            }
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string roleId, CancellationToken cancellationToken = default)
        => Roles("roles.delete", async (service, token) =>
        {
            var result = await service.DeleteAsync(roleId, token);
            if (!result.IsOk) throw new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
            return true;
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
