using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingWebApiTests.Tools;

[TestClass]
public sealed class AsrToolTests
{
    [TestMethod]
    public async Task ExecuteAsync_AuthorizedWorkspaceArtifact_InvokesTranscription()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-asr-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var storage = new AudioArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<AudioArtifactStorageService>.Instance);
            const string artifactId =
                "audio-0123456789abcdef0123456789abcdef";
            await using var stream = new MemoryStream(CreatePcm16Wav());
            await storage.SaveIdempotentAsync(
                "default",
                artifactId,
                stream);
            var local = await storage.ResolveLocalFileAsync(
                "default",
                artifactId);
            Assert.IsNotNull(local);
            var transcription = new RecordingTranscriptionService();
            var tool = new AsrTool(
                storage,
                transcription,
                NullLogger<AsrTool>.Instance);

            var result = await tool.ExecuteAsync(
                Request(local.Path, language: "zh-CN"));

            Assert.IsTrue(result.Success, result.Error);
            Assert.IsNotNull(transcription.LastRequest);
            Assert.AreEqual(
                VoiceAudioFormats.Wav,
                transcription.LastRequest.Format);
            Assert.AreEqual(
                "zh-CN",
                transcription.LastRequest.Language);
            using var output = JsonDocument.Parse(result.Output);
            Assert.AreEqual(
                "今天天气很好",
                output.RootElement.GetProperty("text").GetString());
            Assert.AreEqual(
                "untrusted_user_audio",
                output.RootElement.GetProperty("trust").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrelatedAbsolutePath_IsRejected()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-asr-tool-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var unrelated = Path.Combine(root, "unrelated.wav");
            await File.WriteAllBytesAsync(unrelated, [82, 73, 70, 70]);
            var storage = new AudioArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<AudioArtifactStorageService>.Instance);
            var transcription = new RecordingTranscriptionService();
            var tool = new AsrTool(
                storage,
                transcription,
                NullLogger<AsrTool>.Instance);

            var result = await tool.ExecuteAsync(Request(unrelated));

            Assert.IsFalse(result.Success);
            StringAssert.Contains(
                result.Error ?? "",
                "not an authorized audio artifact");
            Assert.IsNull(transcription.LastRequest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ToolExecutionRequest Request(
        string path,
        string? language = null) => new()
        {
            ToolCallId = "tool-call-asr",
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                path,
                language,
            }),
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                AgentTemplateId = "template-1",
            },
        };

    private static byte[] CreatePcm16Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(40);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(4);
        writer.Write(new byte[] { 0, 0, 0, 0 });
        return stream.ToArray();
    }

    private sealed class RecordingTranscriptionService :
        IAudioTranscriptionService
    {
        public AudioTranscriptionRequest? LastRequest { get; private set; }

        public Task<AudioTranscriptionResult> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(
                new AudioTranscriptionResult
                {
                    Text = "今天天气很好",
                    Emotion = "happy",
                    Provider = "test-provider",
                    Model = "test-model",
                });
        }
    }
}
