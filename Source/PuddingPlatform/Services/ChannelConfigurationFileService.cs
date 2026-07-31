using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// File-backed channel provider and channel instance configuration.
/// Provider metadata lives in data/config/channel.providers.json; credentials
/// live only in data/channels/{channelId}/manifest.json.
/// </summary>
public sealed class ChannelConfigurationFileService(
    PuddingDataPaths paths,
    IWorkspaceAgentCatalog agentCatalog,
    IAgentChannelBinder agentChannelBinder,
    ILogger<ChannelConfigurationFileService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

    private static readonly ChannelProviderManifest BuiltInFeishuProvider = new()
    {
        ProviderId = ChannelProviderKinds.Feishu,
        Name = "飞书",
        ChannelType = ChannelProviderKinds.Feishu,
        Description = "飞书企业自建应用，通过 WebSocket 长连接接收消息并可靠回复。",
        IsBuiltIn = true,
        IsEnabled = true,
        Capabilities = ["text", "image", "audio", "streaming", "slash_commands"],
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string ProvidersPath => paths.SystemConfigFile("channel.providers.json");

    public async Task<IReadOnlyList<ChannelProviderDto>> ListProvidersAsync(
        CancellationToken ct = default)
    {
        var config = await LoadProvidersAsync(ct);
        return config.Providers
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public async Task<ChannelProviderDto> UpdateProviderAsync(
        string providerId,
        UpdateChannelProviderRequest request,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(providerId, nameof(providerId));
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadProvidersCoreAsync(ct);
            var provider = config.Providers.FirstOrDefault(item =>
                string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Channel provider '{providerId}' 不存在");
            var updated = provider with
            {
                Name = RequireText(request.Name, nameof(request.Name)),
                Description = NormalizeOptional(request.Description),
                IsEnabled = request.IsEnabled,
            };
            config.Providers[config.Providers.IndexOf(provider)] = updated;
            await AtomicFileWriter.WriteJsonAsync(ProvidersPath, config, JsonOptions, ct);
            return ToDto(updated);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WorkspaceChannelDto>> ListWorkspaceChannelsAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(workspaceId, nameof(workspaceId));
        var providers = (await LoadProvidersAsync(ct)).Providers
            .ToDictionary(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase);
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var channels = await ListAllChannelsAsync(ct);

        return channels
            .Where(channel => string.Equals(
                channel.WorkspaceId,
                workspaceId,
                StringComparison.Ordinal))
            .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(channel => ToDto(
                channel,
                providers.GetValueOrDefault(channel.ProviderId),
                agents.FirstOrDefault(agent =>
                    agent.ChannelIds?.Contains(channel.ChannelId, StringComparer.Ordinal) == true)
                    ?.AgentId))
            .ToList();
    }

    public async Task<WorkspaceChannelDto?> GetWorkspaceChannelAsync(
        string workspaceId,
        string channelId,
        CancellationToken ct = default)
    {
        var channels = await ListWorkspaceChannelsAsync(workspaceId, ct);
        return channels.FirstOrDefault(channel =>
            string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal));
    }

    public async Task<WorkspaceChannelDto> CreateWorkspaceChannelAsync(
        string workspaceId,
        UpsertWorkspaceChannelRequest request,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(workspaceId, nameof(workspaceId));
        await _writeLock.WaitAsync(ct);
        try
        {
            var provider = await GetEnabledProviderAsync(request.ProviderId, ct);
            await ValidateBoundAgentAsync(workspaceId, request.BoundAgentId, ct);
            var allChannels = await ListAllChannelsAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var channel = BuildChannel(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                request,
                provider,
                existing: null,
                now);
            EnsureUniqueFeishuAppId(allChannels, channel, exceptChannelId: null);
            await AtomicFileWriter.WriteJsonAsync(
                paths.ChannelManifestFile(channel.ChannelId),
                channel,
                JsonOptions,
                ct);
            await agentChannelBinder.SetChannelBindingAsync(
                workspaceId,
                channel.ChannelId,
                NormalizeOptional(request.BoundAgentId),
                ct);
            logger.LogInformation(
                "Workspace channel created: workspace={WorkspaceId} channel={ChannelId} provider={ProviderId}",
                workspaceId,
                channel.ChannelId,
                channel.ProviderId);
            return ToDto(channel, provider, NormalizeOptional(request.BoundAgentId));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WorkspaceChannelDto> UpdateWorkspaceChannelAsync(
        string workspaceId,
        string channelId,
        UpsertWorkspaceChannelRequest request,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(workspaceId, nameof(workspaceId));
        ValidateSafeSegment(channelId, nameof(channelId));
        await _writeLock.WaitAsync(ct);
        try
        {
            var existing = await GetChannelAsync(channelId, ct)
                ?? throw new KeyNotFoundException($"Channel '{channelId}' 不存在");
            EnsureWorkspace(existing, workspaceId);
            var provider = await GetEnabledProviderAsync(request.ProviderId, ct);
            await ValidateBoundAgentAsync(workspaceId, request.BoundAgentId, ct);
            var allChannels = await ListAllChannelsAsync(ct);
            var updated = BuildChannel(
                channelId,
                workspaceId,
                request,
                provider,
                existing,
                DateTimeOffset.UtcNow);
            EnsureUniqueFeishuAppId(allChannels, updated, channelId);
            await AtomicFileWriter.WriteJsonAsync(
                paths.ChannelManifestFile(channelId),
                updated,
                JsonOptions,
                ct);
            await agentChannelBinder.SetChannelBindingAsync(
                workspaceId,
                channelId,
                NormalizeOptional(request.BoundAgentId),
                ct);
            logger.LogInformation(
                "Workspace channel updated: workspace={WorkspaceId} channel={ChannelId} provider={ProviderId}",
                workspaceId,
                channelId,
                updated.ProviderId);
            return ToDto(updated, provider, NormalizeOptional(request.BoundAgentId));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteWorkspaceChannelAsync(
        string workspaceId,
        string channelId,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(workspaceId, nameof(workspaceId));
        ValidateSafeSegment(channelId, nameof(channelId));
        await _writeLock.WaitAsync(ct);
        try
        {
            var existing = await GetChannelAsync(channelId, ct)
                ?? throw new KeyNotFoundException($"Channel '{channelId}' 不存在");
            EnsureWorkspace(existing, workspaceId);
            await agentChannelBinder.SetChannelBindingAsync(
                workspaceId,
                channelId,
                agentId: null,
                ct);
            var channelRoot = paths.ChannelRoot(channelId);
            if (Directory.Exists(channelRoot))
                Directory.Delete(channelRoot, recursive: true);
            logger.LogInformation(
                "Workspace channel deleted: workspace={WorkspaceId} channel={ChannelId}",
                workspaceId,
                channelId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ChannelInstanceManifest?> GetChannelAsync(
        string channelId,
        CancellationToken ct = default)
    {
        ValidateSafeSegment(channelId, nameof(channelId));
        return await AtomicFileWriter.ReadJsonAsync<ChannelInstanceManifest>(
            paths.ChannelManifestFile(channelId),
            JsonOptions,
            ct);
    }

    public async Task<IReadOnlyList<ChannelInstanceManifest>> ListAllChannelsAsync(
        CancellationToken ct = default)
    {
        if (!Directory.Exists(paths.ChannelsRoot))
            return [];

        var result = new List<ChannelInstanceManifest>();
        foreach (var directory in Directory.EnumerateDirectories(paths.ChannelsRoot))
        {
            ct.ThrowIfCancellationRequested();
            var channelId = Path.GetFileName(directory);
            if (!IsSafeSegment(channelId))
                continue;
            try
            {
                var channel = await GetChannelAsync(channelId, ct);
                if (channel is not null
                    && string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal))
                {
                    result.Add(channel);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogError(
                    ex,
                    "Channel manifest load failed: channel={ChannelId}",
                    channelId);
            }
        }

        return result;
    }

    /// <summary>
    /// Directly upgrades development data from the former Agent-owned Feishu
    /// object. Runtime never reads the legacy object after this method returns.
    /// </summary>
    public async Task<int> MigrateLegacyAgentFeishuBindingsAsync(
        CancellationToken ct = default)
    {
#pragma warning disable CS0618
        var migrated = 0;
        if (!Directory.Exists(paths.AgentInstancesRoot))
            return migrated;

        await LoadProvidersAsync(ct);
        foreach (var directory in Directory.EnumerateDirectories(paths.AgentInstancesRoot))
        {
            ct.ThrowIfCancellationRequested();
            var agentId = Path.GetFileName(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            var agent = await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(
                manifestPath,
                JsonOptions,
                ct);
            if (agent?.Feishu is not { } feishu)
                continue;

            var channelId = $"feishu-{agent.AgentInstanceId}";
            ValidateSafeSegment(channelId, nameof(channelId));
            var channel = await GetChannelAsync(channelId, ct);
            if (channel is null)
            {
                var now = DateTimeOffset.UtcNow;
                channel = new ChannelInstanceManifest
                {
                    ChannelId = channelId,
                    WorkspaceId = agent.WorkspaceId,
                    ProviderId = ChannelProviderKinds.Feishu,
                    Name = string.IsNullOrWhiteSpace(agent.DisplayName)
                        ? "飞书机器人"
                        : $"{agent.DisplayName} · 飞书",
                    Description = feishu.Description,
                    IsEnabled = feishu.Enabled,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Feishu = new FeishuChannelSettings
                    {
                        AppId = feishu.AppId,
                        AppSecret = feishu.AppSecret,
                        StreamingRepliesEnabled = feishu.StreamingRepliesEnabled,
                        TtsRepliesEnabled = feishu.TtsRepliesEnabled,
                        TtsVoice = string.IsNullOrWhiteSpace(feishu.TtsVoice)
                            ? "Cherry"
                            : feishu.TtsVoice.Trim(),
                        PrivilegedUserOpenIds = feishu.PrivilegedUserOpenIds,
                    },
                };
                EnsureUniqueFeishuAppId(
                    await ListAllChannelsAsync(ct),
                    channel,
                    exceptChannelId: null);
                await AtomicFileWriter.WriteJsonAsync(
                    paths.ChannelManifestFile(channelId),
                    channel,
                    JsonOptions,
                    ct);
            }

            await agentChannelBinder.SetChannelBindingAsync(
                agent.WorkspaceId,
                channelId,
                agent.AgentInstanceId,
                ct);
            migrated++;
        }
#pragma warning restore CS0618

        if (migrated > 0)
        {
            logger.LogInformation(
                "Migrated {Count} legacy Agent-owned Feishu binding(s) to channel manifests",
                migrated);
        }
        return migrated;
    }

    private async Task<ChannelProvidersConfig> LoadProvidersAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            return await LoadProvidersCoreAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<ChannelProvidersConfig> LoadProvidersCoreAsync(CancellationToken ct)
    {
        var config = await AtomicFileWriter.ReadJsonAsync<ChannelProvidersConfig>(
            ProvidersPath,
            JsonOptions,
            ct) ?? new ChannelProvidersConfig();
        if (config.Providers.All(provider => !string.Equals(
                provider.ProviderId,
                ChannelProviderKinds.Feishu,
                StringComparison.OrdinalIgnoreCase)))
        {
            config.Providers.Add(BuiltInFeishuProvider);
            await AtomicFileWriter.WriteJsonAsync(ProvidersPath, config, JsonOptions, ct);
        }
        return config;
    }

    private async Task<ChannelProviderManifest> GetEnabledProviderAsync(
        string providerId,
        CancellationToken ct)
    {
        var provider = (await LoadProvidersCoreAsync(ct)).Providers.FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Channel provider '{providerId}' 不存在");
        if (!provider.IsEnabled)
            throw new InvalidOperationException($"Channel provider '{providerId}' 已停用");
        if (!string.Equals(provider.ChannelType, ChannelProviderKinds.Feishu, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Channel provider type '{provider.ChannelType}' 尚未实现");
        return provider;
    }

    private static ChannelInstanceManifest BuildChannel(
        string channelId,
        string workspaceId,
        UpsertWorkspaceChannelRequest request,
        ChannelProviderManifest provider,
        ChannelInstanceManifest? existing,
        DateTimeOffset now)
    {
        if (!string.Equals(provider.ChannelType, ChannelProviderKinds.Feishu, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Channel provider type '{provider.ChannelType}' 尚未实现");

        var appId = RequireText(request.AppId, nameof(request.AppId));
        var appSecret = NormalizeOptional(request.AppSecret)
            ?? existing?.Feishu?.AppSecret;
        if (string.IsNullOrWhiteSpace(appSecret))
            throw new InvalidOperationException("飞书 App Secret 未配置");

        return new ChannelInstanceManifest
        {
            ChannelId = channelId,
            WorkspaceId = workspaceId,
            ProviderId = provider.ProviderId,
            Name = RequireText(request.Name, nameof(request.Name)),
            Description = NormalizeOptional(request.Description),
            IsEnabled = request.IsEnabled,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Feishu = new FeishuChannelSettings
            {
                AppId = appId,
                AppSecret = appSecret,
                StreamingRepliesEnabled = request.StreamingRepliesEnabled,
                TtsRepliesEnabled = request.TtsRepliesEnabled,
                TtsVoice = NormalizeOptional(request.TtsVoice) ?? "Cherry",
                PrivilegedUserOpenIds = (request.PrivilegedUserOpenIds ?? [])
                    .Select(NormalizeOptional)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            },
        };
    }

    private static void EnsureUniqueFeishuAppId(
        IReadOnlyList<ChannelInstanceManifest> channels,
        ChannelInstanceManifest candidate,
        string? exceptChannelId)
    {
        var appId = candidate.Feishu?.AppId;
        if (string.IsNullOrWhiteSpace(appId))
            return;
        if (channels.Any(channel =>
                !string.Equals(channel.ChannelId, exceptChannelId, StringComparison.Ordinal)
                && string.Equals(channel.Feishu?.AppId, appId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("这个飞书 App ID 已绑定到其它渠道");
        }
    }

    private static ChannelProviderDto ToDto(ChannelProviderManifest provider) => new(
        provider.ProviderId,
        provider.Name,
        provider.ChannelType,
        provider.Description,
        provider.IsBuiltIn,
        provider.IsEnabled,
        provider.Capabilities);

    private static WorkspaceChannelDto ToDto(
        ChannelInstanceManifest channel,
        ChannelProviderManifest? provider,
        string? boundAgentId) => new(
        channel.ChannelId,
        channel.Name,
        channel.Description,
        channel.ProviderId,
        provider?.Name ?? channel.ProviderId,
        provider?.ChannelType ?? channel.ProviderId,
        boundAgentId,
        channel.Feishu?.AppId,
        !string.IsNullOrWhiteSpace(channel.Feishu?.AppSecret),
        channel.Feishu?.StreamingRepliesEnabled ?? false,
        channel.Feishu?.PrivilegedUserOpenIds.ToList() ?? [],
        channel.IsEnabled,
        channel.CreatedAt,
        channel.UpdatedAt,
        channel.Feishu?.TtsRepliesEnabled ?? false,
        string.IsNullOrWhiteSpace(channel.Feishu?.TtsVoice)
            ? "Cherry"
            : channel.Feishu.TtsVoice);

    private static void EnsureWorkspace(ChannelInstanceManifest channel, string workspaceId)
    {
        if (!string.Equals(channel.WorkspaceId, workspaceId, StringComparison.Ordinal))
            throw new KeyNotFoundException($"Channel '{channel.ChannelId}' 不属于 Workspace '{workspaceId}'");
    }

    private async Task ValidateBoundAgentAsync(
        string workspaceId,
        string? agentId,
        CancellationToken ct)
    {
        var normalizedAgentId = NormalizeOptional(agentId);
        if (normalizedAgentId is null)
            return;
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        if (agents.All(agent => !string.Equals(
                agent.AgentId,
                normalizedAgentId,
                StringComparison.Ordinal)))
        {
            throw new KeyNotFoundException(
                $"Agent '{normalizedAgentId}' in workspace '{workspaceId}' 不存在");
        }
    }

    private static string RequireText(string? value, string name)
        => NormalizeOptional(value)
           ?? throw new ArgumentException($"{name} 不能为空", name);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateSafeSegment(string value, string name)
    {
        if (!IsSafeSegment(value))
            throw new ArgumentException($"{name} 不是安全的路径段", name);
    }

    private static bool IsSafeSegment(string value)
        => !string.IsNullOrWhiteSpace(value)
           && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
           && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
