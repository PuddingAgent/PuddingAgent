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
        await using var stream = new MemoryStream([1, 2, 3, 4]);

        var saved = await service.SaveAsync(
            "default",
            stream,
            "image/jpg",
            width: 640,
            height: 480,
            capturedAt: 1234);

        StringAssert.StartsWith(saved.ArtifactId, "vision-");
        Assert.AreEqual("image/jpeg", saved.MimeType);
        Assert.AreEqual(640, saved.Width);
        Assert.AreEqual(480, saved.Height);
        Assert.AreEqual(1234, saved.CapturedAt);

        var resolved = await service.ResolveAsync("default", saved.ArtifactId);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(saved.ArtifactId, resolved.ArtifactId);
        Assert.AreEqual("image/jpeg", resolved.MimeType);
        Assert.AreEqual(640, resolved.Width);
        Assert.AreEqual(480, resolved.Height);
        Assert.AreEqual(1234, resolved.CapturedAt);
        Assert.AreEqual("data:image/jpeg;base64,AQIDBA==", resolved.Uri);

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

        var ex = await ThrowsInvalidOperationAsync(() =>
            service.SaveAsync("default", stream, "text/plain"));

        StringAssert.Contains(ex.Message, "Unsupported");
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-vision-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<InvalidOperationException> ThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        Assert.Fail("Expected InvalidOperationException.");
        throw new InvalidOperationException("unreachable");
    }
}
