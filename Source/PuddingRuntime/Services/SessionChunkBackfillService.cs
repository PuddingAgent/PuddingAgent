using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services;

/// <summary>
/// WP-L2d：SessionChunkVectors 存量回填 job。
/// BackgroundService：宿主启动后在后台跑一次即退出；Enabled=false（默认）时直接跳过。
/// 从 platform.db 的 ChatMessages（约 43 万行）按键集分页扫描，
/// 批内先过滤 user/assistant 角色，再跳过 SessionChunkVectors 已存在的 MessageId，
/// 剩余逐条调 ISessionChunkIndexer.IndexMessageAsync（索引器自身幂等兜底）。
/// </summary>
public sealed class SessionChunkBackfillService : BackgroundService
{
    private readonly IChatMessageBackfillSource _backfillSource;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _memoryDbFactory;
    private readonly ISessionChunkIndexer _indexer;
    private readonly IOptions<SessionChunkBackfillOptions> _options;
    private readonly ILogger<SessionChunkBackfillService> _logger;

    public SessionChunkBackfillService(
        IChatMessageBackfillSource backfillSource,
        IDbContextFactory<MemoryLibraryDbContext> memoryDbFactory,
        ISessionChunkIndexer indexer,
        IOptions<SessionChunkBackfillOptions> options,
        ILogger<SessionChunkBackfillService> logger)
    {
        _backfillSource = backfillSource;
        _memoryDbFactory = memoryDbFactory;
        _indexer = indexer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync invokes ExecuteAsync synchronously until
        // the first incomplete await. Yield first so a large historical backfill
        // can never delay WebApplication.StartAsync or the Desktop Ready signal.
        await Task.Yield();

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[SessionChunkBackfill] cancelled by host shutdown");
        }
        catch (Exception ex)
        {
            // 健康自证：回填失败不拖垮宿主启动，但必须留下错误日志。
            _logger.LogError(ex, "[SessionChunkBackfill] backfill failed");
        }
    }

    /// <summary>
    /// 回填主流程（public 便于测试直调）：
    /// a) 键集分页扫 ChatMessages：Where(Id &gt; lastId).OrderBy(Id).Take(BatchSize)，lastId 递进；
    /// b) 批内过滤 role 非 user/assistant 的行；
    /// c) 批查 SessionChunkVectors 已存在的 MessageId，跳过已索引；
    /// d) 剩余逐条调 ISessionChunkIndexer.IndexMessageAsync（幂等兜底）；
    /// e) 每批后 Task.Delay(DelayMs, ct)；ct 取消优雅退出。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation(
                "[SessionChunkBackfill] disabled (Enabled=false), skip backfill");
            return;
        }

        var batchSize = Math.Max(1, options.BatchSize);
        var delayMs = Math.Max(0, options.DelayMs);

        long lastId = 0;
        long indexed = 0;
        long skipped = 0;
        long nonUserAssistant = 0;
        long batches = 0;

        _logger.LogInformation(
            "[SessionChunkBackfill] start batchSize={BatchSize} delayMs={DelayMs}",
            batchSize, delayMs);

        while (!ct.IsCancellationRequested)
        {
            // a) 键集分页：通过 IChatMessageBackfillSource 接口（避免直接依赖 PuddingPlatform）。
            var batch = await _backfillSource.GetBatchAfterIdAsync(lastId, batchSize, ct);
            if (batch.Count == 0)
                break;

            // b) 批内先过滤 role 非 user/assistant 的行，省 embedding 调用。
            var candidates = batch.Where(m => m.Role is "user" or "assistant").ToList();
            nonUserAssistant += batch.Count - candidates.Count;

            // c) 批查 SessionChunkVectors 已存在的 MessageId，跳过已索引。
            var existing = await LoadExistingMessageIdsAsync(candidates, ct);
            var pending = candidates.Where(m => !existing.Contains(m.MessageId)).ToList();
            skipped += candidates.Count - pending.Count;

            // d) 剩余的逐条调 indexer（索引器内含角色过滤 + 幂等兜底）。
            foreach (var message in pending)
            {
                ct.ThrowIfCancellationRequested();
                await _indexer.IndexMessageAsync(
                    message.WorkspaceId,
                    message.SessionId,
                    message.MessageId,
                    message.Role,
                    message.Content,
                    ct);
                indexed++;
            }

            lastId = batch[^1].Id;
            batches++;

            // 健康自证：每 10 批输出一次进度。
            if (batches % 10 == 0)
            {
                _logger.LogInformation(
                    "[SessionChunkBackfill] progress batch={Batches} lastId={LastId} indexed={Indexed} skipped={Skipped} nonUserAssistant={NonUserAssistant}",
                    batches, lastId, indexed, skipped, nonUserAssistant);
            }

            // e) 每批后延迟，限速 embedding 调用。
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);
        }

        _logger.LogInformation(
            "[SessionChunkBackfill] completed batches={Batches} indexed={Indexed} skipped={Skipped} nonUserAssistant={NonUserAssistant}",
            batches, indexed, skipped, nonUserAssistant);
    }

    private async Task<HashSet<string>> LoadExistingMessageIdsAsync(
        IReadOnlyCollection<BackfillChatMessageRow> candidates,
        CancellationToken ct)
    {
        var ids = candidates
            .Select(m => m.MessageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        await using var memoryDb = await _memoryDbFactory.CreateDbContextAsync(ct);
        var found = await memoryDb.SessionChunkVectors
            .AsNoTracking()
            .Where(v => ids.Contains(v.MessageId))
            .Select(v => v.MessageId)
            .ToListAsync(ct);

        return new HashSet<string>(found, StringComparer.Ordinal);
    }
}
