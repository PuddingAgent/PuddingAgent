namespace PuddingDesktop.Storage;

public sealed class DataRootSafetyValidator : IDataRootSafetyValidator
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public ValidatedDataRoot ValidateDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new StorageSafetyException("尚未配置数据目录。");

        string normalized;
        try
        {
            normalized = Normalize(dataRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StorageSafetyException($"数据目录路径无效：{ex.Message}");
        }

        if (!Path.IsPathRooted(normalized))
            throw new StorageSafetyException("数据目录必须是绝对路径。");

        var driveRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(driveRoot))
            throw new StorageSafetyException("无法确定数据目录所在磁盘。");

        var normalizedDriveRoot = Normalize(driveRoot);
        if (string.Equals(normalized, normalizedDriveRoot, PathComparison))
            throw new StorageSafetyException("数据目录不能是磁盘根目录。");

        if (!Directory.Exists(normalized))
            throw new StorageSafetyException($"数据目录不存在：{normalized}");

        if (IsReparsePoint(normalized))
            throw new StorageSafetyException("数据目录不能是符号链接或 Junction。");

        return new ValidatedDataRoot(
            normalized,
            Path.Combine(normalized, "logs"),
            normalizedDriveRoot);
    }

    public ValidatedDataRoot ValidateLogRoot(
        string dataRoot,
        bool requireLogDirectory)
    {
        var validated = ValidateDataRoot(dataRoot);
        var logRoot = Normalize(validated.LogRoot);

        if (!IsDescendantOf(logRoot, validated.DataRoot)
            || !string.Equals(Path.GetFileName(logRoot), "logs", PathComparison))
        {
            throw new StorageSafetyException("日志目录必须是 DataRoot 下的直接 logs 子目录。");
        }

        if (!Directory.Exists(logRoot))
        {
            if (requireLogDirectory)
                throw new StorageSafetyException($"日志目录不存在：{logRoot}");

            return validated with { LogRoot = logRoot };
        }

        if (IsReparsePoint(logRoot))
            throw new StorageSafetyException("日志目录不能是符号链接或 Junction。");

        return validated with { LogRoot = logRoot };
    }

    public bool IsDescendantOf(string candidatePath, string parentPath)
    {
        var candidate = Normalize(candidatePath);
        var parent = Normalize(parentPath);
        var relative = Path.GetRelativePath(parent, candidate);

        return !string.Equals(relative, ".", StringComparison.Ordinal)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageSafetyException($"无法验证路径安全属性：{path}；{ex.Message}");
        }
    }

    internal static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
