namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 超过飞书单文件上限时怎么发。
///
/// <para>
/// 飞书 <c>im/v1/files</c> 的硬上限是 30 MB，这是服务端约束，压不过去。
/// 所以大文件的处理不是「传上去」，而是按上限切成若干段，依次发，并在最后给一条重组命令。
/// 切分是纯计算，不碰磁盘也不碰网络，因此可以单独钉死边界：
/// 恰好等于上限必须是 1 段（不是 2 段），只有超过 1 字节才切第二段。
/// </para>
/// </summary>
public static class FeishuFileSplitPlanner
{
    /// <summary>飞书单文件硬上限（30 MB）。</summary>
    public const long MaxSingleFileBytes = 30L * 1024 * 1024;

    /// <summary>切分计划；文件在限内时返回单段且后缀为空字符串。</summary>
    public static IReadOnlyList<FeishuFilePart> Plan(
        long totalBytes,
        long maxPartBytes = MaxSingleFileBytes)
    {
        if (maxPartBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPartBytes), "maxPartBytes must be positive.");
        if (totalBytes <= 0)
            return [];

        if (totalBytes <= maxPartBytes)
            return [new FeishuFilePart(1, 0, totalBytes, string.Empty)];

        var count = (int)((totalBytes + maxPartBytes - 1) / maxPartBytes);
        var parts = new List<FeishuFilePart>(count);
        long offset = 0;
        for (var i = 1; i <= count; i++)
        {
            var length = Math.Min(maxPartBytes, totalBytes - offset);
            parts.Add(new FeishuFilePart(i, offset, length, $".part{i:D2}of{count:D2}"));
            offset += length;
        }

        return parts;
    }

    /// <summary>分段总数；限内为 1，空文件为 0。</summary>
    public static int PartCount(long totalBytes, long maxPartBytes = MaxSingleFileBytes)
        => Plan(totalBytes, maxPartBytes).Count;
}

/// <summary>一段切片：序号、起始偏移、长度、要追加到文件名末尾的后缀。</summary>
public readonly record struct FeishuFilePart(int Index, long Offset, long Length, string Suffix);
