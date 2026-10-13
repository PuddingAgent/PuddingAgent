using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>
/// 远端路径策略（切片 B §11.1）：按 POSIX 处理，必须落在配置的允许根内。
/// <para>
/// 诚实的边界声明：`SSH_FXP_STAT`/realpath 检查与 `SSH_FXP_OPEN` 之间仍存在**服务端竞态**，
/// 严格隔离必须依靠远端 chroot/受限账号；字符串与 realpath 检查都**不是**远端沙箱。
/// </para>
/// </summary>
public static class SshRemotePathPolicy
{
    /// <summary>规范化远端路径：必须是 POSIX 绝对路径，拒绝控制字符与目录标记，按词法消解 <c>.</c>/<c>..</c>。</summary>
    public static string Normalize(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw Invalid("empty", requestedPath);
        }

        if (requestedPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw Invalid("control_chars", requestedPath);
        }

        if (!requestedPath.StartsWith('/'))
        {
            throw Invalid("not_absolute", requestedPath);
        }

        if (requestedPath.EndsWith('/'))
        {
            // 切片 B 只处理单文件；带尾斜杠表示目录。
            throw Invalid("directory_marker", requestedPath);
        }

        var segments = requestedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        throw Invalid("escapes_root", requestedPath);
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    stack.Add(segment);
                    continue;
            }
        }

        if (stack.Count == 0)
        {
            throw Invalid("empty", requestedPath);
        }

        return "/" + string.Join('/', stack);
    }

    /// <summary>规范化允许根：不能是 <c>/</c>（那等于没有边界），不允许尾斜杠。</summary>
    public static string NormalizeRoot(string root, string field)
    {
        if (string.Equals(root?.Trim(), "/", StringComparison.Ordinal))
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                $"传输限额非法：{field} 不能是文件系统根 '/'（等于没有边界）。");
        }

        var normalized = Normalize(root!);
        if (normalized == "/")
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                $"传输限额非法：{field} 不能是文件系统根 '/'（等于没有边界）。");
        }

        return normalized;
    }

    /// <summary>规范化后的目标必须等于根或位于根之下。</summary>
    public static void EnsureWithinRoot(string normalizedRoot, string normalizedPath, string reasonPrefix = "outside_root")
    {
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            // 根本身是目录，单文件传输不允许把它当目标。
            throw Invalid("is_root", normalizedPath);
        }

        if (!normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            throw Invalid(reasonPrefix, normalizedPath);
        }
    }

    /// <summary>
    /// 父目录 realpath 检查（由传输层用 SFTP realpath 解析后调用）：
    /// realpath 必须仍在允许根内，否则视为符号链接逃逸。
    /// </summary>
    public static void EnsureRealParentWithinRoot(string normalizedRoot, string realParent)
    {
        var normalizedReal = Normalize(realParent);
        if (!string.Equals(normalizedReal, normalizedRoot, StringComparison.Ordinal)
            && !normalizedReal.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            throw Invalid("symlink_escape", normalizedReal);
        }
    }

    /// <summary>取远端路径的父目录（POSIX 语义；调用方已确认路径是绝对且非根）。</summary>
    public static string ParentOf(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index <= 0 ? "/" : normalizedPath[..index];
    }

    /// <summary>取远端路径的文件名。</summary>
    public static string FileNameOf(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index < 0 ? normalizedPath : normalizedPath[(index + 1)..];
    }

    /// <summary>同目录唯一临时文件名：<c>.&lt;name&gt;.pudding-tmp-&lt;token&gt;</c>。</summary>
    public static string TemporarySiblingOf(string normalizedPath, string token)
    {
        var parent = ParentOf(normalizedPath);
        var name = "." + FileNameOf(normalizedPath) + ".pudding-tmp-" + token;
        return parent == "/" ? "/" + name : parent + "/" + name;
    }

    private static SshComponentException Invalid(string reason, string detail) =>
        new(
            SshErrorCodes.RemotePathInvalid,
            $"远端路径被拒绝（reason={reason}）：{SshDiagnosticText.Bound(detail)}");
}
