using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>Resolved server-side visual artifact reference safe to pass to Core visual reasoning.</summary>
public sealed record VisualArtifactReference(
    string ArtifactId,
    string Uri,
    string MimeType,
    int? Width,
    int? Height,
    long? CapturedAt);

/// <summary>Resolves a user-facing artifact id into a server-authorized URI.</summary>
public interface IVisualArtifactReferenceResolver
{
    Task<VisualArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

/// <summary>
/// Converts chat camera metadata into a Core visual reasoning request.
/// The factory intentionally ignores client-supplied artifact URIs; only a resolver may
/// issue the controlled URI used by downstream provider calls.
/// </summary>
public sealed class ChatVisualReasoningRequestFactory(IVisualArtifactReferenceResolver artifactResolver)
{
    public async Task<VisualReasoningRequest> BuildAsync(
        string workspaceId,
        string roomId,
        string participantId,
        AdminChatRequest chatRequest,
        string provider,
        string model,
        string? traceId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>(
            chatRequest.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        if (!metadata.TryGetValue("inputMode", out var inputMode)
            || !string.Equals(inputMode, "camera", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Visual reasoning request requires camera input metadata.");
        }

        if (!metadata.TryGetValue("visionArtifactId", out var artifactId)
            || string.IsNullOrWhiteSpace(artifactId))
        {
            throw new InvalidOperationException("Camera visual reasoning request requires visionArtifactId metadata.");
        }

        var artifact = await artifactResolver.ResolveAsync(workspaceId, artifactId, ct);
        if (artifact is null)
        {
            throw new InvalidOperationException(
                $"Camera visual artifact '{artifactId}' could not be resolved for workspace '{workspaceId}'.");
        }

        var cameraSessionId = metadata.TryGetValue("cameraSessionId", out var sid) && !string.IsNullOrWhiteSpace(sid)
            ? sid
            : chatRequest.SessionId ?? Guid.NewGuid().ToString("N");

        return new VisualReasoningRequest
        {
            WorkspaceId = workspaceId,
            RoomId = roomId,
            ParticipantId = participantId,
            SessionId = cameraSessionId,
            Provider = provider,
            Model = model,
            Transport = VisualReasoningTransports.OpenAiCompatibleSse,
            OutputMode = VisualReasoningOutputModes.Streaming,
            ThinkingMode = VisualReasoningThinkingModes.Toggleable,
            EnableThinking = true,
            Prompt = chatRequest.MessageText,
            TraceId = traceId,
            Inputs =
            [
                new VisualInputArtifact
                {
                    ArtifactId = artifact.ArtifactId,
                    Kind = VisualInputKinds.CameraFrame,
                    MimeType = artifact.MimeType,
                    Uri = artifact.Uri,
                    Width = artifact.Width,
                    Height = artifact.Height,
                    CapturedAt = artifact.CapturedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            ],
            Metadata = BuildMetadata(metadata, artifact),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(
        IReadOnlyDictionary<string, string> source,
        VisualArtifactReference artifact)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["inputMode"] = "camera",
            ["visionArtifactId"] = artifact.ArtifactId,
        };

        if (source.TryGetValue("cameraSessionId", out var cameraSessionId)
            && !string.IsNullOrWhiteSpace(cameraSessionId))
        {
            metadata["cameraSessionId"] = cameraSessionId;
        }

        if (!string.IsNullOrWhiteSpace(artifact.MimeType))
            metadata["mimeType"] = artifact.MimeType;
        if (artifact.Width is { } width)
            metadata["width"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (artifact.Height is { } height)
            metadata["height"] = height.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return metadata;
    }
}
