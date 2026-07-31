using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingPlatform.Services;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FeishuImageUploadPreparationServiceTests
{
    [TestMethod]
    public async Task PrepareAsync_LargeImageCreatesBoundedCopyAndKeepsOriginal()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Assert.Inconclusive("System.Drawing delivery transcoding is Windows-only.");
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-feishu-image-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(
            root,
            "vision-0123456789abcdef0123456789abcdef.bmp");

        try
        {
            using (var bitmap = new Bitmap(2400, 1600))
            {
                bitmap.Save(path, ImageFormat.Bmp);
            }
            var originalLength = new FileInfo(path).Length;
            Assert.IsGreaterThan(
                FeishuImageUploadPreparationService.MaxUploadBytes,
                originalLength);

            var service = new FeishuImageUploadPreparationService(
                NullLogger<FeishuImageUploadPreparationService>.Instance);
            var payload = await service.PrepareAsync(
                new VisualArtifactLocalFile(
                    "vision-0123456789abcdef0123456789abcdef",
                    path,
                    "image/bmp",
                    2400,
                    1600,
                    null));

            Assert.IsTrue(payload.Transcoded);
            Assert.AreEqual("image/jpeg", payload.MimeType);
            Assert.IsLessThanOrEqualTo(
                FeishuImageUploadPreparationService.MaxUploadBytes,
                payload.Content.Length);
            Assert.AreEqual(0xFF, payload.Content[0]);
            Assert.AreEqual(0xD8, payload.Content[1]);
            Assert.AreEqual(originalLength, new FileInfo(path).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
