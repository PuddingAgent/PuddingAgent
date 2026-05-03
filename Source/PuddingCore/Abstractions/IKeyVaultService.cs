namespace PuddingCode.Abstractions;

/// <summary>
/// KeyVault 业务抽象：负责密钥存储、加密解密、文本注入与脱敏。
/// </summary>
public interface IKeyVaultService
{
    Task<string> EncryptAsync(string plainText, CancellationToken ct = default);
    Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default);

    Task<KeyVaultSecretSummary> CreateSecretAsync(CreateKeyVaultSecretCommand request, CancellationToken ct = default);
    Task<KeyVaultSecretSummary?> UpdateSecretAsync(string keyVaultId, UpdateKeyVaultSecretCommand request, CancellationToken ct = default);
    Task<KeyVaultSecretDetail?> GetSecretAsync(string keyVaultId, bool includePlainText = false, CancellationToken ct = default);
    Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default);
    Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default);

    Task<string> InjectAsync(string text, CancellationToken ct = default);
    Task<string> StripAsync(string text, CancellationToken ct = default);
}

/// <summary>创建密钥参数。</summary>
public sealed record CreateKeyVaultSecretCommand
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = "general";
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>更新密钥参数。Value 为空表示不更新密钥值。</summary>
public sealed record UpdateKeyVaultSecretCommand
{
    public string Name { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? Description { get; init; }
    public string Category { get; init; } = "general";
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>密钥列表项（不包含明文）。</summary>
public sealed record KeyVaultSecretSummary
{
    public long Id { get; init; }
    public string KeyVaultId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = "general";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>密钥详情（可选包含明文）。</summary>
public sealed record KeyVaultSecretDetail
{
    public long Id { get; init; }
    public string KeyVaultId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = "general";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? Value { get; init; }
}
