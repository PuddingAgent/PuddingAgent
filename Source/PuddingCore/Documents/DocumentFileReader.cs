using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PuddingCore.Documents;

/// <summary>读取成功的文档。正文是**已经规整过的文本**，不是原始字节。</summary>
/// <param name="Name">文件名（相对路径最后一段），供标签页标题与下载使用。</param>
/// <param name="Content">文本正文。</param>
/// <param name="SizeBytes">磁盘上的字节数（不是字符数）。</param>
public sealed record DocumentFile(string Name, string Content, long SizeBytes);

/// <summary>
/// 文档内容读取（**唯一的读取实现**）。
///
/// <para>
/// **为什么放在 PuddingCore 而不是各自实现**：Core 的 HTTP 接口与 Desktop 的文档标签页
/// 都要"按相对路径读出文本"。两处各写一遍的后果不是代码重复，而是**规则漂移** ——
/// 一边补了尾随空格检查、另一边没补，就成了安全缺口。这里把整条链（规整 → 根复核 →
/// 大小上限 → 文本解码）固定成一处，两个执行体只提供各自信任的根目录。
/// </para>
/// <para>
/// 错误语义刻意与 HTTP 接口一致：根外与不存在**都报 NotFound**（区分两者等于提供
/// 存在性探测）；超限报 TooLarge 而**不截断**（截断的文档会被读成"文件就这么长"）。
/// </para>
/// </summary>
public static class DocumentFileReader
{
    /// <summary>
    /// 按相对路径读取文本文件。
    /// </summary>
    /// <param name="root">调用方信任的根目录（必须是绝对路径）。</param>
    /// <param name="relativePath">用户/Agent 给的相对路径，未规整。</param>
    /// <param name="file">成功时的结果。</param>
    /// <param name="errorCode">失败时为 <see cref="DocumentPathErrors"/> 之一。</param>
    /// <param name="errorMessage">失败时的可读原因；**不含绝对路径**。</param>
    public static bool TryRead(
        string root,
        string? relativePath,
        [NotNullWhen(true)] out DocumentFile? file,
        out string? errorCode,
        out string? errorMessage)
    {
        file = null;

        if (!DocumentPathPolicy.TryNormalize(relativePath, out var normalized, out errorCode, out errorMessage))
            return false;

        if (!DocumentPathPolicy.TryResolveFullPath(root, normalized, out var fullPath))
        {
            // 根外与不存在同样处理：不泄露"这个文件在别处存在"。
            errorCode = DocumentPathErrors.NotFound;
            errorMessage = "找不到该文件，或它超出了允许的目录范围";
            return false;
        }

        long size;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                errorCode = DocumentPathErrors.NotFound;
                errorMessage = "找不到该文件";
                return false;
            }

            size = info.Length;
        }
        catch (Exception)
        {
            // 权限、路径过长、设备异常：一律当作不可用，不把异常抛给 UI。
            errorCode = DocumentPathErrors.NotFound;
            errorMessage = "无法访问该文件";
            return false;
        }

        if (size > DocumentPathPolicy.MaxContentBytes)
        {
            errorCode = DocumentPathErrors.TooLarge;
            errorMessage =
                $"文件过大（{size / 1024} KB），上限为 {DocumentPathPolicy.MaxContentBytes / 1024 / 1024} MiB";
            return false;
        }

        string content;
        try
        {
            // UTF-8 且带 BOM 自动识别：仓库里的中文文档既有带 BOM 也有不带的。
            content = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception)
        {
            errorCode = DocumentPathErrors.NotFound;
            errorMessage = "无法读取该文件";
            return false;
        }

        file = new DocumentFile(LastSegment(normalized), content, size);
        errorCode = null;
        errorMessage = null;
        return true;
    }

    private static string LastSegment(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index >= 0 ? normalizedPath[(index + 1)..] : normalizedPath;
    }
}
