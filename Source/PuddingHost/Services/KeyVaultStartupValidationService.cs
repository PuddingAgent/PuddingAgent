using PuddingCode.Abstractions;

namespace PuddingAgent.Services;

/// <summary>Readiness requires every stored credential to be decryptable; no plaintext is logged.</summary>
public sealed class KeyVaultStartupValidationService(IKeyVaultService vault, ILogger<KeyVaultStartupValidationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var secrets = await vault.ListSecretsAsync(ct);
        foreach (var secret in secrets)
            _ = await vault.GetSecretAsync(secret.KeyVaultId, includePlainText: true, ct);
        logger.LogInformation("[KeyVault] Startup decryption check passed for {Count} credentials.", secrets.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
