using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Configuration;
using PuddingCode.Models;

namespace PuddingPlatform.Services;

public sealed record AudioArtifactUploadResult(
    string ArtifactId,
    string MimeType,
    string Format,
    long? DurationMs,
    long CapturedAt);

/// <summary>
/// Stores provider-safe WAV audio beneath the workspace root. External resource
/// identifiers and client paths never become filesystem paths.
/// </summary>
public sealed class AudioArtifactStorageService(
    PuddingDataPaths dataPaths,
    ILogger<AudioArtifactStorageService> logger) :
    IAudioArtifactReferenceResolver,
    IAudioArtifactLocalFileResolver
{
    private const int MaxAudioBytes = 30 * 1024 * 1024;
    private static readonly Regex ArtifactIdRegex = new(
        @"^audio-[a-z0-9][a-z0-9-]{7,80}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex PathSegmentRegex = new(
        @"[^a-zA-Z0-9._-]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceSaveLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<AudioArtifactUploadResult> SaveAsync(
        string workspaceId,
        Stream content,
        long? durationMs = null,
        long? capturedAt = null,
        CancellationToken ct = default)
        => SaveIdempotentAsync(
            workspaceId,
            $"audio-{Guid.NewGuid():N}",
            content,
            durationMs,
            capturedAt,
            ct);

    public async Task<AudioArtifactUploadResult> SaveIdempotentAsync(
        string workspaceId,
        string artifactId,
        Stream content,
        long? durationMs = null,
        long? capturedAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidOperationException("Audio artifact upload requires a workspace id.");
        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Audio artifact upload requires readable content.");
        if (string.IsNullOrWhiteSpace(artifactId) || !ArtifactIdRegex.IsMatch(artifactId))
            throw new InvalidOperationException("Audio artifact id is invalid.");
        if (durationMs is <= 0)
            durationMs = null;

        var workspaceKey = SanitizePathSegment(workspaceId);
        var saveLock = _workspaceSaveLocks.GetOrAdd(
            workspaceKey,
            static _ => new SemaphoreSlim(1, 1));
        await saveLock.WaitAsync(ct);
        try
        {
            var existing = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
            if (existing is not null)
            {
                return new AudioArtifactUploadResult(
                    existing.ArtifactId,
                    existing.MimeType,
                    existing.Format,
                    existing.DurationMs,
                    existing.CapturedAt
                        ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            return await SaveCoreAsync(
                workspaceId,
                artifactId,
                content,
                durationMs,
                capturedAt,
                ct);
        }
        finally
        {
            saveLock.Release();
        }
    }

    private async Task<AudioArtifactUploadResult> SaveCoreAsync(
        string workspaceId,
        string artifactId,
        Stream content,
        long? durationMs,
        long? capturedAt,
        CancellationToken ct)
    {
        var storedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveCapturedAt = capturedAt ?? storedAt;
        var root = WorkspaceAudioRoot(workspaceId);
        Directory.CreateDirectory(root);

        var bytesPath = Path.Combine(root, $"{artifactId}.wav");
        var metadataPath = Path.Combine(root, $"{artifactId}.json");
        var tempSuffix = $".tmp-{Guid.NewGuid():N}";
        var tempBytesPath = bytesPath + tempSuffix;
        var tempMetadataPath = metadataPath + tempSuffix;

        try
        {
            await using (var file = new FileStream(
                tempBytesPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81_920];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaxAudioBytes)
                    {
                        throw new InvalidDataException(
                            $"Audio artifact exceeds {MaxAudioBytes} bytes.");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0)
                    throw new InvalidDataException("Audio artifact is empty.");
                await file.FlushAsync(ct);
            }
            ValidateProviderSafeWav(tempBytesPath);

            var metadata = new AudioArtifactMetadata(
                artifactId,
                "audio/wav",
                VoiceAudioFormats.Wav,
                Path.GetFileName(bytesPath),
                durationMs,
                effectiveCapturedAt,
                storedAt);
            await File.WriteAllTextAsync(
                tempMetadataPath,
                JsonSerializer.Serialize(metadata, JsonOptions),
                ct);
            File.Move(tempBytesPath, bytesPath, overwrite: true);
            File.Move(tempMetadataPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempBytesPath))
                File.Delete(tempBytesPath);
            if (File.Exists(tempMetadataPath))
                File.Delete(tempMetadataPath);
        }

        logger.LogInformation(
            "[AudioArtifact] Stored workspace={WorkspaceId} artifact={ArtifactId} durationMs={DurationMs}",
            workspaceId,
            artifactId,
            durationMs);
        return new AudioArtifactUploadResult(
            artifactId,
            "audio/wav",
            VoiceAudioFormats.Wav,
            durationMs,
            effectiveCapturedAt);
    }

    public async Task<AudioArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default)
    {
        var local = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
        if (local is null)
            return null;
        var bytes = await File.ReadAllBytesAsync(local.Path, ct);
        return new AudioArtifactReference(
            local.ArtifactId,
            $"data:{local.MimeType};base64,{Convert.ToBase64String(bytes)}",
            local.MimeType,
            local.Format,
            local.DurationMs,
            local.CapturedAt);
    }

    public async Task<AudioArtifactLocalFile?> ResolveLocalFileAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(artifactId)
            || !ArtifactIdRegex.IsMatch(artifactId))
        {
            return null;
        }

        var root = Path.GetFullPath(WorkspaceAudioRoot(workspaceId));
        var metadataPath = Path.Combine(root, $"{artifactId}.json");
        if (!File.Exists(metadataPath))
            return null;

        AudioArtifactMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<AudioArtifactMetadata>(
                await File.ReadAllTextAsync(metadataPath, ct),
                JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[AudioArtifact] Failed to read metadata artifact={ArtifactId}",
                artifactId);
            return null;
        }

        if (metadata is null
            || !string.Equals(metadata.ArtifactId, artifactId, StringComparison.Ordinal)
            || !string.Equals(metadata.Format, VoiceAudioFormats.Wav, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.MimeType, "audio/wav", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytesPath = Path.GetFullPath(Path.Combine(root, metadata.FileName));
        if (!bytesPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(bytesPath))
        {
            return null;
        }

        return new AudioArtifactLocalFile(
            metadata.ArtifactId,
            bytesPath,
            metadata.MimeType,
            metadata.Format,
            metadata.DurationMs,
            metadata.CapturedAt);
    }

    private string WorkspaceAudioRoot(string workspaceId)
        => Path.Combine(
            dataPaths.WorkspaceRoot(SanitizePathSegment(workspaceId)),
            "audio-artifacts");

    private static string SanitizePathSegment(string value)
    {
        var sanitized = PathSegmentRegex.Replace(value.Trim(), "_");
        return string.IsNullOrWhiteSpace(sanitized)
               || sanitized is "." or ".."
            ? "default"
            : sanitized;
    }

    private static void ValidateProviderSafeWav(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44
            || !reader.ReadBytes(4).AsSpan().SequenceEqual("RIFF"u8))
        {
            throw new InvalidDataException(
                "Audio artifact must be a PCM WAV file.");
        }

        _ = reader.ReadUInt32();
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException(
                "Audio artifact must be a PCM WAV file.");
        }

        var foundPcmFormat = false;
        var foundAudioData = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = reader.ReadBytes(4);
            var declaredSize = reader.ReadUInt32();
            var available = stream.Length - stream.Position;
            var readableSize = (long)Math.Min(declaredSize, available);

            if (chunkId.AsSpan().SequenceEqual("fmt "u8))
            {
                if (readableSize < 16)
                    break;
                var formatTag = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                var blockAlign = reader.ReadUInt16();
                var bitsPerSample = reader.ReadUInt16();
                foundPcmFormat = formatTag == 1
                    && channels is 1 or 2
                    && sampleRate is >= 8_000 and <= 96_000
                    && bitsPerSample == 16
                    && blockAlign == channels * 2;
                stream.Position += readableSize - 16;
            }
            else if (chunkId.AsSpan().SequenceEqual("data"u8))
            {
                foundAudioData = readableSize > 0;
                stream.Position += readableSize;
            }
            else
            {
                stream.Position += readableSize;
            }

            if ((declaredSize & 1) != 0 && stream.Position < stream.Length)
                stream.Position++;
            if (declaredSize > available)
                break;
        }

        if (!foundPcmFormat || !foundAudioData)
        {
            throw new InvalidDataException(
                "Audio artifact must contain 16-bit mono or stereo PCM WAV data.");
        }
    }

    private sealed record AudioArtifactMetadata(
        string ArtifactId,
        string MimeType,
        string Format,
        string FileName,
        long? DurationMs,
        long CapturedAt,
        long StoredAt);
}
