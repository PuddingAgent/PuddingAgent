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

/// <summary>Deterministic fingerprint of the managed Core launch artifact.</summary>
public sealed record ManagedArtifactManifestResult
{
    public string Sha256 { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public IReadOnlyList<string> RelativePaths { get; init; } = [];
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
    /// Stages every changed file beside the target directory, then commits the
    /// prepared files with per-file rollback. A staging failure never mutates
    /// the live Core directory; a commit failure restores files already changed.
    /// </summary>
    public static BuildOutputSyncResult DeployDirectoryTransactional(
        string sourceDirectory,
        string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory);
        var failures = new List<string>();

        if (!Directory.Exists(sourceRoot))
        {
            failures.Add($"源目录不存在: {sourceRoot}");
            return new BuildOutputSyncResult { Failures = failures };
        }

        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(sourceRoot),
                Path.TrimEndingDirectorySeparator(targetRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return new BuildOutputSyncResult { Skipped = sourceFiles.Count };
        }

        var targetParent = Directory.GetParent(targetRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(targetParent))
        {
            failures.Add($"无法解析目标目录的父目录: {targetRoot}");
            return new BuildOutputSyncResult { Failures = failures };
        }

        Directory.CreateDirectory(targetRoot);
        var transactionRoot = Path.Combine(targetParent, $".pudding-bootstrap-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var changed = new List<(string StagedPath, string DestinationPath, string BackupPath, bool HadOriginal)>();
        var skipped = 0;

        try
        {
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(backupRoot);

            foreach (var sourceFile in sourceFiles)
            {
                var relative = Path.GetRelativePath(sourceRoot, sourceFile);
                var destination = Path.Combine(targetRoot, relative);
                if (FilesIdentical(sourceFile, destination))
                {
                    skipped++;
                    continue;
                }

                var staged = Path.Combine(stagingRoot, relative);
                var stagedParent = Path.GetDirectoryName(staged);
                if (!string.IsNullOrWhiteSpace(stagedParent))
                    Directory.CreateDirectory(stagedParent);
                File.Copy(sourceFile, staged, overwrite: true);
                if (!FilesIdentical(sourceFile, staged))
                    throw new IOException($"暂存校验失败: {relative}");

                changed.Add((
                    staged,
                    destination,
                    Path.Combine(backupRoot, relative),
                    File.Exists(destination)));
            }
        }
        catch (Exception ex)
        {
            failures.Add($"产物暂存失败: {ex.Message}");
            TryDeleteTransactionDirectory(transactionRoot, targetParent);
            return new BuildOutputSyncResult { Skipped = skipped, Failures = failures };
        }

        var committed = new List<(string DestinationPath, string BackupPath, bool HadOriginal)>();
        try
        {
            foreach (var item in changed)
            {
                var destinationParent = Path.GetDirectoryName(item.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationParent))
                    Directory.CreateDirectory(destinationParent);

                if (item.HadOriginal)
                {
                    var backupParent = Path.GetDirectoryName(item.BackupPath);
                    if (!string.IsNullOrWhiteSpace(backupParent))
                        Directory.CreateDirectory(backupParent);
                    File.Copy(item.DestinationPath, item.BackupPath, overwrite: true);
                }

                File.Move(item.StagedPath, item.DestinationPath, overwrite: true);
                committed.Add((item.DestinationPath, item.BackupPath, item.HadOriginal));
            }
        }
        catch (Exception ex)
        {
            failures.Add($"产物提交失败: {ex.Message}");
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.HadOriginal)
                        File.Copy(item.BackupPath, item.DestinationPath, overwrite: true);
                    else if (File.Exists(item.DestinationPath))
                        File.Delete(item.DestinationPath);
                }
                catch (Exception rollbackException)
                {
                    failures.Add($"回滚失败 {item.DestinationPath}: {rollbackException.Message}");
                }
            }

            TryDeleteTransactionDirectory(transactionRoot, targetParent);
            return new BuildOutputSyncResult { Skipped = skipped, Failures = failures };
        }

        TryDeleteTransactionDirectory(transactionRoot, targetParent);
        return new BuildOutputSyncResult { Copied = changed.Count, Skipped = skipped, Failures = failures };
    }

    /// <summary>Computes a lowercase SHA-256 digest for deployment evidence.</summary>
    public static string ComputeSha256(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Fingerprints every managed launch artifact using sorted relative path +
    /// per-file SHA-256. Passing relativePaths lets the deployed directory be
    /// checked against the exact prepared file set without stale extras causing
    /// a false mismatch.
    /// </summary>
    public static ManagedArtifactManifestResult ComputeManagedArtifactManifest(
        string rootDirectory,
        IEnumerable<string>? relativePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"产物目录不存在: {root}");

        var files = (relativePaths ?? Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsManagedLaunchArtifact)
                .Select(path => Path.GetRelativePath(root, path)))
            .Select(path => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException($"产物目录不包含托管启动程序集: {root}");

        var manifest = new System.Text.StringBuilder(files.Count * 96);
        foreach (var relative in files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsPathWithin(fullPath, root))
                throw new InvalidOperationException($"产物清单路径越界: {relative}");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"产物清单文件缺失: {relative}", fullPath);

            manifest
                .Append(relative.Replace('\\', '/'))
                .Append('\0')
                .Append(ComputeSha256(fullPath))
                .Append('\n');
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(manifest.ToString()));
        return new ManagedArtifactManifestResult
        {
            Sha256 = Convert.ToHexString(digest).ToLowerInvariant(),
            FileCount = files.Count,
            RelativePaths = files,
        };
    }

    /// <summary>True when candidate is the root itself or a descendant of it.</summary>
    public static bool IsPathWithin(string candidatePath, string rootPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

            // Current dotnet SDKs print TargetPath (ending in .dll), while
            // older output/tests may provide TargetDir (ending in a slash).
            // Normalize both shapes to the artifact directory.
            var extension = Path.GetExtension(path);
            lastMatch = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path)
                : path;
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

    private static bool IsManagedLaunchArtifact(string path)
        => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteTransactionDirectory(string transactionRoot, string targetParent)
    {
        try
        {
            if (IsPathWithin(transactionRoot, targetParent)
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(transactionRoot)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetParent)),
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }
        catch
        {
            // Deployment has already succeeded or failed; temporary cleanup is best effort.
        }
    }
}
