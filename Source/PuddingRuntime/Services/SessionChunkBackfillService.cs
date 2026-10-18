using System.Text.Json;
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
/// BackgroundService：宿主启动后延迟（Ready 保护窗口）在后台跑一次即退出；
/// Enabled=false（默认）时直接跳过。
///
/// 可恢复性（Plan §8.1）：
/// - 启动冻结高水位 H = 扫描开始时 platform.db 的 MAX(ChatMessages.Id)，只扫描 (lastId,H]；
/// - checkpoint 绑定 dataset identity（platform/memory 库路径）+ IndexConfigVersion；
/// - 恢复旧库（maxId 低于 checkpoint）、换库或配置版本变化时旧断点失效，从头重建；
/// - 每批成功后才推进并原子写 checkpoint；批内失败/取消只重扫该批（索引器幂等）。
/// </summary>
public sealed class SessionChunkBackfillService : BackgroundService
{
    private readonly IChatMessageBackfillSource _backfillSource;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _memoryDbFactory;
    private readonly ISessionChunkIndexer _indexer;
    private readonly IOptions<SessionChunkBackfillOptions> _options;
    private readonly ILogger<SessionChunkBackfillService> _logger;
    private readonly PuddingDataPaths? _paths;

    private static readonly JsonSerializerOptions CheckpointJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SessionChunkBackfillService(
        IChatMessageBackfillSource backfillSource,
        IDbContextFactory<MemoryLibraryDbContext> memoryDbFactory,
        ISessionChunkIndexer indexer,
        IOptions<SessionChunkBackfillOptions> options,
        ILogger<SessionChunkBackfillService> logger,
        PuddingDataPaths? paths = null)
    {
        _backfillSource = backfillSource;
        _memoryDbFactory = memoryDbFactory;
        _indexer = indexer;
        _options = options;
        _logger = logger;
        _paths = paths;
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
    /// 0) 等待 Ready 保护窗口；
    /// 1) 冻结高水位 H、读取/校验 checkpoint；
    /// 2) 键集分页扫 (lastId,H]，批内过滤 role、跳过已索引，逐条调索引器；
    /// 3) 每批成功推进 lastId 并原子写 checkpoint；批间延迟限速。
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
        var startupDelayMs = Math.Max(0, options.StartupDelayMs);
        var configVersion = string.IsNullOrWhiteSpace(options.IndexConfigVersion)
            ? "1"
            : options.IndexConfigVersion.Trim();
        var checkpointPath = ResolveCheckpointPath(options);

        if (startupDelayMs > 0)
        {
            _logger.LogInformation(
                "[SessionChunkBackfill] waiting Ready protection window {DelayMs}ms",
                startupDelayMs);
            await Task.Delay(startupDelayMs, ct);
        }

        var highWatermark = await _backfillSource.GetMaxIdAsync(ct);
        var datasetIdentity = await BuildDatasetIdentityAsync(ct);

        long lastId = 0;
        long indexed = 0;
        long skipped = 0;
        long nonUserAssistant = 0;
        long batches = 0;

        var checkpoint = checkpointPath is null
            ? null
            : await TryLoadCheckpointAsync(checkpointPath, ct);
        if (checkpoint is not null)
        {
            if (!string.Equals(checkpoint.IndexConfigVersion, configVersion, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "[SessionChunkBackfill] checkpoint invalidated: index config version changed {Old} -> {New}; full rescan",
                    checkpoint.IndexConfigVersion, configVersion);
            }
            else if (!string.Equals(checkpoint.DatasetIdentity, datasetIdentity, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "[SessionChunkBackfill] checkpoint invalidated: dataset identity changed; full rescan");
            }
            else if (checkpoint.LastId > highWatermark || checkpoint.HighWatermark > highWatermark)
            {
                // 恢复旧库/删库后 maxId 降低：旧断点会把新数据永久跳过。
                _logger.LogWarning(
                    "[SessionChunkBackfill] checkpoint invalidated: restore detected (checkpointLastId={CheckpointLastId}, checkpointH={CheckpointH}, currentMaxId={MaxId}); full rescan",
                    checkpoint.LastId, checkpoint.HighWatermark, highWatermark);
            }
            else
            {
                lastId = checkpoint.LastId;
                indexed = checkpoint.Indexed;
                skipped = checkpoint.Skipped;
                nonUserAssistant = checkpoint.NonUserAssistant;
                batches = checkpoint.Batches;
                _logger.LogInformation(
                    "[SessionChunkBackfill] resume from checkpoint lastId={LastId} H={HighWatermark} indexed={Indexed} skipped={Skipped}",
                    lastId, highWatermark, indexed, skipped);
            }
        }

        _logger.LogInformation(
            "[SessionChunkBackfill] start batchSize={BatchSize} delayMs={DelayMs} lastId={LastId} H={HighWatermark} configVersion={ConfigVersion} checkpoint={CheckpointPath}",
            batchSize, delayMs, lastId, highWatermark, configVersion,
            checkpointPath ?? "<none>");

        while (!ct.IsCancellationRequested && lastId < highWatermark)
        {
            // a) 键集分页：通过 IChatMessageBackfillSource 接口（避免直接依赖 PuddingPlatform）。
            var batch = await _backfillSource.GetBatchAfterIdAsync(lastId, batchSize, ct);
            if (batch.Count == 0)
                break;

            // 本次冻结 H：忽略扫描期间新增的行（由下一次回填或增量索引处理）。
            var bounded = batch.Where(m => m.Id <= highWatermark).ToList();
            if (bounded.Count == 0)
                break;

            // b) 批内先过滤 role 非 user/assistant 的行，省 embedding 调用。
            var candidates = bounded.Where(m => m.Role is "user" or "assistant").ToList();
            nonUserAssistant += bounded.Count - candidates.Count;

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

            lastId = bounded[^1].Id;
            batches++;

            // e) 每批成功后才持久化进度；失败/取消时该批会重扫（索引器幂等）。
            if (checkpointPath is not null)
            {
                await SaveCheckpointAsync(
                    checkpointPath,
                    new SessionChunkBackfillCheckpoint(
                        datasetIdentity,
                        configVersion,
                        lastId,
                        highWatermark,
                        batches,
                        indexed,
                        skipped,
                        nonUserAssistant,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    ct);
            }

            // 健康自证：每 10 批输出一次进度。
            if (batches % 10 == 0)
            {
                _logger.LogInformation(
                    "[SessionChunkBackfill] progress batch={Batches} lastId={LastId} H={HighWatermark} indexed={Indexed} skipped={Skipped} nonUserAssistant={NonUserAssistant}",
                    batches, lastId, highWatermark, indexed, skipped, nonUserAssistant);
            }

            // 批窗口已越过冻结 H：本轮结束。
            if (bounded.Count < batch.Count)
                break;

            // f) 每批后延迟，限速 embedding 调用（200ms 默认，可校准）。
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);
        }

        // 收尾写一次 checkpoint（包含 H=0 / 无消息场景），确保下次启动不重复扫描。
        if (checkpointPath is not null && !ct.IsCancellationRequested)
        {
            await SaveCheckpointAsync(
                checkpointPath,
                new SessionChunkBackfillCheckpoint(
                    datasetIdentity,
                    configVersion,
                    Math.Max(lastId, highWatermark),
                    highWatermark,
                    batches,
                    indexed,
                    skipped,
                    nonUserAssistant,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ct);
        }

        _logger.LogInformation(
            "[SessionChunkBackfill] completed batches={Batches} lastId={LastId} H={HighWatermark} indexed={Indexed} skipped={Skipped} nonUserAssistant={NonUserAssistant}",
            batches, lastId, highWatermark, indexed, skipped, nonUserAssistant);
    }

    private string? ResolveCheckpointPath(SessionChunkBackfillOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CheckpointFilePath))
            return Path.GetFullPath(options.CheckpointFilePath);

        if (_paths is null)
            return null;

        return Path.Combine(
            _paths.RuntimeRoot,
            "backfill",
            "session-chunk-checkpoint.json");
    }

    private async Task<string> BuildDatasetIdentityAsync(CancellationToken ct)
    {
        var platformIdentity = await _backfillSource.GetDatasetIdentityAsync(ct);
        string memoryIdentity;
        await using (var memoryDb = await _memoryDbFactory.CreateDbContextAsync(ct))
        {
            memoryIdentity = memoryDb.Database.GetDbConnection().DataSource ?? string.Empty;
        }

        return $"{platformIdentity}|memory:{memoryIdentity}";
    }

    private async Task<SessionChunkBackfillCheckpoint?> TryLoadCheckpointAsync(
        string path,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            await using var stream = File.OpenRead(path);
            var checkpoint = await JsonSerializer.DeserializeAsync<SessionChunkBackfillCheckpoint>(
                stream, CheckpointJsonOptions, ct);
            return checkpoint;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 损坏/不可读 checkpoint 不能阻断回填：从头扫描（索引器幂等）。
            _logger.LogWarning(ex,
                "[SessionChunkBackfill] checkpoint unreadable path={Path}; falling back to full scan",
                path);
            return null;
        }
    }

    private async Task SaveCheckpointAsync(
        string path,
        SessionChunkBackfillCheckpoint checkpoint,
        CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, checkpoint, CheckpointJsonOptions, ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // checkpoint 写失败只影响续跑效率，不影响索引正确性（幂等跳过已存在）。
            _logger.LogWarning(ex,
                "[SessionChunkBackfill] checkpoint write failed path={Path}; continuing",
                path);
        }
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

/// <summary>
/// 回填断点：绑定数据集身份 + 索引配置版本 + 冻结高水位。
/// LastId=已成功推进的位置；HighWatermark=启动时冻结的 H（不得随写入变化）。
/// </summary>
public sealed record SessionChunkBackfillCheckpoint(
    string DatasetIdentity,
    string IndexConfigVersion,
    long LastId,
    long HighWatermark,
    long Batches,
    long Indexed,
    long Skipped,
    long NonUserAssistant,
    long UpdatedAtUnixMs);
