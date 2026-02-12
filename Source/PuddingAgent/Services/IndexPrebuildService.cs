using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingFullTextIndex.Contracts;

namespace PuddingAgent.Services;

/// <summary>
/// 应用启动时在后台异步构建工作区根目录的 Lucene 全文索引。
/// 不阻塞应用启动，首次 search_grep 直接走索引，毫秒级返回。
/// </summary>
public sealed class IndexPrebuildService : IHostedService
{
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly ILogger<IndexPrebuildService> _logger;

    public IndexPrebuildService(
        IFullTextSearchEngine searchEngine,
        ILogger<IndexPrebuildService> logger)
    {
        _searchEngine = searchEngine;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        Console.WriteLine("[Startup] IndexPrebuildService.StartAsync — first hosted service reached!");
        // 延迟启动索引构建，确保 Kestrel 先绑定端口。
        // 立即启动 fire-and-forget 会在 THREAD_POOL 有限时饥饿 Kestrel 绑定。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            await BuildIndexAsync(ct);
        }, ct);
        return Task.CompletedTask;
    }

    private async Task BuildIndexAsync(CancellationToken ct)
    {
        try
        {
            var workspaceRoot = Directory.GetCurrentDirectory();

            if (_searchEngine.HasIndex(workspaceRoot))
            {
                _logger.LogInformation(
                    "[IndexPrebuild] Index already exists for {Dir}, skipping", workspaceRoot);
                return;
            }

            _logger.LogInformation(
                "[IndexPrebuild] Starting background index build for {Dir}...", workspaceRoot);

            var result = await _searchEngine.BuildIndexAsync(workspaceRoot, ct: ct);

            if (result.Success)
            {
                _logger.LogInformation(
                    "[IndexPrebuild] Index build completed: {Files} files, {Bytes} bytes, {Elapsed}ms",
                    result.IndexedFileCount, result.TotalBytes, result.ElapsedMs);
            }
            else
            {
                _logger.LogWarning(
                    "[IndexPrebuild] Index build failed: {Error}", result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[IndexPrebuild] Index build cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IndexPrebuild] Index build error (non-fatal)");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
