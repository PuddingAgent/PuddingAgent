namespace PuddingDesktop.Foundation;

/// <summary>A channel provider (today only Feishu is implemented in Core).</summary>
public sealed record ChannelProvider(
    string ProviderId, string Name, string ChannelType, string Description,
    bool IsBuiltIn, bool IsEnabled, IReadOnlyList<string> Capabilities)
{
    public string StateText => IsEnabled ? "已启用" : "已停用";
    public string CapabilityText => Capabilities.Count == 0 ? "无声明能力" : string.Join("、", Capabilities);
}

/// <summary>A channel instance. The app secret is never returned: only whether one is stored.</summary>
public sealed record ChannelSummary(
    string ChannelId, string Name, string Description, string ProviderId, string ProviderName, string ChannelType,
    string BoundAgentId, string AppId, bool HasAppSecret,
    bool StreamingRepliesEnabled, bool TtsRepliesEnabled, string TtsVoice,
    IReadOnlyList<string> PrivilegedUserOpenIds, bool IsEnabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string SecretText => HasAppSecret ? "已配置（留空表示保持）" : "未配置";
    public string StateText => IsEnabled ? "已启用" : "已停用";
}

/// <summary>
/// Credential write intent. Core has no "clear the secret" operation: a blank value keeps the stored one,
/// and a channel that would end up without any secret is refused outright.
/// </summary>
public sealed record ChannelSecret(bool Replace, string Value)
{
    public static ChannelSecret Keep { get; } = new(false, "");
    public static ChannelSecret Of(string value) => new(true, value);
}

public sealed record ChannelProviderEdit(string ProviderId, string Name, string Description, bool IsEnabled);

public sealed record ChannelEdit(
    string WorkspaceId, string ChannelId, string Name, string Description, string ProviderId, string BoundAgentId,
    string AppId, ChannelSecret AppSecret, bool StreamingRepliesEnabled, bool TtsRepliesEnabled, string TtsVoice,
    IReadOnlyList<string> PrivilegedUserOpenIds, bool IsEnabled);

/// <summary>
/// Task-shaped operations for the channel tab, implemented in Composition against the same
/// ChannelConfigurationFileService the Web controller uses.
/// </summary>
public interface IChannelSettings
{
    Task<IReadOnlyList<ChannelProvider>> ListProvidersAsync(CancellationToken cancellationToken = default);
    Task SaveProviderAsync(ChannelProviderEdit edit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelSummary>> ListChannelsAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task CreateChannelAsync(ChannelEdit create, CancellationToken cancellationToken = default);
    Task SaveChannelAsync(ChannelEdit edit, CancellationToken cancellationToken = default);
    Task DeleteChannelAsync(string workspaceId, string channelId, CancellationToken cancellationToken = default);
}

public static class ChannelText
{
    /// <summary>The secret is write-only in this product; the page must never imply it can be read back.</summary>
    public const string SecretNotice =
        "App Secret 只写不读：界面只显示是否已配置，留空表示保持已保存的密钥；Core 没有“清除密钥”的语义，" +
        "把留空当作清除会直接导致保存失败。";

    public const string ProviderNotice =
        "渠道服务商由 Core 内置定义：只能改名、描述与启用状态，不能新增或删除服务商本身。";

    /// <summary>Core keeps the stored voice when blank and defaults to Cherry; it does not whitelist values.</summary>
    public const string TtsVoiceHint = "留空时 Core 使用 Cherry；Core 不校验音色取值，这里也不编造白名单。";

    public static IReadOnlyList<string> Validate(ChannelProviderEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ProviderId)) errors.Add("缺少服务商 ID。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("服务商名称不能为空。");
        return errors;
    }

    /// <summary>
    /// Validation stays at the form level: Core remains authoritative. A missing secret is only an error when
    /// no secret is stored, because a blank replacement means "keep".
    /// </summary>
    public static IReadOnlyList<string> Validate(ChannelEdit edit, bool hasStoredSecret)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.WorkspaceId)) errors.Add("缺少工作区。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("渠道名称不能为空。");
        if (string.IsNullOrWhiteSpace(edit.ProviderId)) errors.Add("必须选择渠道服务商。");
        if (string.IsNullOrWhiteSpace(edit.AppId)) errors.Add("飞书 App ID 不能为空。");
        if (!hasStoredSecret && (!edit.AppSecret.Replace || string.IsNullOrWhiteSpace(edit.AppSecret.Value)))
            errors.Add("该渠道还没有 App Secret，必须填写一个。");
        if (edit.AppSecret.Replace && string.IsNullOrWhiteSpace(edit.AppSecret.Value))
            errors.Add("填写了替换密钥但内容为空；若要保持原密钥请不要勾选替换。");
        return errors;
    }

    /// <summary>Open ids are comma/whitespace/semicolon separated; blanks are dropped and duplicates removed.</summary>
    public static IReadOnlyList<string> ParseOpenIds(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    public static string FormatOpenIds(IEnumerable<string>? ids) => string.Join(", ", ids ?? []);

    public static string DescribeReplies(bool streaming, bool tts)
    {
        var parts = new List<string>();
        if (streaming) parts.Add("流式回复");
        if (tts) parts.Add("语音回复");
        return parts.Count == 0 ? "普通回复" : string.Join(" + ", parts);
    }
}
