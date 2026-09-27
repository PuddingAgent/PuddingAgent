using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : IChatSpeechClient
{
    public Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        request.Validate();
        var message = await services.GetRequiredService<IChatMessageRepository>().GetByMessageIdAsync(request.MessageId, token)
            ?? throw new InvalidOperationException("消息不存在。");
        var session = await services.GetRequiredService<ISessionRepository>().GetAsync(message.SessionId, token);
        if (message.Role is not ("assistant" or "agent") || message.WorkspaceId != request.Role.WorkspaceId
            || session is null || session.WorkspaceId != request.Role.WorkspaceId || session.OwnerUserId != LocalUserId
            || (session.PrincipalId ?? session.AgentInstanceId) != request.Role.AgentId)
            throw new InvalidOperationException("消息与当前角色归属不匹配。");
        // Read authoritative content instead of trusting an edited/stale UI text snapshot.
        var envelope = AgentContextEnvelopeRenderer.TryParse(message.Content);
        var text = !string.IsNullOrWhiteSpace(envelope?.Context.Text) ? envelope.Context.Text : message.Content;
        var canonical = request with { Text = text }; canonical.Validate();
        var result = await services.GetRequiredService<IVoiceSynthesisService>().SynthesizeAsync(new VoiceSynthesisRequest
        {
            WorkspaceId = request.Role.WorkspaceId, MessageId = request.MessageId, Text = canonical.Text,
            Model = "", Voice = "", AudioFormat = VoiceAudioFormats.Wav, SampleRate = 24_000
        }, token);
        var audio = new SpeechAudio(result.AudioBytes ?? [], result.Format.ToLowerInvariant() switch
        {
            "wav" => "audio/wav", "mp3" => "audio/mpeg", _ => throw new InvalidDataException("不支持的语音格式。")
        });
        audio.Validate(); return audio;
    }, ct);
}
