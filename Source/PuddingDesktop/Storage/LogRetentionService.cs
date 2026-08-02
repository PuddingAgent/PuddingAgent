namespace PuddingDesktop.Storage;

public sealed class LogRetentionService(IDataRootSafetyValidator safetyValidator) : ILogRetentionService
{
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> AllowedExtensions = new(
        [".log", ".jsonl", ".txt", ".gz", ".zip"],
        StringComparer.OrdinalIgnoreCase);

    public Task<LogCleanupPreview> PreviewAsync(
        string dataRoot,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (retention < MinimumRetention)
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "日志至少保留 24 小时。");

        return Task.Run(
            () => PreviewCore(dataRoot, retention, cancellationToken),
            cancellationToken);
    }

    public Task<LogCleanupResult> ExecuteAsync(
        LogCleanupPreview preview,
        IProgress<LogCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Task.Run(
            () => ExecuteCore(preview, progress, cancellationToken),
            cancellationToken);
    }

    private LogCleanupPreview PreviewCore(
        string dataRoot,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var validated = safetyValidator.ValidateLogRoot(dataRoot, requireLogDirectory: false);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - retention;
        var candidates = new List<LogCleanupCandidate>();

        if (Directory.Exists(validated.LogRoot))
            CollectCandidates(validated.LogRoot, cutoff, candidates, cancellationToken);

        candidates.Sort((left, right) =>
        {
            var timeComparison = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
            return timeComparison != 0
                ? timeComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        });

        return new LogCleanupPreview
        {
            PreviewId = Guid.NewGuid(),
            DataRoot = validated.DataRoot,
            LogRoot = validated.LogRoot,
            CreatedAt = now,
            CutoffUtc = cutoff,
            Retention = retention,
            Candidates = candidates,
            CandidateBytes = candidates.Sum(item => item.Length),
        };
    }

    private LogCleanupResult ExecuteCore(
        LogCleanupPreview preview,
        IProgress<LogCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (preview.Retention < MinimumRetention)
            throw new StorageSafetyException("清理计划的保留时间小于 24 小时。");
        if (DateTimeOffset.UtcNow - preview.CreatedAt > PreviewLifetime)
            throw new StorageSafetyException("清理预览已过期，请重新扫描。");

        var validated = safetyValidator.ValidateLogRoot(
            preview.DataRoot,
            requireLogDirectory: preview.Candidates.Count > 0);
        if (!PathEquals(validated.DataRoot, preview.DataRoot)
            || !PathEquals(validated.LogRoot, preview.LogRoot))
        {
            throw new StorageSafetyException("清理预览与当前数据目录不匹配。");
        }

        var deletedFiles = 0;
        long deletedBytes = 0;
        var skippedFiles = 0;
        var failures = new List<LogCleanupFailure>();

        for (var index = 0; index < preview.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = preview.Candidates[index];
            var relativePath = candidate.RelativePath;

            try
            {
                var fullPath = DataRootSafetyValidator.Normalize(candidate.FullPath);
                if (!safetyValidator.IsDescendantOf(fullPath, validated.LogRoot)
                    || !AllowedExtensions.Contains(Path.GetExtension(fullPath)))
                {
                    throw new StorageSafetyException("候选日志路径超出允许范围。");
                }

                if (!File.Exists(fullPath))
                {
                    skippedFiles++;
                    continue;
                }

                var info = new FileInfo(fullPath);
                info.Refresh();
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || info.Length != candidate.Length
                    || new DateTimeOffset(info.LastWriteTimeUtc) != candidate.LastWriteTimeUtc
                    || new DateTimeOffset(info.CreationTimeUtc) != candidate.CreationTimeUtc
                    || new DateTimeOffset(info.LastWriteTimeUtc) >= preview.CutoffUtc)
                {
                    skippedFiles++;
                    continue;
                }

                if (!CanOpenExclusively(fullPath))
                {
                    skippedFiles++;
                    continue;
                }

                File.Delete(fullPath);
                deletedFiles++;
                deletedBytes += candidate.Length;
            }
            catch (FileNotFoundException)
            {
                skippedFiles++;
            }
            catch (DirectoryNotFoundException)
            {
                skippedFiles++;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or StorageSafetyException)
            {
                failures.Add(new LogCleanupFailure(relativePath, ex.Message));
            }
            finally
            {
                progress?.Report(new LogCleanupProgress(
                    index + 1,
                    preview.Candidates.Count,
                    deletedBytes,
                    relativePath));
            }
        }

        RemoveEmptyDirectories(validated.LogRoot);
        return new LogCleanupResult
        {
            PreviewId = preview.PreviewId,
            DeletedFiles = deletedFiles,
            DeletedBytes = deletedBytes,
            SkippedFiles = skippedFiles,
            FailedFiles = failures.Count,
            Failures = failures,
        };
    }

    private static void CollectCandidates(
        string logRoot,
        DateTimeOffset cutoff,
        ICollection<LogCleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(logRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(current).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    entry.Refresh();
                    if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry.FullName);
                        continue;
                    }

                    if (entry is not FileInfo file
                        || !AllowedExtensions.Contains(file.Extension))
                    {
                        continue;
                    }

                    var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc);
                    if (lastWrite >= cutoff)
                        continue;

                    candidates.Add(new LogCleanupCandidate
                    {
                        FullPath = file.FullName,
                        RelativePath = Path.GetRelativePath(logRoot, file.FullName),
                        Length = file.Length,
                        LastWriteTimeUtc = lastWrite,
                        CreationTimeUtc = new DateTimeOffset(file.CreationTimeUtc),
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Files that change or become inaccessible during preview are not candidates.
                }
            }
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void RemoveEmptyDirectories(string logRoot)
    {
        if (!Directory.Exists(logRoot))
            return;

        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(logRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (DataRootSafetyValidator.IsReparsePoint(child))
                        continue;
                    directories.Add(child);
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageSafetyException)
            {
            }
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            try { Directory.Delete(directory, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            DataRootSafetyValidator.Normalize(left),
            DataRootSafetyValidator.Normalize(right),
            StringComparison.OrdinalIgnoreCase);
}
