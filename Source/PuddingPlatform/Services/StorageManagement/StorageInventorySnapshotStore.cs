using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// ADR-076 §5.1 存储库存快照存储：内存原子发布当前快照 + DataRoot 下有界历史（每小时一点、90 天）。
/// GET overview 只读本存储，永不触发扫描；历史点供 7/30/90 天趋势报表读取。
/// </summary>
public sealed class StorageInventorySnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const int SchemaVersion = 1;
    private static readonly TimeSpan HistoryPointInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    private const int MaxHistoryPoints = 24 * 90;

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<StorageInventorySnapshotStore> _logger;
    private readonly object _gate = new();

    private StorageInventorySnapshotDto _current =
        new()
        {
            SnapshotId = Guid.NewGuid(),
            Revision = 0,
            SchemaVersion = SchemaVersion,
            CapturedAtUtc = DateTimeOffset.MinValue,
            UpdatedAtUtc = DateTimeOffset.MinValue,
            Databases = [],
            Classes = [],
            IsRefreshing = false,
            Warnings = ["快照尚未生成，后台估算进行中"],
        };

    private DateTimeOffset _lastHistoryPointUtc = DateTimeOffset.MinValue;

    private string HistoryDirectory => Path.Combine(_paths.DataRoot, "maintenance", "storage", "inventory");
    private string HistoryFilePath => Path.Combine(HistoryDirectory, "history.jsonl");

    public StorageInventorySnapshotStore(
        PuddingDataPaths paths,
        ILogger<StorageInventorySnapshotStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public StorageInventorySnapshotDto Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>启动时恢复最近一次持久化快照与历史水位（文件缺失/损坏时静默保持空快照）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
                return;

            SerializedSnapshot? last = null;
            var lastPointUtc = DateTimeOffset.MinValue;
            string? line;
        using (var reader = new StreamReader(HistoryFilePath))
            {
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        var point = JsonSerializer.Deserialize<SerializedSnapshot>(line, JsonOptions);
                        if (point is null)
                            continue;
                        last = point;
                        if (point.CapturedAtUtc > lastPointUtc)
                            lastPointUtc = point.CapturedAtUtc;
                    }
                    catch (JsonException)
                    {
                        // 半写行直接跳过，历史文件是 append-only 容错格式。
                    }
                }
            }

            if (last is not null)
            {
                lock (_gate)
                {
                    _current = last.ToDto(isRefreshing: false);
                    _lastHistoryPointUtc = lastPointUtc;
                }

                _logger.LogInformation(
                    "[StorageInventory] restored snapshot revision={Revision} capturedAt={CapturedAt:O}",
                    last.Revision, last.CapturedAtUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageInventory] failed to restore history snapshot");
        }
    }

    /// <summary>
    /// 原子合并受影响分类：数据库文件组整体替换，分类只覆盖传入的 TargetId；
    /// 某一分类失败不清空上一份有效值。返回新 revision。
    /// </summary>
    public long MergeSnapshot(
        IReadOnlyList<StorageInventoryDatabaseDto>? databases,
        IReadOnlyList<StorageInventoryClassDto>? classes,
        bool isRefreshing,
        IReadOnlyList<string>? warnings = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var revision = _current.Revision + 1;
            var previousClasses = _current.Classes.ToDictionary(c => c.TargetId, StringComparer.Ordinal);

            var merged = classes is { Count: > 0 }
                ? classes.Select(Update).ToArray()
                : previousClasses.Values.Select(c => c with { EstimateState = StorageEstimateState.Updated }).ToArray();

            var capturedAt = _current.CapturedAtUtc == DateTimeOffset.MinValue || !isRefreshing
                ? now
                : _current.CapturedAtUtc;

            _current = new StorageInventorySnapshotDto
            {
                SnapshotId = revision == 1 ? Guid.NewGuid() : _current.SnapshotId,
                Revision = revision,
                SchemaVersion = SchemaVersion,
                CapturedAtUtc = capturedAt,
                UpdatedAtUtc = now,
                Databases = databases ?? _current.Databases,
                Classes = merged,
                IsRefreshing = isRefreshing,
                Warnings = warnings ?? (isRefreshing ? _current.Warnings : []),
            };

            StorageInventoryClassDto Update(StorageInventoryClassDto incoming)
            {
                if (!previousClasses.TryGetValue(incoming.TargetId, out var previous))
                    return incoming;
                // 采样失败（Unavailable 且无新值）时保留上一份有效估算。
                if (incoming.EstimateState == StorageEstimateState.Unavailable
                    && incoming.EstimatedBytes is null
                    && previous.EstimatedBytes is not null)
                    return previous with { UpdatedAtUtc = now };
                return incoming;
            }

            return revision;
        }
    }

    /// <summary>按历史节奏持久化一个点（每小时最多一个，90 天有界轮转）。</summary>
    public async Task<bool> TryAppendHistoryPointAsync(CancellationToken ct = default)
    {
        StorageInventorySnapshotDto snapshot;
        lock (_gate)
        {
            if (_current.CapturedAtUtc == DateTimeOffset.MinValue)
                return false;
            if (_current.UpdatedAtUtc - _lastHistoryPointUtc < HistoryPointInterval)
                return false;
            snapshot = _current;
            _lastHistoryPointUtc = _current.UpdatedAtUtc;
        }

        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            var point = SerializedSnapshot.FromDto(snapshot, includeRefreshState: false);
            await File.AppendAllTextAsync(
                HistoryFilePath,
                JsonSerializer.Serialize(point, JsonOptions) + "\n",
                ct);
            await PruneHistoryAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageInventory] failed to append history point");
            return false;
        }
    }

    /// <summary>读取趋势历史点（有界：仅返回 days 天内、最多 1000 点）。</summary>
    public async Task<IReadOnlyList<StorageInventoryTrendPointDto>> ReadTrendAsync(int days, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var points = new List<StorageInventoryTrendPointDto>();
        try
        {
            if (!File.Exists(HistoryFilePath))
                return points;

            string? line;
            using var reader = new StreamReader(HistoryFilePath);
            while ((line = await reader.ReadLineAsync(ct)) != null && points.Count < 1000)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var point = JsonSerializer.Deserialize<SerializedSnapshot>(line, JsonOptions);
                    if (point is null || point.CapturedAtUtc < since)
                        continue;
                    points.Add(new StorageInventoryTrendPointDto
                    {
                        CapturedAtUtc = point.CapturedAtUtc,
                        ClassBytes = point.Classes?.ToDictionary(
                            c => c.TargetId,
                            c => c.EstimatedBytes ?? 0,
                            StringComparer.Ordinal) ?? new Dictionary<string, long>(),
                        DatabaseTotalBytes = point.Databases?.Sum(d => d.TotalBytes) ?? 0,
                    });
                }
                catch (JsonException)
                {
                    // 跳过半写行。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageInventory] failed to read trend history");
        }

        return points;
    }

    private async Task PruneHistoryAsync(CancellationToken ct)
    {
        try
        {
            var lines = new List<string>();
            var cutoff = DateTimeOffset.UtcNow - HistoryRetention;
            string? line;
            using (var reader = new StreamReader(HistoryFilePath))
            {
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    lines.Add(line);
                }
            }

            if (lines.Count <= MaxHistoryPoints)
            {
                var oldest = TryReadTimestamp(lines[0]);
                if (oldest >= cutoff)
                    return;
            }

            var kept = new List<string>();
            foreach (var entry in lines)
            {
                var timestamp = TryReadTimestamp(entry);
                if (timestamp >= cutoff)
                    kept.Add(entry);
            }

            if (kept.Count > MaxHistoryPoints)
                kept = kept[^MaxHistoryPoints..];

            if (kept.Count != lines.Count)
            {
                await AtomicFileWriter.WriteAsync(
                    HistoryFilePath,
                    string.Join("\n", kept) + (kept.Count > 0 ? "\n" : string.Empty),
                    ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageInventory] failed to prune history");
        }
    }

    private static DateTimeOffset TryReadTimestamp(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SerializedSnapshot>(line, JsonOptions)?.CapturedAtUtc
                   ?? DateTimeOffset.MinValue;
        }
        catch (JsonException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed record SerializedSnapshot
    {
        public Guid SnapshotId { get; init; }
        public long Revision { get; init; }
        public DateTimeOffset CapturedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
        public List<StorageInventoryDatabaseDto> Databases { get; init; } = [];
        public List<StorageInventoryClassDto> Classes { get; init; } = [];

        public static SerializedSnapshot FromDto(StorageInventorySnapshotDto dto, bool includeRefreshState)
            => new()
            {
                SnapshotId = dto.SnapshotId,
                Revision = dto.Revision,
                CapturedAtUtc = dto.CapturedAtUtc,
                UpdatedAtUtc = dto.UpdatedAtUtc,
                Databases = dto.Databases.ToList(),
                Classes = dto.Classes.ToList(),
            };

        public StorageInventorySnapshotDto ToDto(bool isRefreshing) => new()
        {
            SnapshotId = SnapshotId,
            Revision = Revision,
            SchemaVersion = SchemaVersion,
            CapturedAtUtc = CapturedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            Databases = Databases,
            Classes = Classes,
            IsRefreshing = isRefreshing,
            Warnings = [],
        };
    }
}
