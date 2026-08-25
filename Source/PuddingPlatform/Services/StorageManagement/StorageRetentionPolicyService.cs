using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单个语义类型的有效策略（目录默认 + system.json 覆盖合并结果）。</summary>
public sealed record EffectiveTargetPolicy
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required bool Enabled { get; init; }
    public required int RetentionDays { get; init; }
    public required bool AutomaticCleanupAllowed { get; init; }
    /// <summary>fail-closed：配置非法时目标被强制禁用并产生告警。</summary>
    public bool Suspended { get; init; }
}

/// <summary>有效策略快照。</summary>
public sealed record EffectiveStoragePolicy
{
    public required int PolicyRevision { get; init; }
    public required bool AutomaticCleanupEnabled { get; init; }
    public required int RunIntervalHours { get; init; }
    public required int StartupDelaySeconds { get; init; }
    public DateTimeOffset? LastCompletedAtUtc { get; init; }
    public required IReadOnlyList<EffectiveTargetPolicy> Targets { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// ADR-076 §9 保留策略服务：&lt;DataRoot&gt;/config/system.json storageManagement 节的
/// 原子读写（JsonNode 保序保留未知字段）、expectedRevision CAS 与目录范围校验。
/// 缺失项使用代码安全默认；非法值 fail closed（对应目标暂停并报警，绝不按猜测清理）。
/// </summary>
public sealed class StorageRetentionPolicyService
{
    private const int DefaultRunIntervalHours = 24;
    private const int DefaultStartupDelaySeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<StorageRetentionPolicyService> _logger;
    private readonly object _gate = new();
    private EffectiveStoragePolicy? _cached;

    public StorageRetentionPolicyService(
        PuddingDataPaths paths,
        ILogger<StorageRetentionPolicyService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string SystemConfigPath => _paths.SystemConfigFile("system.json");

    public async Task<EffectiveStoragePolicy> GetEffectivePolicyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cached is not null)
                return _cached;
        }

        var policy = await LoadAsync(ct);
        lock (_gate)
        {
            _cached ??= policy;
            return _cached;
        }
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<EffectiveStoragePolicy> LoadAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var config = await ReadStorageSectionAsync(warnings, ct);

        var revision = config?.PolicyRevision ?? 0;
        var automatic = config?.AutomaticCleanup;
        var intervalHours = automatic?.RunIntervalHours is { } hours and >= 1 and <= 720
            ? hours
            : DefaultRunIntervalHours;
        if (automatic?.RunIntervalHours is { } invalidHours and (< 1 or > 720))
            warnings.Add($"runIntervalHours={invalidHours} 越界，已回退默认 {DefaultRunIntervalHours}");
        var startupDelay = automatic?.StartupDelaySeconds is { } seconds and >= 0 and <= 3600
            ? seconds
            : DefaultStartupDelaySeconds;

        var configured = automatic?.Targets;
        var targets = new List<EffectiveTargetPolicy>();
        foreach (var definition in StorageDataClassCatalog.Definitions)
        {
            var defaultEnabled = definition.AutomaticCleanupAllowed && !definition.RequiresRollupBeforeAutomatic;
            PuddingStorageTargetPolicy? overridePolicy = null;
            if (configured is not null && configured.TryGetValue(definition.TargetId, out var found))
                overridePolicy = found;

            var suspended = false;
            var enabled = defaultEnabled;
            var retentionDays = definition.DefaultRetentionDays ?? 0;

            if (overridePolicy is not null)
            {
                if (overridePolicy.Enabled is { } explicitEnabled)
                    enabled = explicitEnabled;

                if (overridePolicy.RetentionDays is { } days)
                {
                    var min = definition.MinRetentionDays ?? 1;
                    var max = definition.MaxRetentionDays ?? 365;
                    if (days == 0 || days < min || days > max)
                    {
                        suspended = true;
                        enabled = false;
                        warnings.Add(
                            $"目标 {definition.TargetId} 保留期 {days} 天非法（允许 {min}–{max}，0 不代表立即删除），该目标自动清理已暂停");
                    }
                    else
                    {
                        retentionDays = days;
                    }
                }
            }

            if (enabled && !definition.AutomaticCleanupAllowed)
            {
                enabled = false;
                warnings.Add($"目标 {definition.TargetId} 不允许自动清理，已忽略启用配置");
            }

            targets.Add(new EffectiveTargetPolicy
            {
                TargetId = definition.TargetId,
                DisplayName = definition.DisplayName,
                Enabled = enabled,
                RetentionDays = retentionDays,
                AutomaticCleanupAllowed = definition.AutomaticCleanupAllowed,
                Suspended = suspended,
            });
        }

        if (configured is not null)
        {
            var known = new HashSet<string>(
                StorageDataClassCatalog.Definitions.Select(d => d.TargetId), StringComparer.Ordinal);
            foreach (var key in configured.Keys.Where(k => !known.Contains(k)))
                warnings.Add($"未知策略目标 {key} 已忽略");
        }

        return new EffectiveStoragePolicy
        {
            PolicyRevision = revision,
            AutomaticCleanupEnabled = automatic?.Enabled ?? true,
            RunIntervalHours = intervalHours,
            StartupDelaySeconds = startupDelay,
            LastCompletedAtUtc = config?.LastCompletedAtUtc,
            Targets = targets,
            Warnings = [.. warnings.Distinct(StringComparer.Ordinal)],
        };
    }

    private async Task<PuddingStorageManagementConfig?> ReadStorageSectionAsync(
        List<string> warnings, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(SystemConfigPath))
                return null;

            var node = JsonNode.Parse(await File.ReadAllTextAsync(SystemConfigPath, ct));
            var section = node?["storageManagement"];
            if (section is null)
                return null;
            return section.Deserialize<PuddingStorageManagementConfig>(JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add($"system.json storageManagement 读取失败，自动清理 fail closed：{ex.Message}");
            return new PuddingStorageManagementConfig
            {
                AutomaticCleanup = new PuddingStorageAutomaticCleanupConfig { Enabled = false },
            };
        }
    }

    /// <summary>更新策略：CAS 校验 expectedRevision；范围校验；JsonNode 原子写保留其它节。</summary>
    public async Task<EffectiveStoragePolicy> UpdateAsync(
        StorageRetentionPolicyUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await ReadStorageSectionAsync([], CancellationToken.None);
        var currentRevision = current?.PolicyRevision ?? 0;
        if (request.ExpectedRevision != currentRevision)
            throw new StorageMaintenanceCoordinator.StorageAdminException(
                StorageAdminErrorCodes.PolicyConflict,
                $"策略已被其他会话修改（当前 revision={currentRevision}），请刷新后重试。");

        var root = await ReadRootAsync(ct) ?? new JsonObject();
        var section = root["storageManagement"] as JsonObject ?? new JsonObject();
        root["storageManagement"] = section;
        section["policyRevision"] = currentRevision + 1;

        var automatic = section["automaticCleanup"] as JsonObject ?? new JsonObject();
        section["automaticCleanup"] = automatic;

        if (request.AutomaticCleanupEnabled is { } enabled)
            automatic["enabled"] = enabled;

        if (request.Targets is { Count: > 0 })
        {
            var targets = automatic["targets"] as JsonObject ?? new JsonObject();
            automatic["targets"] = targets;
            foreach (var update in request.Targets)
            {
                var definition = StorageDataClassCatalog.Find(update.TargetId);
                if (definition is null)
                    throw new StorageMaintenanceCoordinator.StorageAdminException(
                        StorageAdminErrorCodes.TargetUnknown, $"未知存储类型：{update.TargetId}");
                if (!definition.AutomaticCleanupAllowed && update.Enabled == true)
                    throw new StorageMaintenanceCoordinator.StorageAdminException(
                        StorageAdminErrorCodes.TargetProtected, $"类型 {update.TargetId} 不允许自动清理。");

                var entry = targets[update.TargetId] as JsonObject ?? new JsonObject();
                targets[update.TargetId] = entry;

                if (update.Enabled is { } targetEnabled)
                    entry["enabled"] = targetEnabled;
                if (update.RetentionDays is { } days)
                {
                    var min = definition.MinRetentionDays ?? 1;
                    var max = definition.MaxRetentionDays ?? 365;
                    if (days == 0 || days < min || days > max)
                        throw new StorageMaintenanceCoordinator.StorageAdminException(
                            StorageAdminErrorCodes.PolicyConflict,
                            $"类型 {update.TargetId} 保留期必须在 {min}–{max} 天之间（0 不是合法保留期）。");
                    entry["retentionDays"] = days;
                }
            }
        }

        await AtomicFileWriter.WriteAsync(SystemConfigPath, root.ToJsonString(JsonOptions), ct);
        InvalidateCache();
        _logger.LogInformation(
            "[StoragePolicy] updated revision={Revision}→{Next}",
            currentRevision, currentRevision + 1);
        return await GetEffectivePolicyAsync(ct);
    }

    /// <summary>调度器写回 lastCompletedAtUtc（低频、CAS-free，保留其它字段）。</summary>
    public async Task MarkAutomaticRunCompletedAsync(CancellationToken ct = default)
    {
        var root = await ReadRootAsync(ct) ?? new JsonObject();
        var section = root["storageManagement"] as JsonObject ?? new JsonObject();
        root["storageManagement"] = section;
        section["lastCompletedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        await AtomicFileWriter.WriteAsync(SystemConfigPath, root.ToJsonString(JsonOptions), ct);
        InvalidateCache();
    }

    private async Task<JsonNode?> ReadRootAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(SystemConfigPath))
                return null;
            return JsonNode.Parse(await File.ReadAllTextAsync(SystemConfigPath, ct));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new StorageMaintenanceCoordinator.StorageAdminException(
                StorageAdminErrorCodes.DataRootUnsafe, $"system.json 无法解析：{ex.Message}");
        }
    }

    public StorageRetentionPolicyDto ToDto(EffectiveStoragePolicy policy) => new()
    {
        PolicyRevision = policy.PolicyRevision,
        AutomaticCleanupEnabled = policy.AutomaticCleanupEnabled,
        RunIntervalHours = policy.RunIntervalHours,
        StartupDelaySeconds = policy.StartupDelaySeconds,
        LastCompletedAtUtc = policy.LastCompletedAtUtc,
        NextRunEstimateUtc = policy.AutomaticCleanupEnabled && policy.LastCompletedAtUtc is { } last
            ? last.AddHours(policy.RunIntervalHours)
            : null,
        Targets = [.. policy.Targets.Select(t => new StorageRetentionPolicyTargetDto
        {
            TargetId = t.TargetId,
            DisplayName = t.DisplayName,
            Enabled = t.Enabled,
            RetentionDays = t.RetentionDays,
            AutomaticCleanupAllowed = t.AutomaticCleanupAllowed,
            DefaultRetentionDays = StorageDataClassCatalog.Find(t.TargetId)?.DefaultRetentionDays,
            MinRetentionDays = StorageDataClassCatalog.Find(t.TargetId)?.MinRetentionDays,
            MaxRetentionDays = StorageDataClassCatalog.Find(t.TargetId)?.MaxRetentionDays,
        })],
        Warnings = policy.Warnings,
    };
}
