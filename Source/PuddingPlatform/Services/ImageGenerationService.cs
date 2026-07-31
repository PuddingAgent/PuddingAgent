using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// Resolves the configured image model, invokes its provider adapter, and stores
/// the result as a workspace-scoped Vision Artifact before returning.
/// </summary>
public sealed class ImageGenerationService(
    ILlmConfigService llmConfig,
    IEnumerable<IImageGenerationProvider> providers,
    VisionArtifactStorageService artifactStorage,
    ILogger<ImageGenerationService> logger)
    : IImageGenerationService
{
    private const int MaxPromptCharacters = 8_000;
    private const int MaxReferenceImages = 10;
    private const int MaxGeneratedImages = 4;
    private const long MinCustomPixels = 921_600;
    private const long MaxCustomPixels = 16_777_216;
    private static readonly Regex SizeRegex = new(
        @"^(?:[1-4]K|(?<width>[1-9][0-9]{1,4})[xX](?<height>[1-9][0-9]{1,4}))$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> operationLocks =
        new(StringComparer.Ordinal);

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.Prompt.Length > MaxPromptCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Prompt),
                $"Image prompt must not exceed {MaxPromptCharacters} characters.");
        }
        ValidateSize(request.Size);
        var outputFormat = request.OutputFormat.Trim().ToLowerInvariant();
        if (outputFormat is not ("png" or "jpeg"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.OutputFormat),
                "Image output format must be png or jpeg.");
        }
        var optimizeMode =
            request.OptimizePromptMode.Trim().ToLowerInvariant();
        if (optimizeMode is not ("standard" or "fast"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.OptimizePromptMode),
                "Image prompt optimization mode must be standard or fast.");
        }
        if (request.ImageCount is < 1 or > MaxGeneratedImages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ImageCount),
                $"Image count must be between 1 and {MaxGeneratedImages}.");
        }
        var referenceArtifactIds = request.ReferenceArtifactIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (referenceArtifactIds.Count > MaxReferenceImages
            || referenceArtifactIds.Count + request.ImageCount > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ReferenceArtifactIds),
                "At most 10 reference images and 15 total input/output images are allowed.");
        }

        var profile = ResolveProfile(request);
        var providerId = profile.ProviderId;
        var modelId = profile.ModelId;
        var resolved = profile.Config;
        var endpoint = resolved.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                $"Image generation provider '{providerId}' has no usable endpoint.");
        }
        var apiKey = resolved.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Image generation provider '{providerId}' has no usable API key.");
        }

        var inputImages = new List<string>(referenceArtifactIds.Count);
        foreach (var artifactId in referenceArtifactIds)
        {
            var reference = await artifactStorage.ResolveAsync(
                request.WorkspaceId,
                artifactId,
                ct);
            if (reference is null)
            {
                throw new InvalidOperationException(
                    $"Reference image artifact '{artifactId}' was not found in the current workspace.");
            }
            inputImages.Add(reference.Uri);
        }

        var stableArtifactId = request.ImageCount == 1
                               && !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? StableArtifactId(
                request.WorkspaceId,
                request.IdempotencyKey!,
                providerId,
                modelId,
                request.Prompt,
                request.Size,
                outputFormat,
                request.Watermark,
                referenceArtifactIds)
            : null;
        if (stableArtifactId is null)
        {
            return await GenerateCoreAsync(
                request,
                providerId,
                modelId,
                endpoint,
                apiKey,
                inputImages,
                outputFormat,
                optimizeMode,
                stableArtifactId: null,
                ct);
        }

        var operationLock = operationLocks.GetOrAdd(
            stableArtifactId,
            static _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync(ct);
        try
        {
            var existing = await artifactStorage.ResolveLocalFileAsync(
                request.WorkspaceId,
                stableArtifactId,
                ct);
            if (existing is not null)
            {
                logger.LogInformation(
                    "[ImageGeneration] Reused idempotent artifact workspace={WorkspaceId} artifact={ArtifactId}",
                    request.WorkspaceId,
                    stableArtifactId);
                return new ImageGenerationResult
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    Artifacts =
                    [
                        new ImageGenerationArtifact
                        {
                            ArtifactId = existing.ArtifactId,
                            MimeType = existing.MimeType,
                            Width = existing.Width,
                            Height = existing.Height,
                        },
                    ],
                };
            }

            return await GenerateCoreAsync(
                request,
                providerId,
                modelId,
                endpoint,
                apiKey,
                inputImages,
                outputFormat,
                optimizeMode,
                stableArtifactId,
                ct);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<ImageGenerationResult> GenerateCoreAsync(
        ImageGenerationRequest request,
        string providerId,
        string modelId,
        string endpoint,
        string apiKey,
        IReadOnlyList<string> inputImages,
        string outputFormat,
        string optimizeMode,
        string? stableArtifactId,
        CancellationToken ct)
    {
        var adapter = providers.FirstOrDefault(item =>
                item.CanHandle(providerId))
            ?? throw new InvalidOperationException(
                $"No image generation adapter is registered for provider '{providerId}'.");
        var generated = await adapter.GenerateAsync(
            new ImageGenerationProviderRequest
            {
                Endpoint = endpoint,
                ApiKey = apiKey,
                ModelId = modelId,
                Prompt = request.Prompt.Trim(),
                Size = request.Size.ToUpperInvariant(),
                Watermark = request.Watermark,
                OutputFormat = outputFormat,
                OptimizePromptMode = optimizeMode,
                EnableWebSearch = request.EnableWebSearch,
                ImageCount = request.ImageCount,
                InputImages = inputImages,
            },
            ct);

        if (generated.Images.Count is < 1 or > MaxGeneratedImages)
        {
            throw new InvalidOperationException(
                $"Image provider returned {generated.Images.Count} images; expected between 1 and {MaxGeneratedImages}.");
        }

        var artifacts = new List<ImageGenerationArtifact>(
            generated.Images.Count);
        for (var index = 0; index < generated.Images.Count; index++)
        {
            var image = generated.Images[index];
            await using var content = new MemoryStream(
                image.Content,
                writable: false);
            var artifact = stableArtifactId is not null && index == 0
                ? await artifactStorage.SaveIdempotentAsync(
                    request.WorkspaceId,
                    stableArtifactId,
                    content,
                    image.MimeType,
                    image.Width,
                    image.Height,
                    ct: ct)
                : await artifactStorage.SaveAsync(
                    request.WorkspaceId,
                    content,
                    image.MimeType,
                    image.Width,
                    image.Height,
                    ct: ct);
            artifacts.Add(new ImageGenerationArtifact
            {
                ArtifactId = artifact.ArtifactId,
                MimeType = artifact.MimeType,
                Width = artifact.Width,
                Height = artifact.Height,
            });
        }
        logger.LogInformation(
            "[ImageGeneration] Stored workspace={WorkspaceId} artifacts={ArtifactCount} provider={ProviderId} model={ModelId}",
            request.WorkspaceId,
            artifacts.Count,
            providerId,
            modelId);

        return new ImageGenerationResult
        {
            Artifacts = artifacts,
            ProviderId = providerId,
            ModelId = modelId,
        };
    }

    private LlmProfileInfo ResolveProfile(ImageGenerationRequest request)
    {
        var hasProvider = !string.IsNullOrWhiteSpace(request.ProviderId);
        var hasModel = !string.IsNullOrWhiteSpace(request.ModelId);
        if (hasProvider != hasModel)
        {
            throw new InvalidOperationException(
                "providerId and modelId must be supplied together.");
        }

        if (hasProvider)
        {
            return RequireProfile(
                request.ProviderId!.Trim(),
                request.ModelId!.Trim());
        }

        var mode = request.Mode.Trim().ToLowerInvariant();
        if (mode is "" or "default")
        {
            return llmConfig.GetImageGenerationProfile()
                   ?? throw new InvalidOperationException(
                       "No default image generation provider/model is configured.");
        }

        var capability = mode switch
        {
            "precision" => "image-editing",
            "sequence" => "sequential-image-generation",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request.Mode),
                "Image mode must be default, precision, or sequence."),
        };
        var model = llmConfig.GetAllModels()
            .Where(item =>
                !item.IsDeprecated
                && item.CapabilityTags.Contains(
                    "image-generation",
                    StringComparer.OrdinalIgnoreCase)
                && item.CapabilityTags.Contains(
                    capability,
                    StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => item.SortOrder)
            .FirstOrDefault(item =>
                llmConfig.Resolve(item.ProviderId, item.ModelId) is not null)
            ?? throw new InvalidOperationException(
                $"No configured image generation model provides capability '{capability}'.");
        return RequireProfile(model.ProviderId, model.ModelId);
    }

    private LlmProfileInfo RequireProfile(string providerId, string modelId)
    {
        var model = llmConfig.GetAllModels().FirstOrDefault(item =>
            string.Equals(
                item.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                item.ModelId,
                modelId,
                StringComparison.OrdinalIgnoreCase));
        if (model is null
            || model.IsDeprecated
            || !model.CapabilityTags.Contains(
                "image-generation",
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configured model '{providerId}/{modelId}' is not an enabled image generation model.");
        }

        var resolved = llmConfig.Resolve(providerId, modelId)
            ?? throw new InvalidOperationException(
                $"Image generation provider/model '{providerId}/{modelId}' could not be resolved.");
        return new LlmProfileInfo
        {
            ProviderId = providerId,
            ModelId = modelId,
            Config = resolved,
        };
    }

    private static void ValidateSize(string size)
    {
        var match = SizeRegex.Match(size.Trim());
        if (!match.Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                "Image size must be a 1K-4K tier or an explicit WIDTHxHEIGHT value.");
        }
        if (!match.Groups["width"].Success)
            return;

        var width = int.Parse(match.Groups["width"].Value);
        var height = int.Parse(match.Groups["height"].Value);
        var pixels = (long)width * height;
        var ratio = (double)width / height;
        if (pixels is < MinCustomPixels or > MaxCustomPixels
            || ratio is < (1d / 16d) or > 16d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                "Custom image size must contain 921600-16777216 pixels and use an aspect ratio between 1:16 and 16:1.");
        }
    }

    private static string StableArtifactId(
        string workspaceId,
        string idempotencyKey,
        string providerId,
        string modelId,
        string prompt,
        string size,
        string outputFormat,
        bool watermark,
        IReadOnlyList<string> references)
    {
        var raw = string.Join(
            '\n',
            workspaceId,
            idempotencyKey,
            providerId,
            modelId,
            prompt,
            size,
            outputFormat,
            watermark,
            string.Join(',', references));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"vision-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
