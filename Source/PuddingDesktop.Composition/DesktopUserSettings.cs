using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-12 user slice: binds the account card to Core's UserService. Passwords travel one way only - the
/// adapter never maps a password or hash back into the shell's model.
/// </summary>
internal sealed class DesktopUserSettings(IDesktopKernel kernel) : IUserSettings
{
    private Task<T> Users<T>(string operationId,
        Func<UserService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<UserService>(), token), cancellationToken);

    public Task<IReadOnlyList<AppUserAccount>> ListAsync(CancellationToken cancellationToken = default)
        => Users("users.list", async (service, token) =>
        {
            var users = await service.ListAsync(token);
            return (IReadOnlyList<AppUserAccount>)users.Select(user => new AppUserAccount(
                user.Id, user.UserId, user.Username, user.Email, user.DisplayName ?? "", user.UserType,
                user.IsEnabled, user.RoleIds ?? [], user.CreatedAt, user.Avatar ?? "")).ToArray();
        }, cancellationToken);

    public Task CreateAsync(UserCreate create, CancellationToken cancellationToken = default)
        => Users("users.create", async (service, token) =>
        {
            Require(await service.CreateAsync(new UserDraft(create.UserId.Trim(), create.Username.Trim(),
                create.Email.Trim(), Nullable(create.DisplayName), create.UserType, create.Password), token));
            return true;
        }, cancellationToken);

    public Task UpdateAsync(UserMetaEdit edit, CancellationToken cancellationToken = default)
        => Users("users.update", async (service, token) =>
        {
            Require(await service.UpdateAsync(edit.UserId, new UserMetaUpdate(edit.Username.Trim(),
                edit.Email.Trim(), Nullable(edit.DisplayName), edit.UserType, edit.IsEnabled), token));
            return true;
        }, cancellationToken);

    public Task ChangePasswordAsync(UserPasswordChange change, CancellationToken cancellationToken = default)
        => Users("users.password", async (service, token) =>
        {
            Require(await service.ChangePasswordAsync(change.UserId, change.NewPassword, token));
            return true;
        }, cancellationToken);

    public Task AssignRolesAsync(string userId, IReadOnlyList<string> roleIds, CancellationToken cancellationToken = default)
        => Users("users.roles", async (service, token) =>
        {
            Require(await service.AssignRolesAsync(userId, [.. roleIds], token));
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
        => Users("users.delete", async (service, token) =>
        {
            Require(await service.DeleteAsync(userId, token));
            return true;
        }, cancellationToken);

    /// <summary>A conflict stays distinguishable so the page can say what actually collided.</summary>
    private static void Require<T>(PuddingCode.Skills.SkillHubResult<T> result) where T : class
    {
        if (result.IsOk) return;
        throw result.Status == PuddingCode.Skills.SkillHubStatus.Conflict
            ? new InvalidOperationException($"冲突：{result.Error}")
            : new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
