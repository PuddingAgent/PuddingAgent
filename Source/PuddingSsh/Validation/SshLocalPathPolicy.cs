using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>
/// 本地路径策略（切片 B §11.1）：以执行快照工作区为根，检查**父目录与文件本身的重解析点**，
/// 不信任纯字符串前缀匹配。
/// <para>
/// 已知限制（必须如实表达）：同一进程/同一用户并发写同一目标仍有 TOCTOU；远端竞态见
/// <see cref="SshRemotePathPolicy"/>。路径检查**不是**沙箱。
/// </para>
/// </summary>
public static class SshLocalPathPolicy
{
    /// <summary>上传源：必须是根内、无重解析点的普通文件，且不超限。</summary>
    public static string ResolveUploadSource(
        string localRoot,
        string requestedPath,
        long maxBytes,
        ISshLocalFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? SshLocalFileSystem.Instance;
        var full = ResolveWithinRoot(fs, localRoot, requestedPath, forUploadSource: true);

        if (!fs.Exists(full))
        {
            throw Invalid("not_found", full);
        }

        if (fs.IsDirectory(full))
        {
            // 目录递归不在切片 B 范围内。
            throw Invalid("directory", full);
        }

        if (!fs.IsRegularFile(full))
        {
            throw Invalid("not_regular_file", full);
        }

        if (fs.IsReparsePoint(full))
        {
            throw Invalid("reparse_point", full);
        }

        var length = fs.GetLength(full);
        if (length > maxBytes)
        {
            throw new SshComponentException(
                SshErrorCodes.TransferTooLarge,
                $"本地文件 {length} 字节超过本次传输上限 {maxBytes} 字节。");
        }

        return full;
    }

    /// <summary>下载目标：根内、父目录存在且无重解析点；目标是目录或重解析点时拒绝。</summary>
    public static string ResolveDownloadTarget(
        string localRoot,
        string requestedPath,
        ISshLocalFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? SshLocalFileSystem.Instance;
        var full = ResolveWithinRoot(fs, localRoot, requestedPath, forUploadSource: false);

        if (fs.IsDirectory(full))
        {
            throw Invalid("directory", full);
        }

        if (fs.IsReparsePoint(full))
        {
            throw Invalid("reparse_point", full);
        }

        return full;
    }

    /// <summary>
    /// 把相对/绝对请求路径解析到根内的绝对路径：先绝对化，再逐段拒绝重解析点。
    /// </summary>
    private static string ResolveWithinRoot(ISshLocalFileSystem fs, string localRoot, string requestedPath, bool forUploadSource)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw Invalid("empty", requestedPath);
        }

        if (requestedPath.Contains('\0', StringComparison.Ordinal))
        {
            throw Invalid("invalid_chars", requestedPath);
        }

        var root = fs.GetFullPath(localRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var combined = System.IO.Path.IsPathFullyQualified(requestedPath)
            ? requestedPath
            : System.IO.Path.Combine(root, requestedPath);

        string full;
        try
        {
            full = fs.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException or NotSupportedException)
        {
            throw Invalid("invalid_chars", requestedPath);
        }

        if (!IsWithin(full, root))
        {
            // 字符串包含判定只是**第一道**过滤；真正的拒绝理由还要看重解析点。
            throw Invalid("outside_root", full);
        }

        EnsureNoReparsePointOnPath(fs, root, full, includeLeaf: true);

        if (forUploadSource)
        {
            // 已存在性/类型检查由调用方按更具体的错误码给出。
            return full;
        }

        return full;
    }

    private static void EnsureNoReparsePointOnPath(ISshLocalFileSystem fs, string root, string full, bool includeLeaf)
    {
        if (!IsWithin(full, root))
        {
            throw Invalid("outside_root", full);
        }

        var relative = System.IO.Path.GetRelativePath(root, full);
        var segments = relative.Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = System.IO.Path.Combine(current, segments[i]);
            var isLeaf = i == segments.Length - 1;
            if (!includeLeaf && isLeaf)
            {
                break;
            }

            if (fs.Exists(current) && fs.IsReparsePoint(current))
            {
                throw Invalid("reparse_point", current);
            }
        }

        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent) && !fs.Exists(parent))
        {
            throw Invalid("parent_missing", parent);
        }
    }

    /// <summary>Windows 大小写不敏感；用 GetRelativePath 判定包含关系而不是字符串前缀。</summary>
    private static bool IsWithin(string full, string root)
    {
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = System.IO.Path.GetRelativePath(root, full);
        return !relative.StartsWith("..", StringComparison.Ordinal)
            && !System.IO.Path.IsPathFullyQualified(relative);
    }

    private static SshComponentException Invalid(string reason, string detail) =>
        new(
            SshErrorCodes.LocalPathInvalid,
            $"本地路径被拒绝（reason={reason}）：{SshDiagnosticText.Bound(detail)}");
}
