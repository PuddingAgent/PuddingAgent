using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// KeyVault 服务：
/// - 使用 AES-256-GCM 加解密密钥值；
/// - 密钥主密钥从环境变量读取（缺失时进程内自动生成）；
/// - 提供密钥管理、占位符注入和明文脱敏能力。
/// </summary>
public sealed class KeyVaultService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<KeyVaultService> logger) : IKeyVaultService
{
    private const string MasterKeyEnvName = "PUDDING_KEYVAULT_MASTER_KEY";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private static readonly Regex VaultPlaceholderRegex =
        new(@"\{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}", RegexOptions.Compiled);

    private static readonly Regex SecretNameRegex =
        new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    private readonly byte[] _masterKey = ResolveMasterKey(logger);

    public Task<string> EncryptAsync(string plainText, CancellationToken ct = default)
    {
        if (plainText is null)
            throw new ArgumentNullException(nameof(plainText));

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tagBytes = new byte[TagSizeBytes];

        using var aes = new AesGcm(_masterKey, TagSizeBytes);
        aes.Encrypt(nonce, plaintextBytes, cipherBytes, tagBytes);

        var payload = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
        Buffer.BlockCopy(tagBytes, 0, payload, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, payload, NonceSizeBytes + TagSizeBytes, cipherBytes.Length);

        return Task.FromResult(Convert.ToBase64String(payload));
    }

    public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
            throw new ArgumentException("密文字段不能为空。", nameof(encryptedValue));

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encryptedValue);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("密文格式非法（非 Base64）。", ex);
        }

        if (payload.Length < NonceSizeBytes + TagSizeBytes)
            throw new InvalidOperationException("密文格式非法（长度不足）。");

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var ciphertext = new byte[payload.Length - NonceSizeBytes - TagSizeBytes];

        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(payload, NonceSizeBytes, tag, 0, TagSizeBytes);
        Buffer.BlockCopy(payload, NonceSizeBytes + TagSizeBytes, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_masterKey, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("密钥解密失败，请检查主密钥是否正确。", ex);
        }

        return Task.FromResult(Encoding.UTF8.GetString(plaintext));
    }

    public async Task<KeyVaultSecretSummary> CreateSecretAsync(CreateKeyVaultSecretCommand request, CancellationToken ct = default)
    {
        ValidateName(request.Name);
        if (string.IsNullOrWhiteSpace(request.Value))
            throw new InvalidOperationException("密钥值不能为空。");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var exists = await db.KeyVaults.AnyAsync(x => x.Name == request.Name, ct);
        if (exists)
            throw new InvalidOperationException($"名称为 '{request.Name}' 的密钥已存在。");

        var encrypted = await EncryptAsync(request.Value, ct);
        var entity = new KeyVaultEntity
        {
            KeyVaultId = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            Category = NormalizeCategory(request.Category),
            EncryptedValue = encrypted,
            Tags = SerializeTags(request.Tags),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = null,
        };

        db.KeyVaults.Add(entity);
        await db.SaveChangesAsync(ct);

        return ToSummary(entity);
    }

    public async Task<KeyVaultSecretSummary?> UpdateSecretAsync(
        string keyVaultId,
        UpdateKeyVaultSecretCommand request,
        CancellationToken ct = default)
    {
        ValidateName(request.Name);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyVaults.FirstOrDefaultAsync(x => x.KeyVaultId == keyVaultId, ct);
        if (entity is null) return null;

        var duplicateName = await db.KeyVaults
            .AnyAsync(x => x.KeyVaultId != keyVaultId && x.Name == request.Name, ct);
        if (duplicateName)
            throw new InvalidOperationException($"名称为 '{request.Name}' 的密钥已存在。");

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Category = NormalizeCategory(request.Category);
        entity.Tags = SerializeTags(request.Tags);

        if (!string.IsNullOrWhiteSpace(request.Value))
            entity.EncryptedValue = await EncryptAsync(request.Value, ct);

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ToSummary(entity);
    }

    public async Task<KeyVaultSecretDetail?> GetSecretAsync(
        string keyVaultId,
        bool includePlainText = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyVaults.AsNoTracking()
            .FirstOrDefaultAsync(x => x.KeyVaultId == keyVaultId, ct);
        if (entity is null) return null;

        string? value = null;
        if (includePlainText)
            value = await DecryptAsync(entity.EncryptedValue, ct);

        return ToDetail(entity, value);
    }

    public async Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.KeyVaults.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new KeyVaultSecretSummary
            {
                Id = x.Id,
                KeyVaultId = x.KeyVaultId,
                Name = x.Name,
                Description = x.Description,
                Category = x.Category,
                Tags = DeserializeTags(x.Tags),
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
            })
            .ToListAsync(ct);

        return list;
    }

    public async Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyVaults.FirstOrDefaultAsync(x => x.KeyVaultId == keyVaultId, ct);
        if (entity is null) return false;

        db.KeyVaults.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string> InjectAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var decrypted = await LoadNameToValueMapAsync(ct);
        if (decrypted.Count == 0) return text;

        return VaultPlaceholderRegex.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            return decrypted.TryGetValue(name, out var value)
                ? value
                : m.Value;
        });
    }

    public async Task<string> StripAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var decrypted = await LoadNameToValueMapAsync(ct);
        if (decrypted.Count == 0) return text;

        var result = text;
        foreach (var item in decrypted
                     .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                     .OrderByDescending(x => x.Value.Length))
        {
            result = result.Replace(item.Value, $"[REDACTED:{item.Key}]", StringComparison.Ordinal);
        }

        return result;
    }

    private async Task<Dictionary<string, string>> LoadNameToValueMapAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.KeyVaults.AsNoTracking()
            .Select(x => new { x.Name, x.EncryptedValue })
            .ToListAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            try
            {
                var value = await DecryptAsync(item.EncryptedValue, ct);
                if (!string.IsNullOrEmpty(value))
                    map[item.Name] = value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[KeyVault] 解密条目失败，name={Name}，已跳过。", item.Name);
            }
        }

        return map;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("密钥名称不能为空。");

        if (!SecretNameRegex.IsMatch(name))
            throw new InvalidOperationException("密钥名称仅允许字母、数字、点、下划线、短横线。");
    }

    private static string NormalizeCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? "general" : category.Trim();

    private static string? SerializeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return null;

        var normalized = tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private static IReadOnlyList<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static KeyVaultSecretSummary ToSummary(KeyVaultEntity entity) => new()
    {
        Id = entity.Id,
        KeyVaultId = entity.KeyVaultId,
        Name = entity.Name,
        Description = entity.Description,
        Category = entity.Category,
        Tags = DeserializeTags(entity.Tags),
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    private static KeyVaultSecretDetail ToDetail(KeyVaultEntity entity, string? value) => new()
    {
        Id = entity.Id,
        KeyVaultId = entity.KeyVaultId,
        Name = entity.Name,
        Description = entity.Description,
        Category = entity.Category,
        Tags = DeserializeTags(entity.Tags),
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        Value = value,
    };

    private static byte[] ResolveMasterKey(ILogger logger)
    {
        var raw = Environment.GetEnvironmentVariable(MasterKeyEnvName);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var key = Convert.FromBase64String(raw);
                if (key.Length == KeySizeBytes)
                    return key;

                throw new InvalidOperationException($"环境变量 {MasterKeyEnvName} 必须是 32 字节 Base64。当前长度={key.Length}。");
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"环境变量 {MasterKeyEnvName} 不是合法 Base64。", ex);
            }
        }

        var generated = RandomNumberGenerator.GetBytes(KeySizeBytes);
        var generatedBase64 = Convert.ToBase64String(generated);
        Environment.SetEnvironmentVariable(MasterKeyEnvName, generatedBase64, EnvironmentVariableTarget.Process);

        logger.LogWarning(
            "[KeyVault] 未配置 {EnvName}，已为当前进程自动生成临时主密钥。重启后如未持久化该环境变量，将无法解密历史密钥。",
            MasterKeyEnvName);

        return generated;
    }
}
