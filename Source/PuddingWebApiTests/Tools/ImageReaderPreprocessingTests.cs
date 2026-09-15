using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Tools;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Services;
using SkiaSharp;

namespace PuddingWebApiTests.Tools;

[TestClass]
public sealed class ImageReaderPreprocessingTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-reader-preprocess-" + Guid.NewGuid().ToString("N"));
    private VisionArtifactStorageService Storage() => new(PuddingDataPaths.FromRoot(_root), NullLogger<VisionArtifactStorageService>.Instance);
    private string Image(int width = 1600, int height = 800)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".unknown");
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Red);
        using var paint = new SKPaint { Color = SKColors.Blue };
        surface.Canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
    private static async Task<ToolExecutionResult> Run(VisionArtifactStorageService storage, object args, bool vision = true)
    {
        var tool = new ImageReaderTool(new ImageReaderSourceResolver(storage, null, NullLogger<ImageReaderSourceResolver>.Instance),
            storage, NullLogger<ImageReaderTool>.Instance);
        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"), ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext { WorkspaceId = "default", SessionId = "chat", AgentInstanceId = "test-agent", CallerLlmSnapshot =
                vision ? new LlmRouteSnapshot("deepseek", "deepseek-flash", "responses", ["vision"]) : null },
        });
    }
    private static JsonDocument Metadata(ToolExecutionResult result)
    {
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output![(result.Output!.IndexOf('\n') + 1)..]);
    }
    private static async Task<SKBitmap> Pixels(VisionArtifactStorageService storage, ToolExecutionResult result)
    {
        Assert.IsTrue(result.Success, result.Error);
        var part = (LlmImagePart)result.ToolContentParts![0];
        var file = await storage.ResolveLocalFileAsync("default", part.ArtifactId);
        return SKBitmap.Decode(file!.Path);
    }

    [TestMethod]
    public async Task MetadataNeedsNoVisionAndReturnsActualFormatWithoutImageContent()
    {
        var result = await Run(Storage(), new { path = Image(100, 50), action = "metadata" }, vision: false);
        using var metadata = Metadata(result);
        Assert.IsTrue(result.ToolContentParts is not { Count: > 0 });
        var info = metadata.RootElement.GetProperty("source").GetProperty("info");
        Assert.AreEqual("image/png", info.GetProperty("MimeType").GetString());
        Assert.AreEqual(100, info.GetProperty("Width").GetInt32());
        Assert.IsTrue(info.GetProperty("Bytes").GetInt64() > 0);
    }

    [TestMethod]
    public async Task LowCreatesSmallPixelsAndRepeatedReadReusesArtifact()
    {
        var storage = Storage(); var path = Image(); var before = File.ReadAllBytes(path);
        var first = await Run(storage, new { path, detail = "low" });
        var second = await Run(storage, new { path, detail = "low" });
        using var bitmap = await Pixels(storage, first);
        Assert.AreEqual(512, bitmap.Width); Assert.AreEqual(256, bitmap.Height);
        Assert.AreEqual("low", ((LlmImagePart)first.ToolContentParts![0]).Detail);
        Assert.AreEqual(((LlmImagePart)first.ToolContentParts[0]).ArtifactId, ((LlmImagePart)second.ToolContentParts![0]).ArtifactId);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task LargePreviewThenTwoOriginalRegionsPreservesIndependentDetails()
    {
        var storage = Storage(); var path = Image(9000, 40);
        var preview = await Run(storage, new { path, transform = "thumbnail" });
        using var metadata = Metadata(preview);
        var source = metadata.RootElement.GetProperty("source").GetProperty("reference").GetString();
        using var thumbnail = await Pixels(storage, preview); Assert.AreEqual(512, thumbnail.Width);
        var left = await Run(storage, new { path = source, transform = "crop", crop_x = 0, crop_y = 0, crop_width = 100, crop_height = 40 });
        var right = await Run(storage, new { path = source, transform = "crop", crop_x = 8800, crop_y = 0, crop_width = 100, crop_height = 40 });
        using var a = await Pixels(storage, left); using var b = await Pixels(storage, right);
        Assert.AreEqual(100, a.Width); Assert.AreEqual(SKColors.Red, a.GetPixel(50,20)); Assert.AreEqual(SKColors.Blue, b.GetPixel(50,20));
    }

    [TestMethod]
    public async Task PrepareCombinesCropRotationGrayAndEncodingWithoutImageThenCanRead()
    {
        var storage = Storage(); var path = Image(100, 80);
        var prepared = await Run(storage, new { path, action = "prepare", transform = "preprocess", crop_x = 0, crop_y = 0,
            crop_width = 40, crop_height = 20, rotation = 90, max_edge = 30, grayscale = true, format = "jpeg", quality = 70 });
        using var metadata = Metadata(prepared); Assert.IsTrue(prepared.ToolContentParts is not { Count: > 0 });
        var reference = metadata.RootElement.GetProperty("result").GetProperty("reference").GetString();
        var read = await Run(storage, new { path = reference });
        using var bitmap = await Pixels(storage, read);
        Assert.AreEqual(15, bitmap.Width); Assert.AreEqual(30, bitmap.Height);
        var pixel = bitmap.GetPixel(7,15); Assert.IsTrue(Math.Abs(pixel.Red - pixel.Blue) <= 1 && Math.Abs(pixel.Red - pixel.Green) <= 1);
        Assert.AreEqual("image/jpeg", metadata.RootElement.GetProperty("result").GetProperty("info").GetProperty("MimeType").GetString());
    }

    [TestMethod]
    public async Task DenoiseSoftensColorBoundaryOnlyWhenRequested()
    {
        var storage = Storage(); var path = Image(40,20);
        var original = await Run(storage, new { path }); var filtered = await Run(storage, new { path, denoise = true });
        using var a = await Pixels(storage, original); using var b = await Pixels(storage, filtered);
        Assert.AreEqual((byte)0, a.GetPixel(19,10).Blue); Assert.IsTrue(b.GetPixel(19,10).Blue > 0);
        Assert.AreEqual(a.Width, b.Width);
    }

    [TestMethod]
    [DataRow("low")][DataRow("high")][DataRow("original")][DataRow("auto")]
    public async Task DetailIsCarriedInTypedToolResult(string detail)
    {
        var result = await Run(Storage(), new { path = Image(20,10), detail });
        Assert.IsTrue(result.Success, result.Error); Assert.AreEqual(detail, ((LlmImagePart)result.ToolContentParts![0]).Detail);
    }

    [TestMethod]
    public async Task CropOverflowIsRejectedBeforeDecode()
    {
        var result = await Run(Storage(), new { path = Image(20,10), crop_x = int.MaxValue, crop_y = 0, crop_width = int.MaxValue, crop_height = 1 });
        Assert.IsFalse(result.Success); StringAssert.Contains(result.Error!, VisionErrorCodes.SourceInvalid);
    }

    [TestMethod]
    public async Task ChatAttachmentLowIsPreparedBeforeResponsesWireAndOriginalRemains()
    {
        var storage = Storage(); var path = Image();
        await using var stream = File.OpenRead(path);
        var saved = await storage.SaveAsync("default", stream, "text/plain"); // actual bytes, not MIME/extension
        var handler = new Capture();
        var gateway = new ResponsesLlmGateway(new HttpClient(handler), new LlmOptions("https://example.test/v1", "test", "deepseek-flash"))
        {
            WorkspaceId = "default", VisualArtifactResolver = new VisualArtifactResolverBridge(storage),
        };
        await gateway.ChatAsync([new ChatMessage(ChatRole.User, "inspect", ContentParts: [new LlmImagePart(saved.ArtifactId, "low")])], []);
        using var body = JsonDocument.Parse(handler.Body!);
        var image = body.RootElement.GetProperty("input")[0].GetProperty("content").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "input_image");
        Assert.AreEqual("low", image.GetProperty("detail").GetString());
        var uri = image.GetProperty("image_url").GetString()!;
        using var pixels = SKBitmap.Decode(Convert.FromBase64String(uri[(uri.IndexOf(',')+1)..]));
        Assert.AreEqual(512, pixels.Width);
        var original = await storage.ResolveLocalFileAsync("default", saved.ArtifactId);
        Assert.AreEqual(1600, original!.Width);
    }

    [TestMethod]
    public async Task ExifRotationUsesUprightDimensionsAndPixels()
    {
        var path = Image(40,20);
        using (var bitmap = SKBitmap.Decode(path))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100))
        {
            var bytes = jpeg.ToArray();
            var exif = Convert.FromHexString("FFE1002245786966000049492A0008000000010012010300010000000600000000000000");
            File.WriteAllBytes(path, bytes[..2].Concat(exif).Concat(bytes[2..]).ToArray());
        }
        var storage = Storage(); var result = await Run(storage, new { path });
        using var pixels = await Pixels(storage, result);
        Assert.AreEqual(20, pixels.Width); Assert.AreEqual(40, pixels.Height);
        Assert.IsTrue(pixels.GetPixel(10,5).Red > 240); Assert.IsTrue(pixels.GetPixel(10,35).Blue > 240);
    }

    [TestMethod]
    public async Task GifSourceIsPreservedAndFirstFrameBecomesNativePng()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root,"frame.gif");
        var original = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        File.WriteAllBytes(path, original);
        var result = await Run(Storage(), new { path }); using var metadata = Metadata(result);
        Assert.AreEqual("image/gif",metadata.RootElement.GetProperty("source").GetProperty("info").GetProperty("MimeType").GetString());
        Assert.AreEqual("image/png",metadata.RootElement.GetProperty("result").GetProperty("info").GetProperty("MimeType").GetString());
        CollectionAssert.AreEqual(original,File.ReadAllBytes(path));
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"r\",\"output\":[]}", Encoding.UTF8, "application/json") };
        }
    }
}
