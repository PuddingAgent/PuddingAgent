namespace PuddingBrowser.AgentTools;

/// <summary>
/// 已发布的截图工件（方案 §7.1）：工具**消费 capture 后**的返回值 —— 它把"字节"变成
/// 一个有身份的工件引用，模型侧拿 <see cref="ArtifactId"/> 组成 native image part。
/// </summary>
public sealed record PublishedBrowserScreenshot(
    string ArtifactId,
    string MimeType,
    long ByteCount,
    int Width,
    int Height,
    string ObservationId);

/// <summary>
/// **截图工件发布端口**（方案 §7.1）：窄接口，声明在工具组件内 ——
/// **不得**引用 `PuddingHost` / `PuddingPlatform` / WebView2，实现由 Host 组合根提供。
///
/// 为什么需要它：`browser_screenshot` 此前只把元数据（含 `chunks = 数量`）返回给模型，
/// **字节被丢弃** ⇒ 工具"成功"了但**模型从未看见图片**（外部复核 P1）。
/// 现在工具把字节交给本端口落成**有界工件**，再用工件 id 组成 native image part。
/// </summary>
public interface IBrowserScreenshotPublisher
{
    /// <summary>
    /// 把一次截图的有序字节块发布为工件。
    ///
    /// 约定：
    /// <list type="bullet">
    /// <item>调用方必须**按 <c>Index</c> 升序**、**恰好一次**传完全部块（末块 <c>IsLast</c>），
    /// 并保证累计字节数等于元数据里的 <c>ByteCount</c> —— 发布端**再次校验**，不符即拒绝；</item>
    /// <item>工件落在**调用 workspace 的工件域**并返回 <c>artifact://…</c> 引用；跨 workspace 无权解析；</item>
    /// <item>**不得**在返回值或日志里暴露文件系统路径或会话凭据；</item>
    /// <item>复用既有内容寻址/幂等存储，重复发布同一份字节应得到同一工件引用。</item>
    /// </list>
    /// </summary>
    ValueTask<PublishedBrowserScreenshot> PublishAsync(
        BrowserScreenshotPublishRequest request,
        CancellationToken cancellationToken);
}

/// <summary>发布请求：把一次 capture 的**元数据 + 有序字节块**交给发布端。</summary>
public sealed record BrowserScreenshotPublishRequest(
    // 方案 §7.2：工件必须落在**调用 workspace 的工件域**（跨 workspace 无权解析）⇒ workspace 随请求传。
    string WorkspaceId,
    string ObservationId,
    string MimeType,
    long ByteCount,
    int Width,
    int Height,
    string Detail,
    IReadOnlyList<BrowserScreenshotPublishChunk> Chunks);

/// <summary>发布用字节块（与线缆块同构，但不依赖线缆类型）。</summary>
public sealed record BrowserScreenshotPublishChunk(int Index, bool IsLast, byte[] Data);
