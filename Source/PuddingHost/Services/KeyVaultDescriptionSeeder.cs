using System.Text.Json;
using PuddingHost.Hosting;
using PuddingCode.Abstractions;

namespace PuddingAgent.Services;

/// <summary>
/// Applies human-readable descriptions from an optional mapping file
/// (<c>&lt;DataRoot&gt;/config/keyvault.descriptions.json</c>, name → description).
///
/// Why this exists: the credential migration named every secret after its config
/// path and wrote a description that only repeated that path, so the admin page
/// could not answer "what is this key for?". A description is data, not a secret:
/// this seeder never reads and never writes a secret value, and it never logs one.
///
/// The step is idempotent — entries whose description already matches are skipped,
/// and a missing mapping file is a no-op — so it is safe to run on every start.
/// </summary>
public sealed class KeyVaultDescriptionSeeder(
    IKeyVaultService vault,
    PuddingHostOptions options,
    ILogger<KeyVaultDescriptionSeeder> logger) : IHostedService
{
    /// <summary>Relative to the data root; mapping is written by an operator, not generated.</summary>
    public const string MappingRelativePath = "config/keyvault.descriptions.json";

    public async Task StartAsync(CancellationToken ct)
    {
        var path = Path.Combine(
            options.DataRoot,
            MappingRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            logger.LogDebug("[KeyVault] No description mapping at {Path}; nothing to seed.", path);
            return;
        }

        Dictionary<string, string> mapping;
        try
        {
            await using var stream = File.OpenRead(path);
            mapping = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
                      ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            // A broken mapping must never block startup.
            logger.LogWarning(ex, "[KeyVault] Description mapping at {Path} is unreadable; skipping.", path);
            return;
        }

        if (mapping.Count == 0)
        {
            logger.LogInformation("[KeyVault] Description mapping at {Path} is empty; nothing to seed.", path);
            return;
        }

        var secrets = await vault.ListSecretsAsync(ct);
        var applied = 0;
        var unchanged = 0;
        var unknown = 0;

        foreach (var secret in secrets)
        {
            if (!mapping.TryGetValue(secret.Name, out var description)) continue;

            if (string.Equals(secret.Description, description, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var updated = await vault.UpdateSecretAsync(secret.KeyVaultId, new UpdateKeyVaultSecretCommand
            {
                Name = secret.Name,
                Description = description,
                Category = secret.Category,
                Tags = secret.Tags.ToList(),
                // Value 留空 = 保持原密文不变；本步骤从不接触密钥值。
            }, ct);

            if (updated is not null) applied++;
        }

        foreach (var name in mapping.Keys)
        {
            if (!secrets.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal))) unknown++;
        }

        logger.LogInformation(
            "[KeyVault] Description seed complete: applied={Applied} unchanged={Unchanged} mappingEntries={Entries} unknown={Unknown}.",
            applied, unchanged, mapping.Count, unknown);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
