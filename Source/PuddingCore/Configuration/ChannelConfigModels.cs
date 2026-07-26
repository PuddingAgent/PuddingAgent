using System.Text.Json.Serialization;

namespace PuddingCode.Configuration;

public static class ChannelProviderKinds
{
    public const string Feishu = "feishu";
}

public sealed record ChannelProvidersConfig
{
    public int Version { get; init; } = 1;
    public List<ChannelProviderManifest> Providers { get; init; } = [];
}

public sealed record ChannelProviderManifest
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ChannelType { get; init; } = "";
    public string? Description { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsEnabled { get; init; } = true;
    public List<string> Capabilities { get; init; } = [];
}

public sealed record ChannelInstanceManifest
{
    public string ChannelId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeishuChannelSettings? Feishu { get; init; }
}

public sealed record FeishuChannelSettings
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public bool StreamingRepliesEnabled { get; init; } = true;
    public IReadOnlyList<string> PrivilegedUserOpenIds { get; init; } = [];
}
