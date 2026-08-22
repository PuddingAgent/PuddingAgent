using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Security;

public enum ExternalAccessTokenAuthFailureReason
{
    Malformed,
    UnknownKey,
    BadSecret,
    Revoked,
    Expired,
    OwnerDisabled,
}

public sealed record ExternalAccessTokenPrincipal
{
    public required string TokenId { get; init; }
    public required string KeyId { get; init; }
    public required string Name { get; init; }
    public required string OwnerUserId { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required IReadOnlyList<string> Workspaces { get; init; }
}

/// <summary>认证结果：成功携带 principal；失败只携带原因类别（对外统一 401 invalid_token）。</summary>
public sealed record ExternalAccessTokenAuthOutcome(
    ExternalAccessTokenPrincipal? Principal,
    ExternalAccessTokenAuthFailureReason? FailureReason,
    string? KnownKeyId)
{
    public static ExternalAccessTokenAuthOutcome Success(ExternalAccessTokenPrincipal principal)
        => new(principal, null, principal.KeyId);

    public static ExternalAccessTokenAuthOutcome Failure(
        ExternalAccessTokenAuthFailureReason reason,
        string? knownKeyId = null)
        => new(null, reason, knownKeyId);
}

public sealed record ExternalAccessTokenCreateCommand
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> WorkspaceIds { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    /// <summary>null = 使用配置默认；1..MaxTokenLifetimeDays。</summary>
    public int? LifetimeDays { get; init; }
    public required string OwnerUserId { get; init; }
}

public sealed record ExternalAccessTokenCreateResult(
    ExternalAccessTokenListItem Item,
    /// <summary>canonical token 明文；仅出现在创建响应一次，此后不可恢复。</summary>
    string AccessToken);

public enum ExternalAccessTokenCreateError
{
    InvalidName,
    NoScopes,
    UnknownScope,
    NoWorkspaces,
    UnknownWorkspace,
    LifetimeOutOfRange,
    TooManyActiveTokens,
}

public enum ExternalAccessTokenManagementError
{
    NotFound,
    VersionConflict,
    InvalidName,
    InvalidReason,
}

/// <summary>轻量结果类型（服务层错误码语义，避免异常控制流）。</summary>
public sealed record Result<TOk, TError>
{
    public required bool IsOk { get; init; }
    public TOk? Value { get; init; }
    public TError? Error { get; init; }

    public static Result<TOk, TError> Success(TOk value) => new() { IsOk = true, Value = value };

    public static Result<TOk, TError> Failure(TError error) => new() { IsOk = false, Error = error };
}

/// <summary>
/// ADR-075: External Access Token 领域服务 — RNG 生成、生命周期规则、认证验证与管理命令。
/// 数据库只保存 canonical token 的 SHA-256 摘要；摘要比较使用 FixedTimeEquals。
/// authentication_failed 审计按 (keyId, reason) 节流（未知 keyId 不写审计，走限速日志，
/// 保证审计写入量以真实 Token 数为上界）。
/// </summary>
public sealed class ExternalAccessTokenService(
    ExternalAccessTokenStore store,
    ExternalTaskApiOptionsProvider optionsProvider,
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<ExternalAccessTokenService> logger)
{
    /// <summary>canonical = pdt_v1_&lt;keyId(Base64Url 16B)&gt;.&lt;secret(Base64Url 32B)&gt;。</summary>
    public const string TokenPrefix = ExternalAccessTokenDefaults.TokenPrefix;
    public const string TokenIdPrefix = "pat_";

    private static readonly TimeSpan AuthFailureAuditThrottle = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<(string KeyId, string Reason), DateTimeOffset> _lastFailureAuditAt = new();

    /// <summary>创建 Token。明文只在返回值中出现一次；持久层只落摘要。</summary>
    public async Task<Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>> CreateAsync(
        ExternalAccessTokenCreateCommand command,
        CancellationToken ct = default)
    {
        var options = optionsProvider.Current;

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.InvalidName);

        var scopes = command.Scopes.Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.NoScopes);
        if (scopes.Any(s => !ExternalTaskApiScopes.IsValid(s)))
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.UnknownScope);

        var workspaceIds = command.WorkspaceIds
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (workspaceIds.Count == 0)
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.NoWorkspaces);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var known = await db.Workspaces.AsNoTracking()
                .Where(w => workspaceIds.Contains(w.WorkspaceId))
                .Select(w => w.WorkspaceId)
                .ToListAsync(ct);
            if (known.Count != workspaceIds.Count)
                return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.UnknownWorkspace);
        }

        var lifetimeDays = command.LifetimeDays ?? options.DefaultTokenLifetimeDays;
        if (lifetimeDays < 1 || lifetimeDays > options.MaxTokenLifetimeDays)
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.LifetimeOutOfRange);

        var activeCount = await store.CountActiveAsync(command.OwnerUserId, ct);
        if (activeCount >= options.MaxActiveTokensPerOwner)
            return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Failure(ExternalAccessTokenCreateError.TooManyActiveTokens);

        var (canonicalToken, record) = GenerateToken(name, command.OwnerUserId, scopes, workspaceIds, lifetimeDays);
        await store.CreateAsync(record, ct);

        logger.LogInformation(
            "[ExternalAccessToken] Created tokenId={TokenId} keyId={KeyId} owner={Owner} scopes={Scopes} workspaces={Workspaces} expires={Expires}",
            record.TokenId, record.KeyId, record.OwnerUserId,
            string.Join(',', record.Scopes), string.Join(',', record.Workspaces), record.ExpiresAtUtc);

        var item = new ExternalAccessTokenListItem
        {
            TokenId = record.TokenId,
            KeyId = record.KeyId,
            DisplayPrefix = record.DisplayPrefix,
            Name = record.Name,
            OwnerUserId = record.OwnerUserId,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            RevokedAtUtc = null,
            RevokedByUserId = null,
            RevocationReason = null,
            LastUsedAtUtc = null,
            Scopes = record.Scopes,
            Workspaces = record.Workspaces,
            Status = ExternalAccessTokenStatus.Active,
        };
        return Result<ExternalAccessTokenCreateResult, ExternalAccessTokenCreateError>.Success(
            new ExternalAccessTokenCreateResult(item, canonicalToken));
    }

    /// <summary>
    /// 验证 canonical token：格式 → keyId 索引 → 固定时间摘要比较 → revoked/expired/owner。
    /// 失败时对外统一 invalid_token；已知 Token 的失败按原因写入节流审计。
    /// </summary>
    public async Task<ExternalAccessTokenAuthOutcome> ValidateAsync(
        string? presentedToken,
        CancellationToken ct = default)
    {
        if (!TryParse(presentedToken, out var keyId, out var canonical))
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.Malformed);

        var record = await store.FindByKeyIdAsync(keyId, ct);
        if (record is null)
        {
            logger.LogDebug("[ExternalAccessToken] Unknown keyId rejected");
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.UnknownKey);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        if (digest.Length != record.SecretHash.Length
            || !CryptographicOperations.FixedTimeEquals(digest, record.SecretHash))
        {
            await AppendThrottledFailureAuditAsync(record, ExternalAccessTokenAuthFailureReason.BadSecret);
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.BadSecret, record.KeyId);
        }

        if (record.RevokedAtUtc is not null)
        {
            await AppendThrottledFailureAuditAsync(record, ExternalAccessTokenAuthFailureReason.Revoked);
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.Revoked, record.KeyId);
        }

        if (record.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await AppendThrottledFailureAuditAsync(record, ExternalAccessTokenAuthFailureReason.Expired);
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.Expired, record.KeyId);
        }

        if (!record.OwnerEnabled)
        {
            await AppendThrottledFailureAuditAsync(record, ExternalAccessTokenAuthFailureReason.OwnerDisabled);
            return ExternalAccessTokenAuthOutcome.Failure(ExternalAccessTokenAuthFailureReason.OwnerDisabled, record.KeyId);
        }

        return ExternalAccessTokenAuthOutcome.Success(new ExternalAccessTokenPrincipal
        {
            TokenId = record.TokenId,
            KeyId = record.KeyId,
            Name = record.Name,
            OwnerUserId = record.OwnerUserId,
            Scopes = record.Scopes,
            Workspaces = record.Workspaces,
        });
    }

    public Task<ExternalAccessTokenRecord?> GetDetailAsync(string tokenId, CancellationToken ct = default)
        => store.FindByTokenIdAsync(tokenId, ct);

    public Task<(IReadOnlyList<ExternalAccessTokenListItem> Items, int Total)> ListAsync(
        ExternalAccessTokenListFilter filter,
        CancellationToken ct = default)
        => store.ListAsync(filter, ct);

    public async Task<Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>> RenameAsync(
        string tokenId,
        int expectedVersion,
        string newName,
        string actorUserId,
        CancellationToken ct = default)
    {
        var name = newName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
            return Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.InvalidName);

        var result = await store.RenameAsync(tokenId, expectedVersion, name, actorUserId, ct);
        if (result != ExternalAccessTokenMutationResult.Ok)
            return MapMutationError(result);

        var record = await store.FindByTokenIdAsync(tokenId, ct);
        return record is null
            ? Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.NotFound)
            : Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Success(ToListItem(record));
    }

    public async Task<Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>> RevokeAsync(
        string tokenId,
        int expectedVersion,
        string revokedByUserId,
        string? reason,
        CancellationToken ct = default)
    {
        if (reason is { Length: > 500 })
            return Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.InvalidReason);

        var result = await store.RevokeAsync(tokenId, expectedVersion, revokedByUserId, reason?.Trim(), ct);
        if (result != ExternalAccessTokenMutationResult.Ok)
            return MapMutationError(result);

        var record = await store.FindByTokenIdAsync(tokenId, ct);
        return record is null
            ? Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.NotFound)
            : Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Success(ToListItem(record));
    }

    /// <summary>解析 canonical token；解析失败一律 Malformed（不区分原因，避免信息泄露）。</summary>
    public static bool TryParse(string? presentedToken, out string keyId, out string canonical)
    {
        keyId = string.Empty;
        canonical = string.Empty;

        if (string.IsNullOrEmpty(presentedToken)
            || presentedToken.Length > ExternalAccessTokenDefaults.MaxCanonicalLength
            || !presentedToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = presentedToken.IndexOf('.');
        if (separatorIndex <= TokenPrefix.Length || separatorIndex == presentedToken.Length - 1)
            return false;

        keyId = presentedToken[TokenPrefix.Length..separatorIndex];
        var secret = presentedToken[(separatorIndex + 1)..];
        if (!IsValidBase64Url(keyId) || !IsValidBase64Url(secret))
            return false;

        canonical = presentedToken;
        return true;
    }

    private (string Canonical, ExternalAccessTokenRecord Record) GenerateToken(
        string name,
        string ownerUserId,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> workspaces,
        int lifetimeDays)
    {
        var keyId = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var canonical = $"{TokenPrefix}{keyId}.{secret}";
        var now = DateTimeOffset.UtcNow;

        return (canonical, new ExternalAccessTokenRecord
        {
            TokenId = $"{TokenIdPrefix}{Base64UrlEncode(RandomNumberGenerator.GetBytes(12))}",
            KeyId = keyId,
            SecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical)),
            DisplayPrefix = $"{TokenPrefix}{keyId[..8]}…",
            Name = name,
            OwnerUserId = ownerUserId,
            Version = 1,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(lifetimeDays),
            RevokedAtUtc = null,
            RevokedByUserId = null,
            RevocationReason = null,
            LastUsedAtUtc = null,
            Scopes = scopes.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            Workspaces = workspaces.OrderBy(w => w, StringComparer.Ordinal).ToList(),
            OwnerEnabled = true,
        });
    }

    private async Task AppendThrottledFailureAuditAsync(
        ExternalAccessTokenRecord record,
        ExternalAccessTokenAuthFailureReason reason)
    {
        var gateKey = (record.KeyId, reason.ToString());
        var now = DateTimeOffset.UtcNow;
        if (_lastFailureAuditAt.TryGetValue(gateKey, out var lastAt) && now - lastAt < AuthFailureAuditThrottle)
            return;

        _lastFailureAuditAt[gateKey] = now;
        await store.AppendAuditAsync(
            record.TokenId,
            record.KeyId,
            ExternalAccessTokenAuditEventType.AuthenticationFailed,
            reason: reason.ToString());
        logger.LogWarning(
            "[ExternalAccessToken] Auth failed tokenId={TokenId} reason={Reason}",
            record.TokenId, reason);
    }

    private static Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError> MapMutationError(
        ExternalAccessTokenMutationResult result)
        => result switch
        {
            ExternalAccessTokenMutationResult.NotFound
                => Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.NotFound),
            ExternalAccessTokenMutationResult.VersionConflict
                => Result<ExternalAccessTokenListItem, ExternalAccessTokenManagementError>.Failure(ExternalAccessTokenManagementError.VersionConflict),
            _ => throw new UnreachableException($"Unhandled mutation result: {result}"),
        };

    private static ExternalAccessTokenListItem ToListItem(ExternalAccessTokenRecord record)
        => new()
        {
            TokenId = record.TokenId,
            KeyId = record.KeyId,
            DisplayPrefix = record.DisplayPrefix,
            Name = record.Name,
            OwnerUserId = record.OwnerUserId,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            RevokedAtUtc = record.RevokedAtUtc,
            RevokedByUserId = record.RevokedByUserId,
            RevocationReason = record.RevocationReason,
            LastUsedAtUtc = record.LastUsedAtUtc,
            Scopes = record.Scopes,
            Workspaces = record.Workspaces,
            Status = record.Status,
        };

    private static bool IsValidBase64Url(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
