using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingAgent.Tools;

[Tool(
    id: "asr",
    name: "Audio Speech Recognition",
    description: "Transcribe a workspace-authorized local WAV audio artifact. Use this when an attached-audio notice says the current model has no native audio access.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresNetwork,
    SortOrder = 36)]
public sealed class AsrTool(
    IAudioArtifactLocalFileResolver localFileResolver,
    IAudioTranscriptionService transcriptionService,
    ILogger<AsrTool> logger) : PuddingToolBase<AsrToolArgs>
{
    private const int MaxAudioBytes = 30 * 1024 * 1024;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AsrToolArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var path = args.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return ToolExecutionResult.Fail("path is required.");
        if (!Path.IsPathFullyQualified(path))
            return ToolExecutionResult.Fail("path must be an absolute local file path.");

        var fullPath = Path.GetFullPath(path);
        var artifactId = Path.GetFileNameWithoutExtension(fullPath);
        var authorized = await localFileResolver.ResolveLocalFileAsync(
            context.WorkspaceId,
            artifactId,
            ct);
        if (authorized is null
            || !string.Equals(
                Path.GetFullPath(authorized.Path),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Fail(
                "path is not an authorized audio artifact in the current workspace.");
        }
        if (!string.Equals(
                authorized.Format,
                VoiceAudioFormats.Wav,
                StringComparison.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Fail(
                $"Unsupported audio artifact format '{authorized.Format}'.");
        }

        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > MaxAudioBytes)
            return ToolExecutionResult.Fail("Audio must be between 1 byte and 30 MB.");
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        var result = await transcriptionService.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                Content = bytes,
                Format = authorized.Format,
                Language = string.IsNullOrWhiteSpace(args.Language)
                    ? null
                    : args.Language.Trim(),
            },
            ct);

        logger.LogInformation(
            "[AsrTool] Transcribed artifact={ArtifactId} provider={ProviderId} model={ModelId} transcriptLength={TranscriptLength}",
            artifactId,
            result.Provider,
            result.Model,
            result.Text.Length);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            text = result.Text,
            emotion = result.Emotion,
            provider = result.Provider,
            model = result.Model,
            trust = "untrusted_user_audio",
        }));
    }
}

public sealed record AsrToolArgs
{
    [ToolParam("Exact absolute WAV path from the attached-audio notice")]
    public string? Path { get; init; }

    [ToolParam("Optional recognition language such as zh-CN; omit for automatic detection")]
    public string? Language { get; init; }
}
