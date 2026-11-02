using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="OpenCvElementDetector.Detect"/> 的 focusRect 行为。
/// <para>
/// 锁定 2026-11-03 定位到的两个真实缺陷：
/// </para>
/// <list type="number">
/// <item><c>RetrievalModes.External</c> 只返回连通体最外层 —— 当闭运算把整片区域连成一块时，
/// 内部元素全部消失（实测 ChatGPT 窗口内轮廓数 0）。</item>
/// <item>尺寸上限按**整屏**算，导致"包住全部内容的外壳"永远在限内、永远不会被排除。</item>
/// </list>
/// <para>
/// 用合成图而不依赖真实屏幕：真实屏幕内容会变，合成图不会。
/// </para>
/// </summary>
[TestClass]
public sealed class DetectorFocusRectTests
{
    private static readonly PhysicalRect Bounds = new(0, 0, 1000, 600);

    /// <summary>
    /// 大面积内容块内部挖孔——真实白底网页的缩小版：
    /// 一个大背景层包住很多小元素，<c>External</c> 只能看到外壳。
    /// </summary>
    private static Mat ContentInsideBigBlock()
    {
        var mat = new Mat(600, 1000, MatType.CV_8UC3, new Scalar(255, 255, 255));

        // 外壳 970x580：宽 970 > 1000*0.95=950，必被尺寸上限排除
        Cv2.Rectangle(mat, new Rect(10, 10, 970, 580), new Scalar(20, 20, 20), -1);

        // 内部 3x3 个“元素”（挖回白色）
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Cv2.Rectangle(mat,
                    new Rect(80 + c * 300, 80 + r * 170, 200, 60),
                    new Scalar(255, 255, 255), -1);
        return mat;
    }

    /// <summary>
    /// **核心用例**：内容被外壳包住时，只有给了 focusRect（走 List）才能把它们拆出来。
    /// <para>
    /// 若实现退回 <c>External</c>，只会拿到一个超尺寸的外壳并被排除，结果为 0。
    /// 这条同时钉住了两个前提：**必须用 List** 且 **尺寸上限必须相对 focusRect**。
    /// </para>
    /// </summary>
    [TestMethod]
    public void FocusRect_RecoversElementsEnclosedByBigBlock()
    {
        using var mat = ContentInsideBigBlock();
        var detector = new OpenCvElementDetector();

        var withFocus = detector.Detect(mat, Bounds, Bounds);
        var withoutFocus = detector.Detect(mat, Bounds);

        Assert.IsEmpty(withoutFocus,
            "External 只能看到外壳，而外壳超出 focusRect 的尺寸上限 → 应为 0");

        Assert.IsGreaterThan(0, withFocus.Count,
            "给了 focusRect 应能用 List 拆出被包住的内部元素");
    }

    /// <summary>白底 + 密集小方块（行间距 3px，小于闭运算核高度 5px，必然粘连）。</summary>
    private static Mat DenseContent()
    {
        var mat = new Mat(600, 1000, MatType.CV_8UC3, new Scalar(255, 255, 255));
        // 行距 15 / 块高 12 ⇒ 间隙 3px < Close(5×3)/(5×5) 的核 ⇒ 垂直方向必粘连。
        // 这正是真实白底网页的样子：一行行文字彼此只隔几个像素。
        for (int row = 0; row < 10; row++)
            for (int col = 0; col < 3; col++)
                Cv2.Rectangle(mat,
                    new Rect(60 + col * 300, 40 + row * 15, 200, 12),
                    new Scalar(20, 20, 20), -1);
        return mat;
    }

    /// <summary>
    /// 尺寸上限必须相对 focusRect，而不是相对整屏。
    /// <para>
    /// 构造：整幅图 1000x600，内容只占左侧 400 宽；把 focusRect 设为那 400 宽的区域。
    /// 一个宽度接近 focusRect 的块应被判为"外壳"排除，而不是因为小于整屏而留下。
    /// </para>
    /// </summary>
    [TestMethod]
    public void SizeCap_IsRelativeToFocusRect_NotFullScreen()
    {
        using var mat = new Mat(600, 1000, MatType.CV_8UC3, new Scalar(255, 255, 255));

        // 左侧 400x600 画满一块深色（占 focusRect 的 100%）
        Cv2.Rectangle(mat, new Rect(0, 0, 400, 600), new Scalar(10, 10, 10), -1);

        var detector = new OpenCvElementDetector();
        var narrowFocus = new PhysicalRect(0, 0, 400, 600);

        var result = detector.Detect(mat, Bounds, narrowFocus);

        // cap = 400*0.95 = 380，整块 400 宽 → 必须被排除
        Assert.IsFalse(result.Any(d => d.Rect.Width >= 400),
            "整块外壳（400 宽）相对 focusRect（400 宽）应被尺寸上限排除");
    }

    /// <summary>
    /// 省略 focusRect 时必须保持旧行为，不能因为新参数改变整屏分析的结果形态。
    /// </summary>
    [TestMethod]
    public void WithoutFocusRect_KeepsLegacyBehavior()
    {
        using var mat = DenseContent();
        var detector = new OpenCvElementDetector();

        var legacy = detector.Detect(mat, Bounds);
        var explicitNull = detector.Detect(mat, Bounds, null);

        Assert.HasCount(legacy.Count, explicitNull,
            "不传参数与显式传 null 必须等价");
    }
}
