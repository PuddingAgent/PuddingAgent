namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 文件投递时飞书要求的两个元数据：<c>file_type</c> 与 multipart 段的 Content-Type。
///
/// <para>
/// 抽成一处的原因：连接器（把已入队的文件发出去）与工具层（决定能不能发）都要判同一张表，
/// 各写一份必然漂移 —— 工具说「可发」而连接器把它当 <c>stream</c>，或者反过来。
/// 飞书 im/v1/files 只接受 opus / mp4 / pdf / doc / xls / ppt / stream 这七个值，
/// 表外的扩展名一律 <c>stream</c>（飞书按未知二进制处理，仍可下载）。
/// </para>
/// </summary>
public static class FeishuFileTypes
{
    /// <summary>飞书接受的 file_type 全集，用于断言与文档。</summary>
    public static IReadOnlyList<string> Supported { get; } =
        ["opus", "mp4", "pdf", "doc", "xls", "ppt", "stream"];

    /// <summary>按文件名（扩展名）解析出 file_type 与上传段的 Content-Type。</summary>
    public static FeishuFileDescriptor Resolve(string? fileName)
    {
        var ext = Extension(fileName);
        return ext switch
        {
            ".opus" or ".ogg" => new FeishuFileDescriptor("opus", "audio/ogg"),
            ".mp4" => new FeishuFileDescriptor("mp4", "video/mp4"),
            ".pdf" => new FeishuFileDescriptor("pdf", "application/pdf"),
            ".doc" => new FeishuFileDescriptor("doc", "application/msword"),
            ".docx" => new FeishuFileDescriptor(
                "doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ".xls" => new FeishuFileDescriptor("xls", "application/vnd.ms-excel"),
            ".xlsx" => new FeishuFileDescriptor(
                "xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ".ppt" => new FeishuFileDescriptor("ppt", "application/vnd.ms-powerpoint"),
            ".pptx" => new FeishuFileDescriptor(
                "ppt",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            // 不在白名单里的扩展名（含无扩展名）统统按未知二进制发，仍能下载。
            _ => new FeishuFileDescriptor("stream", "application/octet-stream"),
        };
    }

    private static string Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var name = fileName.Trim();
        // 只取最后一段的文件名，避免 ..\dir\a.pdf 这类输入把目录点号当扩展名。
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
            name = name[(slash + 1)..];

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return string.Empty;

        return name[dot..].ToLowerInvariant();
    }
}

/// <summary>一次文件投递所需的飞书元数据。</summary>
/// <param name="FileType">im/v1/files 的 file_type。</param>
/// <param name="ContentType">multipart 中 file 段的 Content-Type。</param>
public readonly record struct FeishuFileDescriptor(string FileType, string ContentType);
