using System.Diagnostics;

namespace PuddingDesktop.Storage;

public sealed class StorageAnalysisService(
    IDataRootSafetyValidator safetyValidator,
    StorageCategoryCatalog categoryCatalog) : IStorageAnalysisService
{
    private const int MaxWarnings = 100;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public Task<StorageSnapshot> AnalyzeAsync(
        string dataRoot,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
        => Task.Run(
            () => AnalyzeCore(dataRoot, progress, cancellationToken),
            cancellationToken);

    private StorageSnapshot AnalyzeCore(
        string dataRoot,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validated = safetyValidator.ValidateDataRoot(dataRoot);
        var totals = categoryCatalog.Definitions.ToDictionary(
            definition => definition.Kind,
            _ => new MutableCategoryTotal());
        var warnings = new List<StorageScanWarning>();
        var pending = new Stack<string>();
        pending.Push(validated.DataRoot);

        long scannedFiles = 0;
        long scannedBytes = 0;
        var progressTimer = Stopwatch.StartNew();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(currentDirectory).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, currentDirectory, ex.Message);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    entry.Refresh();
                    if (!entry.Exists)
                    {
                        AddWarning(warnings, entry.FullName, "扫描期间文件或目录已消失。");
                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        AddWarning(warnings, entry.FullName, "已跳过符号链接或 Junction。");
                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry.FullName);
                        continue;
                    }

                    if (entry is not FileInfo file)
                        continue;

                    var length = file.Length;
                    var relativePath = Path.GetRelativePath(validated.DataRoot, file.FullName);
                    var definition = categoryCatalog.Classify(relativePath);
                    var total = totals[definition.Kind];
                    total.FileCount++;
                    total.LogicalBytes += length;
                    scannedFiles++;
                    scannedBytes += length;

                    if (progress is not null && progressTimer.Elapsed >= ProgressInterval)
                    {
                        progress.Report(new StorageScanProgress(
                            scannedFiles,
                            scannedBytes,
                            relativePath));
                        progressTimer.Restart();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddWarning(warnings, entry.FullName, ex.Message);
                }
            }
        }

        progress?.Report(new StorageScanProgress(scannedFiles, scannedBytes, string.Empty));

        long driveTotalBytes = 0;
        long driveFreeBytes = 0;
        try
        {
            var drive = new DriveInfo(validated.DriveRoot);
            if (drive.IsReady)
            {
                driveTotalBytes = drive.TotalSize;
                driveFreeBytes = drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddWarning(warnings, validated.DriveRoot, $"无法读取磁盘容量：{ex.Message}");
        }

        var categories = categoryCatalog.Definitions
            .OrderBy(definition => definition.Order)
            .Select(definition => new StorageCategorySnapshot
            {
                Definition = definition,
                LogicalBytes = totals[definition.Kind].LogicalBytes,
                FileCount = totals[definition.Kind].FileCount,
            })
            .ToArray();

        return new StorageSnapshot
        {
            DataRoot = validated.DataRoot,
            CapturedAt = DateTimeOffset.Now,
            LogicalBytes = scannedBytes,
            AllocatedBytes = null,
            DriveTotalBytes = driveTotalBytes,
            DriveFreeBytes = driveFreeBytes,
            Categories = categories,
            Warnings = warnings,
        };
    }

    private static void AddWarning(
        ICollection<StorageScanWarning> warnings,
        string path,
        string message)
    {
        if (warnings.Count < MaxWarnings)
            warnings.Add(new StorageScanWarning(path, message));
    }

    private sealed class MutableCategoryTotal
    {
        public long LogicalBytes { get; set; }
        public long FileCount { get; set; }
    }
}
