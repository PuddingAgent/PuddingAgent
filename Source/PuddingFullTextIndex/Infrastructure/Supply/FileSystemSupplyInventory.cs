using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 文件系统语料清点（真实实现，<b>只读</b>）。
/// <para>
/// 口径必须与索引引擎一致，否则 Plan 的估算与 job 的计数会各自漂移：
/// ① 扩展名 ∈ <see cref="FullTextIndexOptions.IsIndexableExtension"/>；
/// ② 不被 <see cref="FullTextIndexOptions.IsExcludedPath"/>（排除清单派生自 <c>PathNoiseRules</c> 单一真源）排除；
/// ③ 文件大小 ∈ (0, <see cref="FullTextIndexOptions.MaxFileSizeBytes"/>]（空文件与超限文件引擎也不索引）。
/// </para>
/// <para>
/// 遍历与 <c>LuceneSearchEngine.EnumerateFilesPruned</c> 同构：显式栈单次 DFS + 噪声目录剪枝；
/// 单个目录的 <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> 局部化，不中断整次扫描。
/// </para>
/// </summary>
public sealed class FileSystemSupplyInventory : IFullTextIndexSupplyInventory
{
    private readonly FullTextIndexOptions _options;

    public FileSystemSupplyInventory(FullTextIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public Task<SupplyInventory> MeasureAsync(string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"供给 scope 目录不存在：{rootPath}");

        return Task.FromResult(Measure(rootPath, ct));
    }

    private SupplyInventory Measure(string rootPath, CancellationToken ct)
    {
        var fileCount = 0;
        long totalBytes = 0;
        var bytesByExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(file);
                if (!_options.IsIndexableExtension(extension))
                    continue;

                if (_options.IsExcludedPath(file, rootPath))
                    continue;

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (length <= 0 || length > _options.MaxFileSizeBytes)
                    continue;

                fileCount++;
                totalBytes += length;

                var key = extension.ToLowerInvariant();
                bytesByExtension[key] = bytesByExtension.TryGetValue(key, out var previous) ? previous + length : length;
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                ct.ThrowIfCancellationRequested();

                // 噪声目录剪枝：判据与 LuceneSearchEngine 完全一致（目录名 ∈ 单一真源排除清单）。
                if (_options.ExcludedDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                    continue;

                pending.Push(subDirectory);
            }
        }

        return new SupplyInventory(fileCount, totalBytes, bytesByExtension);
    }
}
