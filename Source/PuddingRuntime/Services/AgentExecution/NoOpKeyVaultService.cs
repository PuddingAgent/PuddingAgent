using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Keeps the execution engine usable in hosts that do not provide a key vault.
/// The fallback is intentionally internal and contains no execution-loop policy.
/// </summary>
internal sealed class NoOpKeyVaultService : IKeyVaultService
{
    public static NoOpKeyVaultService Instance { get; } = new();

    private NoOpKeyVaultService()
    {
    }

    public Task<string> EncryptAsync(string plainText, CancellationToken ct = default)
        => Task.FromResult(plainText);

    public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default)
        => Task.FromResult(encryptedValue);

    public Task<KeyVaultSecretSummary> CreateSecretAsync(
        CreateKeyVaultSecretCommand request,
        CancellationToken ct = default)
        => Task.FromResult(new KeyVaultSecretSummary());

    public Task<KeyVaultSecretSummary?> UpdateSecretAsync(
        string keyVaultId,
        UpdateKeyVaultSecretCommand request,
        CancellationToken ct = default)
        => Task.FromResult<KeyVaultSecretSummary?>(null);

    public Task<KeyVaultSecretDetail?> GetSecretAsync(
        string keyVaultId,
        bool includePlainText = false,
        CancellationToken ct = default)
        => Task.FromResult<KeyVaultSecretDetail?>(null);

    public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>([]);

    public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string> InjectAsync(string text, CancellationToken ct = default)
        => Task.FromResult(text);

    public Task<string> StripAsync(string text, CancellationToken ct = default)
        => Task.FromResult(text);
}
