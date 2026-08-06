namespace PuddingCode.Configuration;

/// <summary>
/// 构建产物目录同步的结果：复制数 / 跳过数 / 失败明细。
/// </summary>
public sealed record BuildOutputSyncResult
{
    /// <summary>实际执行了覆盖复制的文件数。</summary>
    public int Copied { get; init; }

    /// <summary>目标文件已存在且内容逐字节一致、无需复制的文件数。</summary>
    public int Skipped { get; init; }

    /// <summary>复制失败明细（相对路径 + 异常消息）。为空表示全部成功。</summary>
    public List<string> Failures { get; init; } = [];

    /// <summary>没有任何失败时为 true。</summary>
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// 构建产物同步的纯函数工具：把 dotnet build 产物目录整体复制到目标目录
/// （如 Desktop 运行目录，使 Core 可以 side-by-side 启动）。
/// 无进程、无 UI 依赖，可在测试项目中直接验证。
/// </summary>
public static class PuddingBuildOutputSync
{
    /// <summary>
    /// 把 sourceDirectory 下的全部文件（含子目录）复制到 targetDirectory，
    /// 覆盖同名文件；目标已存在且内容一致的文件跳过。
    /// 单个文件失败不会中断整体复制，失败明细记入结果。
    /// </summary>
    /// <param name="sourceDirectory">构建产物目录（绝对路径）。</param>
    /// <param name="targetDirectory">目标目录（绝对路径，如 AppContext.BaseDirectory）。</param>
    public static BuildOutputSyncResult SyncDirectory(string sourceDirectory, string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var failures = new List<string>();
        var copied = 0;
        var skipped = 0;

        if (!Directory.Exists(sourceDirectory))
        {
            failures.Add($"源目录不存在: {sourceDirectory}");
            return new BuildOutputSyncResult { Copied = 0, Skipped = 0, Failures = failures };
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            failures.Add($"无法创建目标目录 {targetDirectory}: {ex.Message}");
            return new BuildOutputSyncResult { Copied = 0, Skipped = 0, Failures = failures };
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relative);
            try
            {
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                if (FilesIdentical(file, destination))
                {
                    skipped++;
                    continue;
                }

                File.Copy(file, destination, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                failures.Add($"{relative}: {ex.Message}");
            }
        }

        return new BuildOutputSyncResult { Copied = copied, Skipped = skipped, Failures = failures };
    }

    /// <summary>
    /// 从 dotnet build 的标准输出中解析产物目录：匹配 "<projectName> -> &lt;绝对路径&gt;" 行
    /// （取最后一个匹配，兼容多目标构建）。解析不到返回 null。
    /// </summary>
    /// <param name="buildLog">RunBuildAsync 暴露的完整 stdout + stderr。</param>
    /// <param name="projectName">目标项目名（不含 .csproj 后缀），如 "PuddingAgent"。</param>
    public static string? TryParseBuildOutputDirectory(string? buildLog, string projectName)
    {
        if (string.IsNullOrWhiteSpace(buildLog) || string.IsNullOrWhiteSpace(projectName))
            return null;

        var marker = projectName + " -> ";
        string? lastMatch = null;

        foreach (var rawLine in buildLog.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var path = line[(index + marker.Length)..].Trim().TrimEnd('\\', '/');
            if (path.Length == 0 || !Path.IsPathRooted(path))
                continue;

            lastMatch = path;
        }

        return lastMatch;
    }

    private static bool FilesIdentical(string source, string destination)
    {
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (!destinationInfo.Exists)
            return false;
        if (sourceInfo.Length != destinationInfo.Length)
            return false;

        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destinationStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = sourceStream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return destinationStream.ReadByte() == -1;

            var other = new byte[read];
            var totalRead = 0;
            while (totalRead < read)
            {
                var n = destinationStream.Read(other, totalRead, read - totalRead);
                if (n == 0)
                    return false;
                totalRead += n;
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != other[i])
                    return false;
            }
        }
    }
}
