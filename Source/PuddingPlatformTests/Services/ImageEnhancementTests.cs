using PuddingCode.Core;
using PuddingPlatform.Services;
using SkiaSharp;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 2026-11-02 新增的三项增强能力：对比度拉伸 / 反锐化掩模 / 红色通道分离。
/// <para>
/// 断言用**相对关系**而不是具体像素值：颜色滤镜与卷积都是平台相关实现，
/// 钉死绝对值会让测试在换 SkiaSharp 版本时假红，但「谁该比谁亮」不会变。
/// </para>
/// </summary>
[TestClass]
public sealed class ImageEnhancementTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pudding-enhance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>左半红、右半白：红底白字的招牌就是这个结构。</summary>
    private string RedAndWhite()
    {
        var path = Path.Combine(_dir, "red-white.png");
        using var bitmap = new SKBitmap(64, 32);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 64; x++)
            bitmap.SetPixel(x, y, x < 32 ? new SKColor(200, 30, 30) : SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    /// <summary>左半暗、右半亮：用来观察对比度是否把两端拉开。</summary>
    private string DarkAndBright()
    {
        var path = Path.Combine(_dir, "dark-bright.png");
        using var bitmap = new SKBitmap(64, 32);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 64; x++)
            bitmap.SetPixel(x, y, x < 32 ? new SKColor(64, 64, 64) : new SKColor(192, 192, 192));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    private static (double Left, double Right) Halves(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        double left = 0, right = 0;
        var leftN = 0; var rightN = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            var luma = (c.Red + c.Green + c.Blue) / 3.0;
            if (x < bitmap.Width / 2) { left += luma; leftN++; }
            else { right += luma; rightN++; }
        }
        return (left / Math.Max(1, leftN), right / Math.Max(1, rightN));
    }

    [TestMethod]
    public void Defaults_are_a_no_op()
    {
        var path = RedAndWhite();
        var implicitDefaults = ImagePreprocessing.Process(path, new ImageProcessingOptions());
        var explicitDefaults = ImagePreprocessing.Process(path, new ImageProcessingOptions(
            Contrast: 1.0, Sharpen: 0, ChannelMode: "none"));

        CollectionAssert.AreEqual(implicitDefaults, explicitDefaults,
            "显式传默认值必须与不传逐字节一致 —— 否则等于给所有既有调用方悄悄改了输出");
    }

    [TestMethod]
    public void ChannelMode_red_inverts_which_side_is_brighter()
    {
        var path = RedAndWhite();

        var grayscale = Halves(ImagePreprocessing.Process(path, new ImageProcessingOptions(Grayscale: true)));
        var redness = Halves(ImagePreprocessing.Process(path, new ImageProcessingOptions(ChannelMode: "red")));

        Assert.IsTrue(grayscale.Left < grayscale.Right, "灰度下白色应当比暗红更亮");
        Assert.IsTrue(redness.Left > redness.Right,
            "红色通道下必须反转为红底更亮 —— 这正是红底白字招牌在灰度里消失的原因");
    }

    [TestMethod]
    public void Contrast_widens_the_gap_between_dark_and_bright()
    {
        var path = DarkAndBright();

        var plain = Halves(ImagePreprocessing.Process(path, new ImageProcessingOptions()));
        var boosted = Halves(ImagePreprocessing.Process(path, new ImageProcessingOptions(Contrast: 2.0)));

        Assert.IsTrue(boosted.Right - boosted.Left > plain.Right - plain.Left,
            $"对比度 2.0 应把两端拉得更开: plain={plain.Right - plain.Left:F1} boosted={boosted.Right - boosted.Left:F1}");
    }

    [TestMethod]
    public void Out_of_range_parameters_are_rejected()
    {
        var path = RedAndWhite();
        Assert.ThrowsExactly<VisionPipelineException>(() =>
            ImagePreprocessing.Process(path, new ImageProcessingOptions(Contrast: 0.05)));
        Assert.ThrowsExactly<VisionPipelineException>(() =>
            ImagePreprocessing.Process(path, new ImageProcessingOptions(Sharpen: 9)));
        Assert.ThrowsExactly<VisionPipelineException>(() =>
            ImagePreprocessing.Process(path, new ImageProcessingOptions(ChannelMode: "blue")));
    }
}
