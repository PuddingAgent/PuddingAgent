namespace PuddingDesktop.Foundation;

/// <summary>
/// How a form treats an already stored credential. "Keep" is the default so a form that never showed
/// the secret cannot erase it; "Replace" is the only value that carries a new secret.
/// </summary>
public enum ApiKeyChange { Keep, Replace, Clear }

public sealed record LlmProviderLimits(int? MaxConcurrentRequests, long? TokensPerMinute, int? RequestsPerMinute);

public sealed record LlmProviderSummary(
    string ProviderId,
    string Name,
    string BaseUrl,
    string Description,
    bool IsEnabled,
    bool HasApiKey,
    LlmProviderLimits Limits,
    int ModelCount);

public sealed record LlmModelSummary(
    string ModelId,
    string Name,
    string Protocol,
    IReadOnlyList<string> CapabilityTags,
    int? MaxContextTokens,
    int? MaxInputTokens,
    int? MaxOutputTokens,
    int? MaxConcurrentRequests,
    decimal InputPricePer1MTokens,
    decimal OutputPricePer1MTokens,
    decimal CacheHitPricePer1MTokens,
    bool IsDefault,
    bool IsDeprecated,
    bool IsEmbedding,
    int SortOrder);

public sealed record LlmProviderEdit(
    string ProviderId,
    string Name,
    string BaseUrl,
    string Description,
    bool IsEnabled,
    ApiKeyChange KeyChange,
    string? NewKey,
    LlmProviderLimits Limits);

public sealed record LlmModelEdit(
    string ProviderId,
    string ModelId,
    string Name,
    string Protocol,
    IReadOnlyList<string> CapabilityTags,
    int? MaxContextTokens,
    int? MaxInputTokens,
    int? MaxOutputTokens,
    int? MaxConcurrentRequests,
    decimal InputPricePer1MTokens,
    decimal OutputPricePer1MTokens,
    decimal CacheHitPricePer1MTokens,
    bool IsDefault,
    bool IsDeprecated,
    bool IsEmbedding,
    int SortOrder);

/// <summary>
/// Provider token quota as reported by Core: the stored limits plus usage derived from the token
/// ledger. Usage is never a stored counter, so a reset only moves the accounting window.
/// </summary>
public sealed record LlmQuotaLimits(long? DailyTokenLimit, long? MonthlyTokenLimit)
{
    public static LlmQuotaLimits Unlimited { get; } = new(null, null);
}

public sealed record LlmQuotaStatus(
    long? DailyTokenLimit,
    long? MonthlyTokenLimit,
    long DailyTokensUsed,
    long MonthlyTokensUsed,
    bool IsSuspended,
    DateTimeOffset? DailyResetAt,
    DateTimeOffset? MonthlyResetAt,
    DateTimeOffset UpdatedAt)
{
    public double? DailyUsedPercent => DailyTokenLimit is > 0 ? (double)DailyTokensUsed / DailyTokenLimit.Value : null;
    public double? MonthlyUsedPercent => MonthlyTokenLimit is > 0 ? (double)MonthlyTokensUsed / MonthlyTokenLimit.Value : null;

    public LlmQuotaLimits Limits => new(DailyTokenLimit, MonthlyTokenLimit);

    public string Describe() => IsSuspended
        ? "已超出配额：新的调用会被按限额拒绝。"
        : DailyTokenLimit is null && MonthlyTokenLimit is null
            ? "未设置限额。"
            : "配额内。";
}

/// <summary>
/// Task-shaped operations for the LLM resource-pool settings pages, implemented in Composition against
/// the in-process Core services. One method per thing a page does — not one per Web endpoint — and no
/// HTTP, JWT or DTO relay. Authoritative validation stays in Core; the shell only pre-checks the form.
/// </summary>
public interface ILlmResourceSettings
{
    Task<IReadOnlyList<LlmProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LlmModelSummary>> ListModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task SaveProviderAsync(LlmProviderEdit edit, CancellationToken cancellationToken = default);
    Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default);
    Task SaveModelAsync(LlmModelEdit edit, CancellationToken cancellationToken = default);
    Task DeleteModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default);
    Task<LlmQuotaStatus?> GetQuotaAsync(string providerId, CancellationToken cancellationToken = default);
    Task<LlmQuotaStatus> SaveQuotaAsync(string providerId, LlmQuotaLimits limits, CancellationToken cancellationToken = default);
    Task<LlmQuotaStatus> ResetDailyQuotaAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Pure form helpers shared by the editor and its tests. No Core call, no persistence.</summary>
public static class LlmSettingsText
{
    public static readonly string[] Protocols = ["openai", "responses", "anthropic"];

    public static IReadOnlyList<string> ParseTags(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split([',', '，', ';', '；', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string FormatTags(IEnumerable<string>? tags) => string.Join(", ", tags ?? []);

    /// <summary>Empty text means "unset", not zero: an absent limit must stay absent in the config file.</summary>
    public static int? ParseOptionalInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;
    }

    public static long? ParseOptionalLong(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return long.TryParse(text.Trim(), out var value) && value > 0 ? value : null;
    }

    public static decimal ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        return decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : 0m;
    }

    public static string FormatOptional<T>(T? value) where T : struct => value?.ToString() ?? "";

    public static string DescribeKeyState(bool hasKey, ApiKeyChange change) => change switch
    {
        ApiKeyChange.Clear => "保存后将清除已配置的密钥。",
        ApiKeyChange.Replace => hasKey ? "保存后将用新密钥替换现有密钥。" : "保存后写入新密钥。",
        _ => hasKey ? "已配置密钥（不显示明文）；保持现有密钥。" : "未配置密钥。"
    };

    /// <summary>
    /// Form-level checks only. Core re-validates authoritatively, so a passing form is not a guarantee.
    /// </summary>
    public static IReadOnlyList<string> Validate(LlmProviderEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ProviderId)) errors.Add("服务商 ID 不能为空。");
        else if (edit.ProviderId.Length > 80 || edit.ProviderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            errors.Add("服务商 ID 只能包含字母、数字、'-' 和 '_'，且不超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("名称不能为空。");
        if (!IsHttpUrl(edit.BaseUrl)) errors.Add("BaseUrl 必须是 http/https 绝对地址，且不能带账号、查询或片段。");
        if (edit.KeyChange == ApiKeyChange.Replace && string.IsNullOrWhiteSpace(edit.NewKey))
            errors.Add("选择“替换”时必须填写新密钥。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(LlmModelEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ProviderId)) errors.Add("请先选择服务商。");
        if (string.IsNullOrWhiteSpace(edit.ModelId)) errors.Add("模型 ID 不能为空。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("模型名称不能为空。");
        if (!Protocols.Contains(edit.Protocol, StringComparer.Ordinal)) errors.Add("协议必须是 openai、responses 或 anthropic。");
        // The Core model request takes non-nullable limits, so an unset value would be written as 0.
        if (edit.MaxContextTokens is not > 0) errors.Add("最大上下文 token 必须大于 0。");
        if (edit.MaxOutputTokens is not > 0) errors.Add("最大输出 token 必须大于 0。");
        if (edit.MaxContextTokens is > 0 && edit.MaxOutputTokens is > 0 && edit.MaxOutputTokens > edit.MaxContextTokens)
            errors.Add("最大输出 token 不能大于最大上下文 token。");
        if (edit.MaxInputTokens is > 0 && edit.MaxContextTokens is > 0 && edit.MaxInputTokens > edit.MaxContextTokens)
            errors.Add("最大输入 token 不能大于最大上下文 token。");
        return errors;
    }

    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>Form-level check mirroring the Core quota rules; Core re-validates authoritatively.</summary>
    public static IReadOnlyList<string> Validate(LlmQuotaLimits limits)
    {
        var errors = new List<string>();
        if (limits.DailyTokenLimit is <= 0) errors.Add("每日 token 限额必须大于 0，或留空表示不限制。");
        if (limits.MonthlyTokenLimit is <= 0) errors.Add("每月 token 限额必须大于 0，或留空表示不限制。");
        if (limits is { DailyTokenLimit: { } daily, MonthlyTokenLimit: { } monthly } && daily > monthly)
            errors.Add("每日 token 限额不能大于每月 token 限额。");
        return errors;
    }
}
