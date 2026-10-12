using System.Security.Cryptography;
using PuddingBrowser.AgentTools;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// <see cref="IBrowserScreenshotPublisher"/> 的 **Host 实现**（方案 §7.1/§7.2）：
/// 把截图字节落成**调用 workspace 工件域**内的有界图片工件，复用既有**内容寻址 + 幂等**存储
/// （`VisionArtifactStorageService.SaveIdempotentAsync`，与 <c>ImageReaderTool</c> 同一范式）。
///
/// 不变量：
/// <list type="bullet">
/// <item>发布端**再校一次**分块（不信任上游）：`Index` 升序、仅末块 `IsLast`、累计字节 == 元数据、非空、不超上限；</item>
/// <item>工件 id 由**内容哈希**决定 ⇒ 同一份字节重复发布得到**同一引用**（幂等）；</item>
/// <item>返回值只含工件 id 与元数据 —— **不含**文件系统路径或凭据。</item>
/// </list>
/// </summary>
public sealed class BrowserScreenshotArtifactPublisher(VisionArtifactStorageService artifactStorage)
    : IBrowserScreenshotPublisher
{
    public async ValueTask<PublishedBrowserScreenshot> PublishAsync(
        BrowserScreenshotPublishRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (bytes, error) = Assemble(request);
        if (error is not null)
        {
            throw new InvalidOperationException($"browser screenshot publish rejected: {error}");
        }

        // 与 ImageReaderTool 同构：内容哈希命名 + 幂等写入 ⇒ 重复发布不产生第二份工件。
        using var memory = new MemoryStream(bytes, writable: false);
        var id = "vision-" + Convert.ToHexString(await SHA256.HashDataAsync(memory, cancellationToken))
            .ToLowerInvariant()[..32];
        memory.Position = 0;

        await artifactStorage
            .SaveIdempotentAsync(request.WorkspaceId, id, memory, request.MimeType, ct: cancellationToken)
            .ConfigureAwait(false);

        return new PublishedBrowserScreenshot(
            id,
            request.MimeType,
            bytes.LongLength,
            request.Width,
            request.Height,
            request.ObservationId);
    }

    /// <summary>
    /// 发布端侧的完整性校验（方案 §7.2）：上游已经判过一次，这里**再判一次** ——
    /// 工件是"模型将要看到的东西"，残缺字节绝不能落成工件。
    /// </summary>
    private static (byte[] Bytes, string? Error) Assemble(BrowserScreenshotPublishRequest request)
    {
        if (request.Chunks.Count == 0)
        {
            return ([], "no chunks were supplied");
        }

        if (request.ByteCount <= 0)
        {
            return ([], "byte count must be positive");
        }

        if (request.ByteCount > VisionImageInspector.MaxCanonicalImageBytes)
        {
            return ([], $"image exceeds the canonical size limit ({VisionImageInspector.MaxCanonicalImageBytes} bytes)");
        }

        var total = 0L;
        for (var i = 0; i < request.Chunks.Count; i++)
        {
            if (request.Chunks[i].Index != i)
            {
                return ([], $"chunk at position {i} reports index {request.Chunks[i].Index}");
            }

            if (i < request.Chunks.Count - 1 && request.Chunks[i].IsLast)
            {
                return ([], $"chunk {i} is marked IsLast but is not the final chunk");
            }

            total += request.Chunks[i].Data.Length;
        }

        if (!request.Chunks[^1].IsLast)
        {
            return ([], "the final chunk is not marked IsLast");
        }

        if (total != request.ByteCount)
        {
            return ([], $"byte count mismatch: metadata says {request.ByteCount}, chunks carry {total}");
        }

        var bytes = new byte[total];
        var offset = 0;
        foreach (var chunk in request.Chunks)
        {
            chunk.Data.CopyTo(bytes, offset);
            offset += chunk.Data.Length;
        }

        return (bytes, null);
    }
}
