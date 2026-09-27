using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingNativeChat.IntegrationTests;

public partial class NativeChatIntegrationTests
{
    private static async Task VerifySpeechAsync(InProcessKernel kernel, IChatClient productClient, RoleKey role, string session, CancellationToken ct)
    {
        Assert.IsAssignableFrom<IChatSpeechClient>(productClient);
        await kernel.RunSettingsAsync("test-native-speech", async (scope, token) =>
        {
            var id = "speech-" + Guid.NewGuid().ToString("N");
            var db = scope.Services.GetRequiredService<PlatformDbContext>();
            db.ChatMessages.Add(new ChatMessageEntity { MessageId = id, SessionId = session, WorkspaceId = role.WorkspaceId,
                AgentInstanceId = role.AgentId, Role = "agent", Content = AgentContextEnvelopeRenderer.RenderForAgent(new AgentContextEnvelope {
                    MessageId = id, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MessageType = "agent_output", ContentType = "text", WorkspaceId = role.WorkspaceId,
                    From = new("agent", role.AgentId, "角色"), To = [], Constraints = [], Context = new("text", "canonical speech text") }), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await db.SaveChangesAsync(token);
            var voice = new SpeechServiceFixture();
            await using var services = new ServiceCollection()
                .AddSingleton(scope.Services.GetRequiredService<IChatMessageRepository>())
                .AddSingleton(scope.Services.GetRequiredService<ISessionRepository>())
                .AddSingleton<IVoiceSynthesisService>(voice).BuildServiceProvider();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var client = new InProcessChatClient(services.GetRequiredService<IServiceScopeFactory>(), stopping.Token);
            var request = new SpeechRequest(role, id, "stale UI snapshot");
            var audio = await client.SynthesizeAsync(request, token);
            Assert.Equal("audio/wav", audio.MimeType); Assert.NotEmpty(audio.Bytes);
            Assert.Equal("canonical speech text", voice.Request!.Text);
            Assert.Equal(role.WorkspaceId, voice.Request.WorkspaceId); Assert.Equal(id, voice.Request.MessageId);
            Assert.Equal(VoiceSynthesisProviders.Unknown, voice.Request.Provider); Assert.Equal("", voice.Request.Model);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(request with { Role = role with { AgentId = "other" } }, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(request with { Role = role with { WorkspaceId = "native-new" } }, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(request with { MessageId = "missing" }, token));
            Assert.Equal(1, voice.Calls);
            voice.Format = "unknown";
            await Assert.ThrowsAsync<InvalidDataException>(() => client.SynthesizeAsync(request, token));
            voice.Block = true;
            var pending = client.SynthesizeAsync(request, token);
            await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(voice.Token.IsCancellationRequested);
            return true;
        }, ct);
    }
    private sealed class SpeechServiceFixture : IVoiceSynthesisService
    {
        public VoiceSynthesisRequest? Request;
        public int Calls;
        public string Format = "wav";
        public bool Block;
        public CancellationToken Token;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VoiceSynthesisResult> SynthesizeAsync(VoiceSynthesisRequest request, CancellationToken ct = default)
        {
            Calls++; Request = request; Token = ct;
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return new() { MessageId = request.MessageId, AudioBytes = [1, 2, 3], Format = Format, Provider = "fixture", Model = "fixture" };
        }
        public IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(VoiceSynthesisRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
