using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ImageGenerationServiceTests
{
    [TestMethod]
    public async Task GenerateAsync_UsesDefaultImageBindingAndStoresVisionArtifact()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-image-generation-{Guid.NewGuid():N}");
        try
        {
            var config = CreateConfig();
            var provider = new RecordingProvider();
            var artifacts = new VisionArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<VisionArtifactStorageService>.Instance);
            var service = new ImageGenerationService(
                config,
                [provider],
                artifacts,
                NullLogger<ImageGenerationService>.Instance);

            var result = await service.GenerateAsync(
                new ImageGenerationRequest
                {
                    WorkspaceId = "default",
                    Prompt = "一只布丁猫",
                    Size = "2K",
                });

            Assert.AreEqual("volcengine-ark", result.ProviderId);
            Assert.AreEqual(
                "doubao-seedream-5-0-260128",
                result.ModelId);
            StringAssert.StartsWith(result.ArtifactId, "vision-");
            Assert.IsNotNull(
                await artifacts.ResolveLocalFileAsync(
                    "default",
                    result.ArtifactId));
            Assert.AreEqual("一只布丁猫", provider.LastRequest?.Prompt);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GenerateAsync_PrecisionModeResolvesProAndPassesReferenceArtifact()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-image-generation-{Guid.NewGuid():N}");
        try
        {
            var config = CreateConfig();
            var provider = new RecordingProvider();
            var artifacts = new VisionArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<VisionArtifactStorageService>.Instance);
            await using var referenceBytes = new MemoryStream(
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var reference = await artifacts.SaveAsync(
                "default",
                referenceBytes,
                "image/png");
            var service = new ImageGenerationService(
                config,
                [provider],
                artifacts,
                NullLogger<ImageGenerationService>.Instance);

            var result = await service.GenerateAsync(
                new ImageGenerationRequest
                {
                    WorkspaceId = "default",
                    Prompt =
                        "把图 1 <bbox>120 180 640 760</bbox> 区域替换成花园",
                    Mode = "precision",
                    Size = "2K",
                    ReferenceArtifactIds = [reference.ArtifactId],
                    OptimizePromptMode = "fast",
                });

            Assert.AreEqual(
                "doubao-seedream-5-0-pro-260628",
                result.ModelId);
            Assert.AreEqual(
                "doubao-seedream-5-0-pro-260628",
                provider.LastRequest?.ModelId);
            Assert.HasCount(1, provider.LastRequest!.InputImages);
            StringAssert.StartsWith(
                provider.LastRequest.InputImages[0],
                "data:image/png;base64,");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GenerateAsync_SameIdempotencyKey_ReusesStoredArtifact()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-image-generation-{Guid.NewGuid():N}");
        try
        {
            var provider = new RecordingProvider();
            var artifacts = new VisionArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<VisionArtifactStorageService>.Instance);
            var service = new ImageGenerationService(
                CreateConfig(),
                [provider],
                artifacts,
                NullLogger<ImageGenerationService>.Instance);
            var request = new ImageGenerationRequest
            {
                WorkspaceId = "default",
                Prompt = "同一张布丁猫",
                Size = "2K",
                IdempotencyKey = "command-1:block-0",
            };

            var first = await service.GenerateAsync(request);
            var second = await service.GenerateAsync(request);

            Assert.AreEqual(first.ArtifactId, second.ArtifactId);
            Assert.AreEqual(1, provider.CallCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PuddingFileLlmConfigService CreateConfig()
        => new(new PuddingLlmProvidersConfig
        {
            ImageGeneration = new PuddingLlmImageGenerationConfig
            {
                ProviderId = "volcengine-ark",
                ModelId = "doubao-seedream-5-0-260128",
            },
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "volcengine-ark",
                    Name = "火山方舟",
                    BaseUrl =
                        "https://ark.cn-beijing.volces.com/api/v3",
                    ApiKey = "test-key",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId =
                                "doubao-seedream-5-0-260128",
                            Name = "Seedream",
                            CapabilityTags =
                            [
                                "image-generation",
                                "text-to-image",
                                "sequential-image-generation",
                            ],
                        },
                        new PuddingLlmModelConfig
                        {
                            ModelId =
                                "doubao-seedream-5-0-pro-260628",
                            Name = "Seedream Pro",
                            SortOrder = 2,
                            CapabilityTags =
                            [
                                "image-generation",
                                "text-to-image",
                                "image-editing",
                            ],
                        },
                    ],
                },
            ],
        });

    private sealed class RecordingProvider : IImageGenerationProvider
    {
        public ImageGenerationProviderRequest? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public bool CanHandle(string providerId)
            => providerId == "volcengine-ark";

        public Task<ImageGenerationProviderResult> GenerateAsync(
            ImageGenerationProviderRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(new ImageGenerationProviderResult
            {
                Images =
                [
                    new ImageGenerationProviderImage
                    {
                        Content =
                            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                        MimeType = "image/png",
                    },
                ],
            });
        }
    }
}
