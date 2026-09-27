namespace PuddingDesktop.Foundation;

/// <summary>
/// A vault secret's metadata. The plaintext value is never part of this model: the vault is write-only
/// from the settings panel, and references are expressed with the placeholder instead.
/// </summary>
public sealed record VaultSecret(
    long Id, string KeyVaultId, string Name, string Description, string Category,
    IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)
{
    /// <summary>Core's placeholder syntax: {{vault:name}}. This is what other configuration copies.</summary>
    public string Placeholder => SecurityText.BuildPlaceholder(Name);
    public string CategoryText => SecurityText.DescribeVaultCategory(Category);
    public string TagsText => Tags.Count == 0 ? "无标签" : string.Join("、", Tags);
}

/// <summary>Create or update. A blank value on update keeps the stored secret.</summary>
public sealed record VaultSecretEdit(
    string KeyVaultId, string Name, string Description, string Category, string Value,
    IReadOnlyList<string> Tags)
{
    public bool IsCreate => string.IsNullOrWhiteSpace(KeyVaultId);
}

public sealed record ClassifierStatusEntry(
    string ClassifierId, string Health, string Detail, int ConsecutiveFailures,
    DateTimeOffset? LastCheckedAtUtc, double? LastLatencyMs)
{
    public string HealthText => SecurityText.DescribeClassifierHealth(Health);
    public string LatencyText => LastLatencyMs is null ? "无延迟数据" : $"{LastLatencyMs:0.#} ms";
    public string CheckedText => LastCheckedAtUtc is null
        ? "尚未探测"
        : $"最近探测 {LastCheckedAtUtc.Value.ToLocalTime():MM-dd HH:mm:ss}";
}

/// <summary>
/// Whether the classifier health surface is wired at all. Core reports this explicitly so the page can say
/// "unknown" instead of pretending everything is healthy.
/// </summary>
public sealed record ClassifierHealthReport(bool Configured, IReadOnlyList<ClassifierStatusEntry> Classifiers)
{
    public static ClassifierHealthReport NotConfigured { get; } = new(false, []);
}

public interface ISecuritySettings
{
    Task<IReadOnlyList<VaultSecret>> ListSecretsAsync(CancellationToken cancellationToken = default);
    Task SaveSecretAsync(VaultSecretEdit edit, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string keyVaultId, CancellationToken cancellationToken = default);
    Task<ClassifierHealthReport> ReadClassifierHealthAsync(CancellationToken cancellationToken = default);
}

public static class SecurityText
{
    /// <summary>Mirrors KeyVaultService.VaultPlaceholderRegex: {{vault:name}}.</summary>
    public static string BuildPlaceholder(string name) => $"{{{{vault:{name}}}}}";

    public static IReadOnlyList<string> VaultCategories { get; } = ["general", "api", "token"];

    public const string WriteOnlyNotice =
        "密钥值是只写的：界面只显示元数据与引用占位符，不回显明文，也不提供「显示密钥」。";

    public const string PlaceholderNotice =
        "引用占位符形如 {{vault:名称}}，由 Core 在注入时替换；把它复制到需要该密钥的配置里即可。";

    public const string ClassifierUnknownNotice =
        "分类器健康面未接线时 Core 明确返回未知态；界面照实显示「未接线」而不是「健康」。";

    public static string DescribeVaultCategory(string? category) => category switch
    {
        null or "" => "未分类",
        var value when string.Equals(value, "general", StringComparison.OrdinalIgnoreCase) => "general（通用）",
        var value when string.Equals(value, "api", StringComparison.OrdinalIgnoreCase) => "api（接口密钥）",
        var value when string.Equals(value, "token", StringComparison.OrdinalIgnoreCase) => "token（令牌）",
        var value => value
    };

    public static string DescribeClassifierHealth(string? health) => health switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) => "未知（尚未探测）",
        var value when string.Equals(value, "healthy", StringComparison.OrdinalIgnoreCase) => "健康",
        var value when string.Equals(value, "degraded", StringComparison.OrdinalIgnoreCase) => "降级",
        var value when string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase) => "不可用",
        var value => value
    };

    public static bool IsKnownCategory(string? category) =>
        category is not null && VaultCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>Core matches the placeholder with [a-zA-Z0-9._-]+, so the name must stay inside it.</summary>
    public static bool IsValidSecretName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static IReadOnlyList<string> Validate(VaultSecretEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("名称不能为空。");
        else if (!IsValidSecretName(edit.Name))
            errors.Add("名称只能包含字母、数字、'.'、'_' 与 '-'（Core 的占位符语法只认这些字符）。");
        if (!IsKnownCategory(edit.Category)) errors.Add($"分类必须是 {string.Join(" / ", VaultCategories)} 之一。");
        // 新建必须有值；更新留空表示保持原值（Core 语义）。
        if (edit.IsCreate && string.IsNullOrWhiteSpace(edit.Value)) errors.Add("新建密钥必须填写密钥值。");
        return errors;
    }

    public static IReadOnlyList<string> ParseTags(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public static string FormatTags(IEnumerable<string>? tags) => string.Join(", ", tags ?? []);

    public static string DescribeHealthSummary(ClassifierHealthReport report)
    {
        if (!report.Configured) return "分类器健康面未接线（未知态）。";
        if (report.Classifiers.Count == 0) return "已接线，但没有任何分类器快照。";
        var unhealthy = report.Classifiers.Count(item =>
            !string.Equals(item.Health, "healthy", StringComparison.OrdinalIgnoreCase));
        return unhealthy == 0
            ? $"已接线：{report.Classifiers.Count} 个分类器全部健康。"
            : $"已接线：{report.Classifiers.Count} 个分类器，其中 {unhealthy} 个非健康。";
    }
}
