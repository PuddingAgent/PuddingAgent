using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : IChatTranscriptionClient
{
    public Task<VoiceTranscript> TranscribeAsync(RoleKey role, RecordedSpeech audio, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(role.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role.AgentId);
        audio.Validate();
        var agent = await services.GetRequiredService<WorkspaceAgentFileService>().GetAgentAsync(role.WorkspaceId, role.AgentId, token);
        if (agent is null || !agent.IsEnabled || agent.IsFrozen)
            throw new InvalidOperationException("角色不存在、已停用或冻结。");
        var result = await services.GetRequiredService<IAudioTranscriptionService>().TranscribeAsync(new AudioTranscriptionRequest
        {
            Content = audio.Bytes, Format = VoiceAudioFormats.Wav
        }, token);
        token.ThrowIfCancellationRequested();
        return new VoiceTranscript(result.Text, result.Provider, result.Model);
    }, ct);
}
