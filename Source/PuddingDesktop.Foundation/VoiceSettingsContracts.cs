namespace PuddingDesktop.Foundation;

public sealed record VoiceProviderSummary(
    string ProviderId, string Name, string Endpoint, string Description,
    bool IsEnabled, bool HasApiKey, int TtsModelCount, int AsrModelCount);

public sealed record VoiceTtsModel(
    string ProviderId, string ModelId, string Name, string Path,
    IReadOnlyList<string> Voices, IReadOnlyList<string> AudioFormats, IReadOnlyList<int> SampleRates,
    bool SupportsStreaming, bool SupportsInstructions, bool SupportsVoiceCloning, bool SupportsVoiceDesign,
    bool IsDeprecated, bool IsDefault, int SortOrder);

public sealed record VoiceAsrModel(
    string ProviderId, string ModelId, string Name, string Path,
    IReadOnlyList<string> Languages, IReadOnlyList<int> SampleRates,
    bool SupportsEmotion, bool SupportsTimestamps, bool SupportsHotWords,
    bool IsDeprecated, bool IsDefault, int SortOrder);

public sealed record VoiceProviderEdit(
    string ProviderId, string Name, string Endpoint, string Description, bool IsEnabled,
    ApiKeyChange KeyChange, string? NewKey);

/// <summary>The effective defaults the runtime actually reads, so the UI never shows a decorative flag.</summary>
public sealed record VoiceDefaults(
    string? TtsProviderId, string? TtsModelId, string? AsrProviderId, string? AsrModelId)
{
    public static VoiceDefaults None { get; } = new(null, null, null, null);

    public string Describe() =>
        $"默认 TTS：{Describe(TtsProviderId, TtsModelId)} · 默认 ASR：{Describe(AsrProviderId, AsrModelId)}";

    private static string Describe(string? providerId, string? modelId) =>
        string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId) ? "未设置" : $"{providerId} / {modelId}";
}

/// <summary>
/// Task-shaped operations for the voice (TTS/ASR) settings pages, implemented in Composition against
/// VoiceProviderFileService. No HTTP, JWT or DTO relay; Core stays the authority for validation.
/// </summary>
public interface IVoiceResourceSettings
{
    Task<IReadOnlyList<VoiceProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceTtsModel>> ListTtsModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceAsrModel>> ListAsrModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task<VoiceDefaults> GetDefaultsAsync(CancellationToken cancellationToken = default);
    Task SaveProviderAsync(VoiceProviderEdit edit, CancellationToken cancellationToken = default);
    Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default);
    Task SaveTtsModelAsync(VoiceTtsModel model, CancellationToken cancellationToken = default);
    Task DeleteTtsModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default);
    Task SaveAsrModelAsync(VoiceAsrModel model, CancellationToken cancellationToken = default);
    Task DeleteAsrModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default);
}

/// <summary>Pure form helpers for the voice pages. No Core call, no persistence.</summary>
public static class VoiceSettingsText
{
    private static readonly char[] Separators = [',', '，', ';', '；', '\n', '\r', '\t', ' '];

    public static IReadOnlyList<string> ParseList(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string FormatList(IEnumerable<string>? items) => string.Join(", ", items ?? []);

    /// <summary>Sample rates are positive integers; anything else is dropped rather than written as 0.</summary>
    public static IReadOnlyList<int> ParseSampleRates(string? text) => ParseList(text)
        .Select(item => int.TryParse(item, out var rate) && rate > 0 ? rate : 0)
        .Where(rate => rate > 0)
        .Distinct()
        .OrderBy(rate => rate)
        .ToArray();

    public static string FormatSampleRates(IEnumerable<int>? rates) => string.Join(", ", rates ?? []);

    /// <summary>Empty text means "unset" rather than zero, matching the LLM form helpers.</summary>
    public static int? ParseOptionalInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;
    }

    public static IReadOnlyList<string> Validate(VoiceProviderEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ProviderId)) errors.Add("服务商 ID 不能为空。");
        else if (edit.ProviderId.Length > 80 || edit.ProviderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            errors.Add("服务商 ID 只能包含字母、数字、'-' 和 '_'，且不超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("名称不能为空。");
        if (!IsHttpEndpoint(edit.Endpoint)) errors.Add("Endpoint 必须是 http/https 绝对地址，且不能带账号、查询或片段。");
        if (edit.KeyChange == ApiKeyChange.Replace && string.IsNullOrWhiteSpace(edit.NewKey)) errors.Add("选择“替换”时必须填写新密钥。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(VoiceTtsModel model)
    {
        var errors = ValidateModelCore(model.ProviderId, model.ModelId, model.Name);
        if (model.SampleRates.Any(rate => rate <= 0)) errors.Add("TTS 采样率必须是正整数。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(VoiceAsrModel model)
    {
        var errors = ValidateModelCore(model.ProviderId, model.ModelId, model.Name);
        if (model.SampleRates.Any(rate => rate <= 0)) errors.Add("ASR 采样率必须是正整数。");
        return errors;
    }

    public static bool IsHttpEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static List<string> ValidateModelCore(string providerId, string modelId, string name)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(providerId)) errors.Add("请先选择服务商。");
        if (string.IsNullOrWhiteSpace(modelId)) errors.Add("模型 ID 不能为空。");
        else if (modelId.Length > 128) errors.Add("模型 ID 不能超过 128 个字符。");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("模型名称不能为空。");
        return errors;
    }
}
