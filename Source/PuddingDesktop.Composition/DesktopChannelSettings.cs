using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-05 channel slice: binds the channel tab to the same ChannelConfigurationFileService the Web
/// controller uses. A blank App Secret means "keep the stored one" — Core has no clear-secret operation —
/// and the secret is never read back into the shell.
/// </summary>
internal sealed class DesktopChannelSettings(IDesktopKernel kernel) : IChannelSettings
{
    private Task<T> Channels<T>(string operationId,
        Func<ChannelConfigurationFileService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<ChannelConfigurationFileService>(), token),
            cancellationToken);

    public Task<IReadOnlyList<ChannelProvider>> ListProvidersAsync(CancellationToken cancellationToken = default)
        => Channels("channels.providers.list", async (service, token) =>
        {
            var providers = await service.ListProvidersAsync(token);
            return (IReadOnlyList<ChannelProvider>)providers.Select(provider => new ChannelProvider(
                provider.ProviderId, provider.Name, provider.ChannelType, provider.Description ?? "",
                provider.IsBuiltIn, provider.IsEnabled, provider.Capabilities ?? [])).ToArray();
        }, cancellationToken);

    public Task SaveProviderAsync(ChannelProviderEdit edit, CancellationToken cancellationToken = default)
        => Channels("channels.providers.save", async (service, token) =>
        {
            await service.UpdateProviderAsync(edit.ProviderId,
                new UpdateChannelProviderRequest(edit.Name, edit.Description, edit.IsEnabled), token);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<ChannelSummary>> ListChannelsAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Channels("channels.list", async (service, token) =>
        {
            var channels = await service.ListWorkspaceChannelsAsync(workspaceId, token);
            return (IReadOnlyList<ChannelSummary>)channels.Select(Map).ToArray();
        }, cancellationToken);

    public Task CreateChannelAsync(ChannelEdit create, CancellationToken cancellationToken = default)
        => Channels("channels.create", async (service, token) =>
        {
            await service.CreateWorkspaceChannelAsync(create.WorkspaceId, Request(create), token);
            return true;
        }, cancellationToken);

    public Task SaveChannelAsync(ChannelEdit edit, CancellationToken cancellationToken = default)
        => Channels("channels.save", async (service, token) =>
        {
            await service.UpdateWorkspaceChannelAsync(edit.WorkspaceId, edit.ChannelId,
                Request(edit), token);
            return true;
        }, cancellationToken);

    public Task DeleteChannelAsync(string workspaceId, string channelId, CancellationToken cancellationToken = default)
        => Channels("channels.delete", async (service, token) =>
        {
            await service.DeleteWorkspaceChannelAsync(workspaceId, channelId, token);
            return true;
        }, cancellationToken);

    /// <summary>A kept secret is sent as null so Core falls back to the stored value.</summary>
    private static UpsertWorkspaceChannelRequest Request(ChannelEdit edit) => new(
        edit.Name.Trim(),
        Nullable(edit.Description),
        edit.ProviderId,
        Nullable(edit.BoundAgentId),
        Nullable(edit.AppId),
        edit.AppSecret.Replace ? Nullable(edit.AppSecret.Value) : null,
        edit.StreamingRepliesEnabled,
        [.. edit.PrivilegedUserOpenIds],
        edit.IsEnabled,
        edit.TtsRepliesEnabled,
        Nullable(edit.TtsVoice));

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ChannelSummary Map(WorkspaceChannelDto channel) => new(
        channel.ChannelId, channel.Name, channel.Description ?? "", channel.ProviderId, channel.ProviderName,
        channel.ChannelType, channel.BoundAgentId ?? "", channel.AppId ?? "", channel.HasAppSecret,
        channel.StreamingRepliesEnabled, channel.TtsRepliesEnabled, channel.TtsVoice ?? "",
        channel.PrivilegedUserOpenIds ?? [], channel.IsEnabled, channel.CreatedAt, channel.UpdatedAt);
}
