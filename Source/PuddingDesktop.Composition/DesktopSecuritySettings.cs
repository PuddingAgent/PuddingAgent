using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-10 vault and classifier-health slice.
///
/// The vault is write-only from the settings panel: this adapter asks Core for summaries only and never
/// requests the plaintext. Classifier health is an optional Core surface, so a missing reporter is reported
/// as "not wired" rather than as healthy.
/// </summary>
internal sealed class DesktopSecuritySettings(IDesktopKernel kernel) : ISecuritySettings
{
    public Task<IReadOnlyList<VaultSecret>> ListSecretsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.list", async (scope, token) =>
        {
            var secrets = await scope.Services.GetRequiredService<IKeyVaultService>().ListSecretsAsync(token);
            return (IReadOnlyList<VaultSecret>)secrets.Select(secret => new VaultSecret(
                secret.Id, secret.KeyVaultId, secret.Name, secret.Description ?? "", secret.Category,
                secret.Tags ?? [], secret.CreatedAt, secret.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task SaveSecretAsync(VaultSecretEdit edit, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.save", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<IKeyVaultService>();
            if (edit.IsCreate)
            {
                await service.CreateSecretAsync(new CreateKeyVaultSecretCommand
                {
                    Name = edit.Name.Trim(),
                    Value = edit.Value,
                    Description = Nullable(edit.Description),
                    Category = edit.Category,
                    Tags = [.. edit.Tags],
                }, token);
                return true;
            }

            // A blank value means "keep the stored secret"; Core treats it that way, so pass null.
            var updated = await service.UpdateSecretAsync(edit.KeyVaultId, new UpdateKeyVaultSecretCommand
            {
                Name = edit.Name.Trim(),
                Value = string.IsNullOrWhiteSpace(edit.Value) ? null : edit.Value,
                Description = Nullable(edit.Description),
                Category = edit.Category,
                Tags = [.. edit.Tags],
            }, token);
            if (updated is null) throw new InvalidOperationException($"密钥 '{edit.KeyVaultId}' 不存在。");
            return true;
        }, cancellationToken);

    public Task DeleteSecretAsync(string keyVaultId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.delete", async (scope, token) =>
        {
            if (!await scope.Services.GetRequiredService<IKeyVaultService>().DeleteSecretAsync(keyVaultId, token))
                throw new InvalidOperationException($"密钥 '{keyVaultId}' 未删除：Core 返回了否。");
            return true;
        }, cancellationToken);

    public Task<ClassifierHealthReport> ReadClassifierHealthAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.classifierHealth", (scope, _) =>
        {
            // Optional surface: absent means unknown, never a fabricated healthy state.
            var reporter = scope.Services.GetService<IClassifierHealthReporter>();
            if (reporter is null) return Task.FromResult(ClassifierHealthReport.NotConfigured);
            var snapshot = reporter.Snapshot();
            return Task.FromResult(new ClassifierHealthReport(true,
                snapshot.Select(status => new ClassifierStatusEntry(status.ClassifierId,
                    status.Health.ToString().ToLowerInvariant(), status.Detail ?? "",
                    status.ConsecutiveFailures, status.LastCheckedAtUtc, status.LastLatencyMs)).ToArray()));
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
