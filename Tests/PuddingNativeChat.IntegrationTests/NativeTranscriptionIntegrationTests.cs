using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;
using PuddingPlatform.Data;

namespace PuddingNativeChat.IntegrationTests;

public partial class NativeChatIntegrationTests
{
    private static async Task VerifyVoiceMetadataAsync(InProcessKernel kernel, string messageId, VoiceInputOrigin expected, CancellationToken ct)
    {
        await kernel.RunSettingsAsync("test-voice-metadata", async (scope, token) =>
        {
            var message = await scope.Services.GetRequiredService<PlatformDbContext>().ChatMessages.AsNoTracking()
                .SingleAsync(m => m.MessageId == messageId, token);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(message.MetadataJson!)!;
            foreach (var pair in expected.ToMetadata()) Assert.Equal(pair.Value, metadata[pair.Key]);
            Assert.False(metadata.ContainsKey("language"));
            return true;
        }, ct);
    }
    private static async Task VerifyTranscriptionAsync(InProcessKernel kernel, IChatClient productClient, RoleKey role, string testRoot, CancellationToken ct)
    {
        Assert.IsAssignableFrom<IChatTranscriptionClient>(productClient);
        await kernel.RunSettingsAsync("test-native-transcription", async (scope, token) =>
        {
            // This isolated configuration is under the integration fixture's own temporary data root.
            var paths = PuddingDataPaths.FromRoot(Path.Combine(testRoot, "asr-fixture"));
            var path = paths.SystemConfigFile("voice/providers.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var config = new PuddingVoiceProvidersConfig
            {
                DefaultAsrProviderId = "fixture", DefaultAsrModelId = "recognition",
                Providers = [new() { ProviderId = "fixture", Name = "Fixture", Endpoint = "https://example.invalid", IsEnabled = true,
                    AsrModels = [new() { ModelId = "recognition", Name = "Recognition", IsDefault = true }] }]
            };
            Task SaveAsync(PuddingVoiceProvidersConfig value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
            var factory = new TranscriptionProviderFixture();
            var service = new AudioTranscriptionService(new VoiceProviderFileService(paths, NullLogger<VoiceProviderFileService>.Instance),
                factory, NullLogger<AudioTranscriptionService>.Instance);
            await using var services = new ServiceCollection()
                .AddSingleton(scope.Services.GetRequiredService<WorkspaceAgentFileService>())
                .AddSingleton<IAudioTranscriptionService>(service).BuildServiceProvider();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var client = new InProcessChatClient(services.GetRequiredService<IServiceScopeFactory>(), stopping.Token);
            var audio = new RecordedSpeech([1, 2, 3]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(role, audio, token));
            Assert.Equal(0, factory.Calls);
            await SaveAsync(config);
            var transcript = await client.TranscribeAsync(role, audio, token);
            Assert.Equal("recognized words", transcript.Text); Assert.Equal("fixture", transcript.Provider);
            Assert.Equal("recognition", transcript.Model); Assert.Null(transcript.Language);
            Assert.Equal("fixture", factory.Provider); Assert.Equal("recognition", factory.Model);
            Assert.Equal("wav", factory.Format); Assert.Null(factory.Language); Assert.Equal(audio.Bytes, factory.Bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(role with { AgentId = "missing" }, audio, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(role with { WorkspaceId = "missing" }, audio, token));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.TranscribeAsync(role, new([]), token));
            Assert.Equal(1, factory.Calls);
            factory.Text = " ";
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(role, audio, token));
            factory.Block = true;
            var pending = client.TranscribeAsync(role, audio, token);
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(factory.Token.IsCancellationRequested);
            return true;
        }, ct);
    }

    private sealed class TranscriptionProviderFixture : IVoiceProviderFactory, IAsrHttpRecognizer
    {
        public string? Provider, Model, Format, Language;
        public byte[]? Bytes;
        public string Text = " recognized words ";
        public int Calls;
        public bool Block;
        public CancellationToken Token;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ITtsProvider CreateTtsProvider(PuddingVoiceProvidersConfig config, string? providerId = null, string? modelId = null) => throw new NotSupportedException();
        public IAsrHttpRecognizer CreateAsrProvider(PuddingVoiceProvidersConfig config, string? providerId = null, string? modelId = null)
        { Provider = providerId; Model = modelId; return this; }
        public async Task<AsrRecognizeResult> RecognizeAsync(byte[] audioData, string format, string? language, CancellationToken ct)
        {
            Calls++; Bytes = audioData; Format = format; Language = language; Token = ct;
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return new(Text, null);
        }
    }
}
