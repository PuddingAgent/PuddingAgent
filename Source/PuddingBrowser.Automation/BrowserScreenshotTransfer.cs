namespace PuddingBrowser.Automation;

/// <summary>截图保真度（设计 §8.1：默认 <see cref="Low"/>，按需 high/original/局部裁剪）。</summary>
public enum BrowserScreenshotDetail
{
    /// <summary>默认：最长边不超过 <see cref="BrowserScreenshotTransfer.LowDetailLongestEdge"/> 像素。</summary>
    Low,

    /// <summary>高保真：不做低清缩放，但要服从字节上限。</summary>
    High,

    /// <summary>原尺寸/局部裁剪：同样服从字节上限。</summary>
    Original,
}

/// <summary>截图载荷被拒的**明确原因**（不静默截断、不静默降级）。</summary>
public enum BrowserScreenshotRejection
{
    None,
    UnsupportedMime,
    OverLimit,
    DetailViolation,
    MissingObservationIdentity,
    MissingRevisions,
    MissingViewportGeometry,
    InvalidGeometry,
    ChunkGap,
    ChunkSizeMismatch,
}

/// <summary>
/// 一次截图的**元数据**：设计 §8.1 要求报告源尺寸、输出尺寸、设备缩放、CSS viewport、
/// scroll offset、裁剪变换与 PageVersion/domRevision/viewportRevision。
/// 用可空类型表达"未上报"，绝不补 0 冒充事实。
/// </summary>
public sealed record BrowserScreenshotMetadata(
    string ObservationId,
    string MimeType,
    int ByteCount,
    int SourceWidth,
    int SourceHeight,
    int OutputWidth,
    int OutputHeight,
    double? DeviceScaleFactor,
    double? ViewportWidth,
    double? ViewportHeight,
    double? ScrollX,
    double? ScrollY,
    double? CropX,
    double? CropY,
    double? CropWidth,
    double? CropHeight,
    long PageVersion,
    long DomRevision,
    long ViewportRevision,
    BrowserScreenshotDetail Detail);

/// <summary>分块载荷的一片（跨进程传输用：Desktop 不给 Core 一个"临时文件路径"让它自己读）。</summary>
public sealed record BrowserScreenshotChunk(int Index, bool IsLast, byte[] Data);

/// <summary>
/// **截图载荷的传输与校验（桌面一侧）**：设计 §8.1 把这半边的职责定为
/// "Desktop 返回**有界**媒体载荷/分块和元数据"，Core 再校验 MIME/像素/字节上限/截屏身份后写入 Vision Artifact。
///
/// 本类只做**能在这半边证明的事**：上限、保真度不变量、元数据完整性、分块无缺口。
/// **不**落库、**不**调用模型、**不**假定 Core 能读本地临时文件。
/// </summary>
public static class BrowserScreenshotTransfer
{
    /// <summary>初始输入上限：**8 MiB/张**（设计 §8.1）；超限明确返回原因。</summary>
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>默认 detail=low 时最长边上限：**512px**（设计 §8.1）。</summary>
    public const int LowDetailLongestEdge = 512;

    /// <summary>允许的 MIME（设计只要求位图截图；其它一律明确拒绝）。</summary>
    public static readonly IReadOnlyList<string> AllowedMimeTypes = ["image/png", "image/jpeg"];

    /// <summary>分块默认片大小（64 KiB：小到不会一次压爆通道，大到不至于碎成几千片）。</summary>
    public const int DefaultChunkBytes = 64 * 1024;

    /// <summary>
    /// 校验元数据与载荷是否**可以交付给 Core**。失败时给出**具体原因**（调用方必须如实上报，不许降级）。
    /// </summary>
    public static bool TryValidate(
        BrowserScreenshotMetadata? metadata,
        out BrowserScreenshotRejection rejection,
        out string reason)
    {
        if (metadata is null)
        {
            rejection = BrowserScreenshotRejection.InvalidGeometry;
            reason = "screenshot metadata is missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(metadata.ObservationId))
        {
            // 坐标操作必须携带截屏 observationId（§8.2）⇒ 没有身份就没法安全使用这张图。
            rejection = BrowserScreenshotRejection.MissingObservationIdentity;
            reason = "screenshot must carry the observation id that later coordinate operations refer to";
            return false;
        }

        if (!AllowedMimeTypes.Contains(metadata.MimeType, StringComparer.OrdinalIgnoreCase))
        {
            rejection = BrowserScreenshotRejection.UnsupportedMime;
            reason = $"screenshot mime '{metadata.MimeType}' is not one of {string.Join(", ", AllowedMimeTypes)}";
            return false;
        }

        if (metadata.ByteCount <= 0)
        {
            rejection = BrowserScreenshotRejection.InvalidGeometry;
            reason = "screenshot byte count must be positive";
            return false;
        }

        if (metadata.ByteCount > MaxImageBytes)
        {
            rejection = BrowserScreenshotRejection.OverLimit;
            reason = $"screenshot is {metadata.ByteCount} bytes, over the {MaxImageBytes}-byte (8 MiB) limit;"
                + " capture the viewport, lower the quality, or crop a region";
            return false;
        }

        if (metadata.SourceWidth <= 0 || metadata.SourceHeight <= 0
            || metadata.OutputWidth <= 0 || metadata.OutputHeight <= 0)
        {
            rejection = BrowserScreenshotRejection.InvalidGeometry;
            reason = "screenshot source/output dimensions must be positive";
            return false;
        }

        if (metadata.Detail == BrowserScreenshotDetail.Low)
        {
            var longestEdge = Math.Max(metadata.OutputWidth, metadata.OutputHeight);
            if (longestEdge > LowDetailLongestEdge)
            {
                // detail=low 是**承诺**：最长边不超过 512px。缩放没做就说做了，等于让调用方按错的尺寸算坐标。
                rejection = BrowserScreenshotRejection.DetailViolation;
                reason = $"detail=low promises a longest edge <= {LowDetailLongestEdge}px"
                    + $" but the output is {metadata.OutputWidth}x{metadata.OutputHeight}";
                return false;
            }
        }

        if (metadata.PageVersion <= 0 || metadata.DomRevision <= 0 || metadata.ViewportRevision <= 0)
        {
            // 三代修订号是"这张图属于哪一版页面"的唯一依据；缺失时旧坐标无法被判无效。
            rejection = BrowserScreenshotRejection.MissingRevisions;
            reason = "screenshot must report PageVersion/domRevision/viewportRevision (> 0)";
            return false;
        }

        if (metadata.DeviceScaleFactor is not > 0)
        {
            rejection = BrowserScreenshotRejection.MissingViewportGeometry;
            reason = "screenshot must report a positive device scale factor (CSS pixel vs device pixel)";
            return false;
        }

        if (metadata.ViewportWidth is not > 0 || metadata.ViewportHeight is not > 0
            || metadata.ScrollX is null || metadata.ScrollY is null)
        {
            rejection = BrowserScreenshotRejection.MissingViewportGeometry;
            reason = "screenshot must report the CSS viewport size and scroll offset";
            return false;
        }

        if (metadata.CropWidth is not > 0 || metadata.CropHeight is not > 0
            || metadata.CropX is null || metadata.CropY is null)
        {
            rejection = BrowserScreenshotRejection.MissingViewportGeometry;
            reason = "screenshot must report the crop transform (even for a full-page capture)";
            return false;
        }

        rejection = BrowserScreenshotRejection.None;
        reason = string.Empty;
        return true;
    }

    /// <summary>把总量切成分块（大小由调用方给，便于按通道配额调整）。</summary>
    public static IReadOnlyList<BrowserScreenshotChunk> Split(int totalBytes, int chunkBytes = DefaultChunkBytes)
    {
        if (totalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "total bytes must be positive");
        }

        if (chunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkBytes), chunkBytes, "chunk size must be positive");
        }

        var chunks = new List<BrowserScreenshotChunk>();
        var offset = 0;
        var index = 0;

        while (offset < totalBytes)
        {
            var size = Math.Min(chunkBytes, totalBytes - offset);
            chunks.Add(new BrowserScreenshotChunk(index, offset + size >= totalBytes, new byte[size]));
            offset += size;
            index++;
        }

        return chunks;
    }

    /// <summary>
    /// 重新组装分块，并**验证没有缺口/重复/总量不符**（设计 §8.1：跨进程传载荷，不能靠共享路径）。
    /// 失败即明确返回原因，绝不返回"可能不完整"的载荷。
    /// </summary>
    public static bool TryAssemble(
        IReadOnlyList<BrowserScreenshotChunk>? chunks,
        int expectedTotalBytes,
        out byte[]? payload,
        out string reason)
    {
        payload = null;

        if (chunks is null || chunks.Count == 0)
        {
            reason = "no screenshot chunks were provided";
            return false;
        }

        var ordered = chunks.OrderBy(chunk => chunk.Index).ToArray();
        for (var position = 0; position < ordered.Length; position++)
        {
            if (ordered[position].Index != position)
            {
                reason = $"screenshot chunks have a gap: expected index {position} but found {ordered[position].Index}";
                return false;
            }
        }

        if (!ordered[^1].IsLast)
        {
            reason = "the last screenshot chunk is not marked as last (the payload may be truncated)";
            return false;
        }

        if (ordered.Take(ordered.Length - 1).Any(chunk => chunk.IsLast))
        {
            reason = "more than one screenshot chunk is marked as last";
            return false;
        }

        var total = ordered.Sum(chunk => chunk.Data.Length);
        if (total != expectedTotalBytes)
        {
            reason = $"assembled {total} bytes but {expectedTotalBytes} were declared";
            return false;
        }

        if (total > MaxImageBytes)
        {
            reason = $"assembled payload is {total} bytes, over the {MaxImageBytes}-byte (8 MiB) limit";
            return false;
        }

        var buffer = new byte[total];
        var offset = 0;
        foreach (var chunk in ordered)
        {
            Buffer.BlockCopy(chunk.Data, 0, buffer, offset, chunk.Data.Length);
            offset += chunk.Data.Length;
        }

        payload = buffer;
        reason = string.Empty;
        return true;
    }
}
