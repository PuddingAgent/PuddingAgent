using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class VisionArtifactStorageServiceTests
{
    [TestMethod]
    public async Task SaveAsync_Stores_And_Resolves_Server_Controlled_Data_Uri()
    {
        var root = CreateTempRoot();
        var service = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        // ADR-077：以 magic bytes 与结构字段为准；客户端声明 MIME/尺寸不参与事实。
        var png = VisionTestImages.MinimalPng();
        await using var stream = new MemoryStream(png);

        var saved = await service.SaveAsync(
            "default",
            stream,
            "image/jpg",
            width: 640,
            height: 480,
            capturedAt: 1234);

        StringAssert.StartsWith(saved.ArtifactId, "vision-");
        Assert.AreEqual("image/png", saved.MimeType);
        Assert.AreEqual(1, saved.Width);
        Assert.AreEqual(1, saved.Height);
        Assert.AreEqual(1234, saved.CapturedAt);

        var resolved = await service.ResolveAsync("default", saved.ArtifactId);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(saved.ArtifactId, resolved.ArtifactId);
        Assert.AreEqual("image/png", resolved.MimeType);
        Assert.AreEqual(1, resolved.Width);
        Assert.AreEqual(1, resolved.Height);
        Assert.AreEqual(1234, resolved.CapturedAt);
        Assert.AreEqual($"data:image/png;base64,{Convert.ToBase64String(png)}", resolved.Uri);

        var localFile = await service.ResolveLocalFileAsync("default", saved.ArtifactId);
        Assert.IsNotNull(localFile);
        Assert.IsTrue(Path.IsPathFullyQualified(localFile.Path));
        Assert.IsTrue(File.Exists(localFile.Path));
        Assert.AreEqual(saved.ArtifactId, localFile.ArtifactId);
    }

    [TestMethod]
    public async Task ResolveAsync_Rejects_Invalid_Artifact_Id()
    {
        var root = CreateTempRoot();
        var service = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);

        var resolved = await service.ResolveAsync("default", "../secret");

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public async Task SaveAsync_Rejects_Unsupported_Mime_Type()
    {
        var root = CreateTempRoot();
        var service = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        await using var stream = new MemoryStream([1, 2, 3]);

        var ex = await ThrowsUnsupportedMediaTypeAsync(() =>
            service.SaveAsync("default", stream, "text/plain"));

        StringAssert.Contains(ex.Message, "Unsupported");
        Assert.AreEqual("text/plain", ex.MimeType);
    }

    [TestMethod]
    public async Task SaveIdempotentAsync_ReusesStableArtifactAcrossConnectorRetry()
    {
        var root = CreateTempRoot();
        var service = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        const string artifactId = "vision-0123456789abcdef0123456789abcdef";
        var png = VisionTestImages.MinimalPng();
        await using var first = new MemoryStream(png);
        await using var retry = new MemoryStream(png);

        var saved = await service.SaveIdempotentAsync(
            "default",
            artifactId,
            first,
            "image/png",
            capturedAt: 1234);
        var reused = await service.SaveIdempotentAsync(
            "default",
            artifactId,
            retry,
            "image/png",
            capturedAt: 9999);

        Assert.AreEqual(artifactId, saved.ArtifactId);
        Assert.AreEqual(artifactId, reused.ArtifactId);
        Assert.AreEqual(1234, reused.CapturedAt);
        var resolved = await service.ResolveAsync("default", artifactId);
        Assert.IsNotNull(resolved);
        Assert.AreEqual($"data:image/png;base64,{Convert.ToBase64String(png)}", resolved.Uri);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-vision-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<UnsupportedVisionArtifactMediaTypeException>
        ThrowsUnsupportedMediaTypeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UnsupportedVisionArtifactMediaTypeException ex)
        {
            return ex;
        }

        Assert.Fail("Expected UnsupportedVisionArtifactMediaTypeException.");
        throw new InvalidOperationException("unreachable");
    }
}
