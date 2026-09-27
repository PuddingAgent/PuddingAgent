using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Security;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.Security;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-11 access-token slice: binds the token cards to Core's ExternalAccessTokenService and the external API
/// policy provider.
///
/// Two boundaries are structural, not cosmetic: the plaintext token exists only in the create result, and the
/// stored secret hash is never mapped into the shell's model at all.
/// </summary>
internal sealed class DesktopAccessTokenSettings(IDesktopKernel kernel) : IAccessTokenSettings
{
    public Task<ExternalApiStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.status", (scope, _) =>
        {
            var options = scope.Services.GetRequiredService<ExternalTaskApiOptionsProvider>().Current;
            return Task.FromResult(new ExternalApiStatus(options.Enabled, options.PublicBaseUrl ?? "",
                options.RequireHttps, options.DefaultTokenLifetimeDays, options.MaxTokenLifetimeDays,
                options.MaxActiveTokensPerOwner));
        }, cancellationToken);

    public Task<AccessTokenListPage> ListAsync(AccessTokenFilter filter, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.list", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<ExternalAccessTokenService>();
            ExternalAccessTokenStatus? status = null;
            if (!string.IsNullOrWhiteSpace(filter.Status)
                && Enum.TryParse<ExternalAccessTokenStatus>(filter.Status, ignoreCase: true, out var parsed))
                status = parsed;

            var (items, total) = await service.ListAsync(new ExternalAccessTokenListFilter
            {
                Status = status,
                OwnerUserId = Nullable(filter.OwnerUserId),
                WorkspaceId = Nullable(filter.WorkspaceId),
                Scope = Nullable(filter.Scope),
                Page = Math.Max(1, filter.Page),
                PageSize = Math.Clamp(filter.PageSize, 1, 200),
            }, token);
            return new AccessTokenListPage(items.Select(Map).ToArray(), total,
                Math.Max(1, filter.Page), Math.Clamp(filter.PageSize, 1, 200));
        }, cancellationToken);

    public Task<AccessTokenCreated> CreateAsync(AccessTokenCreateRequest request, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.create", async (scope, token) =>
        {
            var result = await scope.Services.GetRequiredService<ExternalAccessTokenService>()
                .CreateAsync(new ExternalAccessTokenCreateCommand
                {
                    Name = request.Name.Trim(),
                    WorkspaceIds = [.. request.WorkspaceIds],
                    Scopes = [.. request.Scopes],
                    LifetimeDays = request.LifetimeDays,
                    // The HTTP surface takes the owner from the authenticated admin; the desktop is the
                    // single local operator, so the same local identity is used.
                    OwnerUserId = LocalDesktopIdentity.UserId,
                }, token);
            if (!result.IsOk)
                throw new InvalidOperationException(AccessTokenText.DescribeCreateError(result.Error.ToString()));
            return new AccessTokenCreated(Map(result.Value!.Item), result.Value.AccessToken);
        }, cancellationToken);

    public Task<AccessTokenSummary?> ReadAsync(string tokenId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.read", async (scope, token) =>
        {
            var record = await scope.Services.GetRequiredService<ExternalAccessTokenService>()
                .GetDetailAsync(tokenId, token);
            return record is null ? null : Map(record);
        }, cancellationToken);

    public Task RenameAsync(string tokenId, int expectedVersion, string name, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.rename", async (scope, token) =>
        {
            var result = await scope.Services.GetRequiredService<ExternalAccessTokenService>()
                .RenameAsync(tokenId, expectedVersion, name.Trim(), LocalDesktopIdentity.UserId, token);
            if (!result.IsOk) throw Management(result.Error);
            return true;
        }, cancellationToken);

    public Task RevokeAsync(string tokenId, int expectedVersion, string reason, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("accessTokens.revoke", async (scope, token) =>
        {
            var result = await scope.Services.GetRequiredService<ExternalAccessTokenService>()
                .RevokeAsync(tokenId, expectedVersion, LocalDesktopIdentity.UserId, reason, token);
            if (!result.IsOk) throw Management(result.Error);
            return true;
        }, cancellationToken);

    /// <summary>A stale version is a conflict the page must surface, not overwrite.</summary>
    private static Exception Management(ExternalAccessTokenManagementError? error) => error switch
    {
        ExternalAccessTokenManagementError.VersionConflict =>
            new SettingsConflictException("expectedVersion 与当前版本不一致，请刷新后重试。"),
        ExternalAccessTokenManagementError.NotFound => new InvalidOperationException("令牌不存在。"),
        ExternalAccessTokenManagementError.InvalidName => new ArgumentException("名称不合法。"),
        ExternalAccessTokenManagementError.InvalidReason => new ArgumentException("撤销原因不合法。"),
        _ => new InvalidOperationException("Core 拒绝了该操作。")
    };

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // SecretHash is intentionally absent from both mappers: the shell's model has no place to put it.
    private static AccessTokenSummary Map(ExternalAccessTokenListItem item) => new(
        item.TokenId, item.KeyId, item.DisplayPrefix, item.Name, item.OwnerUserId, item.Version,
        item.CreatedAtUtc, item.ExpiresAtUtc, item.RevokedAtUtc, item.RevokedByUserId ?? "",
        item.RevocationReason ?? "", item.LastUsedAtUtc, item.Scopes ?? [], item.Workspaces ?? [],
        item.Status.ToString());

    private static AccessTokenSummary Map(ExternalAccessTokenRecord record) => new(
        record.TokenId, record.KeyId, record.DisplayPrefix, record.Name, record.OwnerUserId, record.Version,
        record.CreatedAtUtc, record.ExpiresAtUtc, record.RevokedAtUtc, record.RevokedByUserId ?? "",
        record.RevocationReason ?? "", record.LastUsedAtUtc, record.Scopes ?? [], record.Workspaces ?? [],
        record.Status.ToString());
}
