namespace PuddingPathFiltering;

/// <summary>路径字符串的规范化助手：统一分隔符为 <c>/</c>，去掉首尾分隔符与 <c>.</c> 前缀。纯函数，无 I/O。</summary>
internal static class PathText
{
    /// <summary>把 <c>\</c> 归一为 <c>/</c>，并去掉首尾的 <c>/</c> 与 <c>./</c> 前缀。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/');

        var start = 0;
        while (start < normalized.Length)
        {
            if (normalized[start] == '/')
            {
                start++;
                continue;
            }

            if (normalized[start] == '.' && start + 1 < normalized.Length &&
                (normalized[start + 1] == '/' || normalized[start + 1] == '\\'))
            {
                start += 2;
                continue;
            }

            break;
        }

        var end = normalized.Length;
        while (end > start && normalized[end - 1] == '/')
            end--;

        return normalized[start..end];
    }

    /// <summary>
    /// 判断 <paramref name="path"/> 是否严格位于 <paramref name="directory"/> 之下，并返回相对部分。
    /// <paramref name="directory"/> 为空串表示「忽略根」；此时任何非空路径都算在其下。
    /// 路径等于目录本身时返回 false（.gitignore 文件不能忽略它自己所在的那一级）。
    /// </summary>
    public static bool TryGetBelow(string path, string directory, out string rest)
    {
        rest = string.Empty;

        if (directory.Length == 0)
        {
            if (path.Length == 0)
                return false;

            rest = path;
            return true;
        }

        if (path.Length <= directory.Length + 1)
            return false;

        if (!path.StartsWith(directory, StringComparison.Ordinal) || path[directory.Length] != '/')
            return false;

        rest = path[(directory.Length + 1)..];
        return true;
    }

    /// <summary>取最后一个路径段。</summary>
    public static string Basename(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
