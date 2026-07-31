using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AudioArtifactStorageServiceTests
{
    [TestMethod]
    public async Task SaveIdempotentAsync_StoresProviderSafeWavAndReusesFirstCopy()
    {
        var root = CreateTempRoot();
        var service = new AudioArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<AudioArtifactStorageService>.Instance);
        const string artifactId = "audio-0123456789abcdef0123456789abcdef";
        var expected = CreatePcm16Wav([1, 2, 3, 4]);
        await using var first = new MemoryStream(expected);
        await using var retry = new MemoryStream(CreatePcm16Wav([9, 9, 9, 9]));

        var saved = await service.SaveIdempotentAsync(
            "default",
            artifactId,
            first,
            durationMs: 1234,
            capturedAt: 5678);
        var reused = await service.SaveIdempotentAsync(
            "default",
            artifactId,
            retry,
            durationMs: 9999,
            capturedAt: 9999);

        Assert.AreEqual(artifactId, saved.ArtifactId);
        Assert.AreEqual("audio/wav", reused.MimeType);
        Assert.AreEqual("wav", reused.Format);
        Assert.AreEqual(1234, reused.DurationMs);
        Assert.AreEqual(5678, reused.CapturedAt);

        var resolved = await service.ResolveAsync("default", artifactId);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(
            $"data:audio/wav;base64,{Convert.ToBase64String(expected)}",
            resolved.Uri);
        var local = await service.ResolveLocalFileAsync("default", artifactId);
        Assert.IsNotNull(local);
        Assert.IsTrue(Path.IsPathFullyQualified(local.Path));
        Assert.IsTrue(File.Exists(local.Path));
    }

    [TestMethod]
    public async Task ResolveAsync_RejectsPathTraversalArtifactId()
    {
        var service = new AudioArtifactStorageService(
            PuddingDataPaths.FromRoot(CreateTempRoot()),
            NullLogger<AudioArtifactStorageService>.Instance);

        Assert.IsNull(await service.ResolveAsync("default", "../secret"));
    }

    [TestMethod]
    public async Task SaveIdempotentAsync_RejectsMislabeledNonWavContent()
    {
        var service = new AudioArtifactStorageService(
            PuddingDataPaths.FromRoot(CreateTempRoot()),
            NullLogger<AudioArtifactStorageService>.Instance);
        await using var content = new MemoryStream([1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveIdempotentAsync(
                "default",
                "audio-abcdef0123456789abcdef0123456789",
                content));
    }

    internal static byte[] CreatePcm16Wav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
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
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return stream.ToArray();
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-audio-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
