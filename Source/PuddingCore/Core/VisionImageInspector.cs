namespace PuddingCode.Core;

/// <summary>图片头部事实：以文件实际内容识别的 MIME 与像素尺寸。</summary>
public sealed record ImageHeaderInfo(string MimeType, int Width, int Height);

/// <summary>
/// 纯头部嗅探（ADR-077 §5.1/§8.2）：按 magic bytes 与结构字段识别 JPEG/PNG/WebP
/// 并读取真实像素尺寸，不信任扩展名或声明 MIME，不引入完整解码器依赖。
/// 只消费文件前缀字节（JPEG SOF 扫描限制在 prefix 内），截断/异常返回 null。
/// </summary>
public static class VisionImageInspector
{
    /// <summary>canonical Artifact 与 Image Reader source 的产品上限（DeepSeek Files API 64 MiB 内收口）。</summary>
    public const long MaxCanonicalImageBytes = 50L * 1024 * 1024;

    /// <summary>DeepSeek 每图最大边长；产品按 8 张上限取 8192px 校验。</summary>
    public const int MaxImageEdgePixels = 8192;

    private const int PrefixLength = 512 * 1024;

    public static ImageHeaderInfo? InspectPrefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 12)
            return null;

        if (prefix[0] == 0x89 && prefix[1] == 0x50 && prefix[2] == 0x4E && prefix[3] == 0x47
            && prefix[4] == 0x0D && prefix[5] == 0x0A && prefix[6] == 0x1A && prefix[7] == 0x0A)
            return InspectPng(prefix);

        if (prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF)
            return InspectJpeg(prefix);

        if (prefix[0] == (byte)'R' && prefix[1] == (byte)'I' && prefix[2] == (byte)'F' && prefix[3] == (byte)'F'
            && prefix[8] == (byte)'W' && prefix[9] == (byte)'E' && prefix[10] == (byte)'B' && prefix[11] == (byte)'P')
            return InspectWebP(prefix);

        return null;
    }

    private static ImageHeaderInfo? InspectPng(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 24)
            return null;
        // 布局：[8..11]=IHDR chunk length(13)，[12..15]="IHDR"，[16..19]=width，[20..23]=height。
        if (prefix[8] != 0 || prefix[9] != 0 || prefix[10] != 0 || prefix[11] != 13)
            return null;
        if (prefix[12] != (byte)'I' || prefix[13] != (byte)'H' || prefix[14] != (byte)'D' || prefix[15] != (byte)'R')
            return null;

        var width = ReadUInt32BigEndian(prefix.Slice(16));
        var height = ReadUInt32BigEndian(prefix.Slice(20));
        return Build("image/png", width, height);
    }

    private static ImageHeaderInfo? InspectJpeg(ReadOnlySpan<byte> prefix)
    {
        var offset = 2;
        while (offset + 4 <= prefix.Length)
        {
            if (prefix[offset] != 0xFF)
                return null;
            var marker = prefix[offset + 1];

            // RST/TEM/SOI/EOI 等独立标记无长度字段
            if (marker is 0x01 or >= 0xD0 and <= 0xD9)
            {
                offset += 2;
                continue;
            }

            if (offset + 4 > prefix.Length)
                return null;
            var segmentLength = (int)((prefix[offset + 2] << 8) | prefix[offset + 3]);
            if (segmentLength < 2)
                return null;

            // SOF0..SOF15（排除 DHT/JPG/DAC）
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (offset + 9 > prefix.Length)
                    return null;
                var height = (prefix[offset + 5] << 8) | prefix[offset + 6];
                var width = (prefix[offset + 7] << 8) | prefix[offset + 8];
                return Build("image/jpeg", (uint)width, (uint)height);
            }

            if (marker == 0xDA)
                return null; // 已到扫描数据仍未见 SOF

            offset += 2 + segmentLength;
        }

        return null;
    }

    private static ImageHeaderInfo? InspectWebP(ReadOnlySpan<byte> prefix)
    {
        var offset = 12;
        while (offset + 8 <= prefix.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(prefix.Slice(offset, 4));
            var chunkSize = (uint)(prefix[offset + 4] | (prefix[offset + 5] << 8)
                | (prefix[offset + 6] << 16) | (prefix[offset + 7] << 24));
            var data = prefix.Slice(offset + 8);

            switch (chunkId)
            {
                case "VP8X" when data.Length >= 10:
                {
                    var width = 1u + (uint)(data[4] | (data[5] << 8) | (data[6] << 16));
                    var height = 1u + (uint)(data[7] | (data[8] << 8) | (data[9] << 16));
                    return Build("image/webp", width, height);
                }
                case "VP8 " when data.Length >= 10:
                {
                    if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
                        return null;
                    var width = (uint)((data[6] | (data[7] << 8)) & 0x3FFF);
                    var height = (uint)((data[8] | (data[9] << 8)) & 0x3FFF);
                    return Build("image/webp", width, height);
                }
                case "VP8L" when data.Length >= 5:
                {
                    if (data[0] != 0x2F)
                        return null;
                    var bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                    var width = (bits & 0x3FFF) + 1;
                    var height = ((bits >> 14) & 0x3FFF) + 1;
                    return Build("image/webp", width, height);
                }
            }

            offset += 8 + (int)chunkSize + (int)(chunkSize & 1); // chunk 按 2 字节对齐
        }

        return null;
    }

    private static ImageHeaderInfo? Build(string mimeType, uint width, uint height)
    {
        if (width is 0 or > (uint)MaxImageEdgePixels || height is 0 or > (uint)MaxImageEdgePixels)
            return null;
        return new ImageHeaderInfo(mimeType, (int)width, (int)height);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> span)
        => (uint)((span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3]);

    /// <summary>流式拷贝时使用的首部缓冲区长度。</summary>
    public static int HeaderPrefixLength => PrefixLength;
}
