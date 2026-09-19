using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingPlatform.Services;
using SkiaSharp;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class VisionRequestPreprocessingTests
{
    [TestMethod]
    public async Task SourceOver32MiBIsCompressedBelowInlineLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-vision-large-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new VisionArtifactStorageService(PuddingDataPaths.FromRoot(root), NullLogger<VisionArtifactStorageService>.Instance);
            using var bitmap = new SKBitmap(3500, 3500);
            var random = new Random(19);
            var pixels = new SKColor[3500 * 3500];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(128, 256));
            bitmap.Pixels = pixels;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var source = encoded.ToArray();
            Assert.IsTrue(source.Length > VisionRequestPolicy.Default.InlineMaxBytesPerImage);
            await using var stream = new MemoryStream(source);
            var saved = await service.SaveAsync("default", stream, "image/png");
            var prepared = await service.ResolveForRequestAsync("default", saved.ArtifactId, "original",
                new VisualArtifactPreparationOptions(8192, VisionRequestPolicy.Default.InlineMaxBytesPerImage));
            Assert.IsNotNull(prepared);
            Assert.IsTrue(Convert.FromBase64String(prepared.Uri.Split(',')[1]).Length <= VisionRequestPolicy.Default.InlineMaxBytesPerImage);
            var original = await service.ResolveLocalFileAsync("default", saved.ArtifactId);
            Assert.AreEqual(source.Length, new FileInfo(original!.Path).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RequestEdgeAndByteBudgetAreEnforcedWithoutChangingOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-vision-preprocess-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new VisionArtifactStorageService(PuddingDataPaths.FromRoot(root), NullLogger<VisionArtifactStorageService>.Instance);
            using var bitmap = new SKBitmap(5000, 80);
            var random = new Random(17);
            var pixels = new SKColor[5000 * 80];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            bitmap.Pixels = pixels;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var source = encoded.ToArray();
            await using var stream = new MemoryStream(source);
            var saved = await service.SaveAsync("default", stream, "image/png");
            var limits = new VisualArtifactPreparationOptions(4096, 16_000);
            var prepared = await service.ResolveForRequestAsync("default", saved.ArtifactId, "original", limits);
            Assert.IsNotNull(prepared);
            Assert.IsTrue(prepared.Width <= 4096 && prepared.Height <= 4096);
            Assert.IsTrue(Convert.FromBase64String(prepared.Uri.Split(',')[1]).Length <= 16_000);
            var original = await service.ResolveLocalFileAsync("default", saved.ArtifactId);
            CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(original!.Path));
            Assert.AreEqual(prepared.Uri, (await service.ResolveForRequestAsync("default", saved.ArtifactId, "original", limits))!.Uri);
            var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => service.ResolveForRequestAsync(
                "default", saved.ArtifactId, "original", new VisualArtifactPreparationOptions(4096, 1)));
            Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, error.Code);
            StringAssert.Contains(error.UserMessage, "仍可继续发送文字");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
