using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// 单条密钥的使用情况：被多少处配置引用、被哪些文件引用、是否与他条同值。
/// </summary>
public sealed record KeyVaultUsageEntry
{
    public string Name { get; init; } = string.Empty;

    /// <summary>引用出现次数（同一文件里出现两次计两次）。</summary>
    public int ReferenceCount { get; init; }

    /// <summary>引用它的文件（相对 DataRoot，正斜杠）。</summary>
    public IReadOnlyList<string> ReferencedBy { get; init; } = [];

    /// <summary>
    /// 同值分组标识（如 <c>dup-1</c>）。只在两条以上密钥值完全相同时才有值。
    /// 刻意使用不透明编号而不是哈希：哈希对高熵密钥虽不可逆，但仍是一个可被反复查询的比对预言机。
    /// </summary>
    public string? DuplicateGroupId { get; init; }
}

/// <summary>密钥库整体使用情况报告。</summary>
public sealed record KeyVaultUsageReport
{
    public int TotalEntries { get; init; }

    /// <summary>没有任何配置引用的条目数。这些条目改了也不会生效。</summary>
    public int OrphanCount { get; init; }

    public int DuplicateGroupCount { get; init; }

    public IReadOnlyList<KeyVaultUsageEntry> Entries { get; init; } = [];
}

/// <summary>
/// 回答「这条密钥现在谁在用、有没有重复、是不是死条目」。
/// 只读：不修改任何密钥，也不返回任何密钥值或它的哈希。
/// </summary>
public interface IKeyVaultUsageInspector
{
    Task<KeyVaultUsageReport> BuildAsync(CancellationToken ct = default);
}

/// <summary>
/// 通过扫描配置文件里的 <c>{{vault:NAME}}</c> 引用，以及比对密钥值的相等性，
/// 给管理界面提供「被引用 / 孤儿 / 同值重复」三类信息。
/// </summary>
/// <remarks>
/// 覆盖面与迁移脚本一致（<c>config</c>、<c>channels</c>、<c>agents</c> 三处）。
/// 若将来有新的配置根，这里与迁移脚本必须同时更新，否则会误报孤儿。
/// </remarks>
public sealed partial class KeyVaultUsageInspector(
    PuddingDataPaths paths,
    IKeyVaultService keyVaultService) : IKeyVaultUsageInspector
{
    private static readonly string[] ScanRoots = ["config", "channels", "agents"];

    [GeneratedRegex(@"\{\{vault:([^}]+)\}\}")]
    private static partial Regex VaultReferencePattern();

    public async Task<KeyVaultUsageReport> BuildAsync(CancellationToken ct = default)
    {
        var secrets = await keyVaultService.ListSecretsAsync(ct);
        var references = ScanReferences();

        var duplicateGroups = await FindDuplicateGroupsAsync(secrets, ct);

        var entries = new List<KeyVaultUsageEntry>(secrets.Count);
        foreach (var secret in secrets)
        {
            references.TryGetValue(secret.Name, out var usage);
            entries.Add(new KeyVaultUsageEntry
            {
                Name = secret.Name,
                ReferenceCount = usage?.Count ?? 0,
                ReferencedBy = usage?.Files ?? [],
                DuplicateGroupId = duplicateGroups.TryGetValue(secret.Name, out var group) ? group : null,
            });
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new KeyVaultUsageReport
        {
            TotalEntries = entries.Count,
            OrphanCount = entries.Count(static e => e.ReferenceCount == 0),
            DuplicateGroupCount = duplicateGroups.Values.Distinct(StringComparer.Ordinal).Count(),
            Entries = entries,
        };
    }

    private sealed record ReferenceUsage(int Count, IReadOnlyList<string> Files);

    private Dictionary<string, ReferenceUsage> ScanReferences()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var root = paths.DataRoot;
        foreach (var scanRoot in ScanRoots)
        {
            var directory = Path.Combine(root, scanRoot);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    // 正在被写入的文件跳过即可：使用情况视图不应因为一次读失败而整体不可用。
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach (Match match in VaultReferencePattern().Matches(text))
                {
                    var name = match.Groups[1].Value.Trim();
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    counts[name] = counts.TryGetValue(name, out var current) ? current + 1 : 1;
                    if (!files.TryGetValue(name, out var list))
                    {
                        list = [];
                        files[name] = list;
                    }

                    if (!list.Contains(relative))
                    {
                        list.Add(relative);
                    }
                }
            }
        }

        var result = new Dictionary<string, ReferenceUsage>(StringComparer.Ordinal);
        foreach (var (name, count) in counts)
        {
            result[name] = new ReferenceUsage(count, files[name]);
        }

        return result;
    }

    /// <summary>
    /// 解密每条密钥并计算 SHA-256，把同值的归到一组。哈希只在方法内部用于比对，
    /// 不出方法，也不进日志。
    /// </summary>
    private async Task<Dictionary<string, string>> FindDuplicateGroupsAsync(
        IReadOnlyList<KeyVaultSecretSummary> secrets,
        CancellationToken ct)
    {
        var byFingerprint = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var secret in secrets)
        {
            ct.ThrowIfCancellationRequested();

            var detail = await keyVaultService.GetSecretAsync(secret.KeyVaultId, includePlainText: true, ct);
            if (detail?.Value is not { Length: > 0 } value)
            {
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            if (!byFingerprint.TryGetValue(hash, out var names))
            {
                names = [];
                byFingerprint[hash] = names;
            }

            names.Add(secret.Name);
        }

        // 只为真正的重复分配编号，编号按组内首个名字排序生成，保证同一输入下输出稳定。
        var groups = byFingerprint.Values
            .Where(static names => names.Count > 1)
            .Select(static names => names.OrderBy(n => n, StringComparer.Ordinal).ToList())
            .OrderBy(static names => names[0], StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < groups.Count; i++)
        {
            var groupId = $"dup-{i + 1}";
            foreach (var name in groups[i])
            {
                result[name] = groupId;
            }
        }

        return result;
    }
}
