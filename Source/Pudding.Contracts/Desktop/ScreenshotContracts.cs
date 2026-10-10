namespace Pudding.Contracts.Desktop;

/// <summary>截图范围（§8.1）：先交付视口与区域/元素裁剪。</summary>
public enum DesktopScreenshotScope
{
    Viewport,
    Region,
    FullPage,
}

/// <summary>截图范围的**线名真源**（与既有 <c>*Wire</c> 同风格）。</summary>
public static class DesktopScreenshotScopeWire
{
    public static string NameOf(DesktopScreenshotScope scope) => scope switch
    {
        DesktopScreenshotScope.Viewport => "viewport",
        DesktopScreenshotScope.Region => "region",
        DesktopScreenshotScope.FullPage => "full_page",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Screenshot scope is not registered."),
    };

    public static bool TryParse(string? name, out DesktopScreenshotScope scope)
    {
        foreach (var candidate in Enum.GetValues<DesktopScreenshotScope>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                scope = candidate;
                return true;
            }
        }

        scope = default;
        return false;
    }
}

/// <summary>细节档（§8.1）：默认 <c>low</c>（最长边 ≤512px）。</summary>
public enum DesktopScreenshotDetail
{
    Low,
    High,
    Original,
}

/// <summary>细节档的**线名真源**。</summary>
public static class DesktopScreenshotDetailWire
{
    public static string NameOf(DesktopScreenshotDetail detail) => detail switch
    {
        DesktopScreenshotDetail.Low => "low",
        DesktopScreenshotDetail.High => "high",
        DesktopScreenshotDetail.Original => "original",
        _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "Screenshot detail is not registered."),
    };

    public static bool TryParse(string? name, out DesktopScreenshotDetail detail)
    {
        foreach (var candidate in Enum.GetValues<DesktopScreenshotDetail>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                detail = candidate;
                return true;
            }
        }

        detail = default;
        return false;
    }
}

/// <summary>
/// 截图请求（§8.1）。**必须钉住页面版本**；区域截图必须给全矩形且为正；元素截图必须给定位；
/// 视口截图**不得**携带定位/矩形（语义只能有一种）；格式必须在白名单内（未登记 ⇒ fail-closed）。
/// </summary>
public sealed record BrowserScreenshotRequest
{
    public BrowserScreenshotRequest(
        DesktopPageTarget target,
        DesktopPageVersion expectedPageVersion,
        DesktopScreenshotScope scope = DesktopScreenshotScope.Viewport,
        DesktopScreenshotDetail detail = DesktopScreenshotDetail.Low,
        DesktopLocator? locator = null,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        string format = "png",
        long? domRevision = null,
        long? viewportRevision = null)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Screenshot scope is not registered.");
        }

        if (!Enum.IsDefined(detail))
        {
            throw new ArgumentOutOfRangeException(nameof(detail), detail, "Screenshot detail is not registered.");
        }

        if (expectedPageVersion.Value <= 0)
        {
            throw new ArgumentException("A screenshot must pin the page version it captures.", nameof(expectedPageVersion));
        }

        var hasRegion = regionX is not null || regionY is not null || regionWidth is not null || regionHeight is not null;
        switch (scope)
        {
            case DesktopScreenshotScope.Viewport when locator is not null || hasRegion:
                throw new ArgumentException("A viewport screenshot takes no locator and no region.", nameof(scope));
            case DesktopScreenshotScope.FullPage when locator is not null || hasRegion:
                throw new ArgumentException("A full-page screenshot takes no locator and no region.", nameof(scope));
            case DesktopScreenshotScope.Region when regionX is null || regionY is null || regionWidth is null || regionHeight is null:
                throw new ArgumentException("A region screenshot requires all four rectangle values.", nameof(regionX));
            case DesktopScreenshotScope.Region when regionWidth <= 0 || regionHeight <= 0:
                throw new ArgumentException("A region screenshot needs a positive width and height.", nameof(regionWidth));
        }

        if (string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("A screenshot format is required.", nameof(format));
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        ExpectedPageVersion = expectedPageVersion;
        Scope = scope;
        Detail = detail;
        Locator = locator;
        RegionX = regionX;
        RegionY = regionY;
        RegionWidth = regionWidth;
        RegionHeight = regionHeight;
        Format = format.Trim().ToLowerInvariant();
        DomRevision = domRevision;
        ViewportRevision = viewportRevision;
    }

    public DesktopPageTarget Target { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    public DesktopScreenshotScope Scope { get; }

    public DesktopScreenshotDetail Detail { get; }

    public DesktopLocator? Locator { get; }

    public double? RegionX { get; }

    public double? RegionY { get; }

    public double? RegionWidth { get; }

    public double? RegionHeight { get; }

    public string Format { get; }

    public long? DomRevision { get; }

    public long? ViewportRevision { get; }
}

/// <summary>截图结果元数据（§8.1）：源/输出尺寸、字节、设备缩放、视口、滚动、裁剪、版本与世代。</summary>
public sealed record DesktopScreenshotMetadata(
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
    DesktopScreenshotDetail Detail)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ObservationId))
        {
            throw new ArgumentException("A screenshot result must carry an observation id.", nameof(ObservationId));
        }

        if (string.IsNullOrWhiteSpace(MimeType) || ByteCount <= 0)
        {
            throw new ArgumentException("A screenshot result must carry a MIME type and a positive byte count.", nameof(MimeType));
        }
    }
}

/// <summary>分块载荷的一片（跨进程传输用）。</summary>
public sealed record DesktopScreenshotChunk(int Index, bool IsLast, byte[] Data);

/// <summary>截图结果（§8.1）：元数据 + 分块。**观察 id 从这里回传**（B5 第 ③ 步的落点）。</summary>
public sealed record BrowserScreenshotResult(
    DesktopScreenshotMetadata Metadata,
    IReadOnlyList<DesktopScreenshotChunk> Chunks)
{
    /// <summary>结构校验（组装后调用）：元数据非空且自身合法、分块列表非空。</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Metadata);
        ArgumentNullException.ThrowIfNull(Chunks);
        Metadata.Validate();
    }
}
