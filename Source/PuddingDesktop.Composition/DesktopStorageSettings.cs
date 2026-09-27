using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Storage;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-09 inventory and policy slice: reads Core's cached inventory snapshot (never triggering a scan),
/// the sampled trend history and the retention policy, and submits policy updates with the revision the
/// page read (CAS).
/// </summary>
internal sealed class DesktopStorageSettings(IDesktopKernel kernel) : IStorageSettings
{
    public Task<StorageSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.snapshot", (scope, _) =>
        {
            var snapshot = scope.Services.GetRequiredService<StorageInventorySnapshotStore>().Current;
            return Task.FromResult(Map(snapshot));
        }, cancellationToken);

    public Task<IReadOnlyList<StorageTrendPoint>> ReadTrendAsync(int days, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.trend", async (scope, token) =>
        {
            var points = await scope.Services.GetRequiredService<StorageInventorySnapshotStore>()
                .ReadTrendAsync(days, token);
            return (IReadOnlyList<StorageTrendPoint>)points.Select(point => new StorageTrendPoint(
                point.CapturedAtUtc, point.DatabaseTotalBytes,
                point.ClassBytes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<StorageDataClass>> ListDataClassesAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.dataClasses", (_, _) =>
        {
            var classes = StorageDataClassCatalog.ToDataClassDtos()
                .Select(item => new StorageDataClass(item.TargetId, item.DisplayName, item.Description,
                    item.SafetyLevelName, item.CatalogVersion, item.ManualCleanupAllowed,
                    item.AutomaticCleanupAllowed, item.Protected, item.ProtectionReason ?? "",
                    item.DefaultRetentionDays, item.MinRetentionDays))
                .ToArray();
            return Task.FromResult((IReadOnlyList<StorageDataClass>)classes);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ListProtectedObjectsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.protectedObjects", (_, _) =>
            Task.FromResult((IReadOnlyList<string>)StorageDataClassCatalog.ProtectedObjects.ToArray()), cancellationToken);

    public Task<StorageRefreshStatus> RequestRefreshAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.refresh", async (scope, token) =>
        {
            var status = await scope.Services.GetRequiredService<StorageInventorySampler>()
                .RequestRefreshAsync(token);
            return new StorageRefreshStatus(status.RefreshId, status.State.ToString(), status.RequestedAtUtc,
                status.CompletedAtUtc, status.SnapshotRevision);
        }, cancellationToken);

    public Task<StorageRetentionPolicy> ReadPolicyAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.policy.read", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<StorageRetentionPolicyService>();
            var policy = await service.GetEffectivePolicyAsync(token);
            return Map(service.ToDto(policy));
        }, cancellationToken);

    public Task SavePolicyAsync(StorageRetentionPolicyUpdate update, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("storage.policy.save", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<StorageRetentionPolicyService>();
            // Core owns the conflict answer: a stale revision must surface as a conflict, never an overwrite.
            try
            {
                await service.UpdateAsync(new StorageRetentionPolicyUpdateRequest
            {
                    ExpectedRevision = update.ExpectedRevision,
                    AutomaticCleanupEnabled = update.AutomaticCleanupEnabled,
                    Targets = [.. update.Targets.Select(target => new StorageRetentionPolicyTargetUpdateDto
                    {
                        TargetId = target.TargetId,
                        Enabled = target.Enabled,
                        RetentionDays = target.RetentionDays,
                    })],
                }, token);
            }
            catch (StorageMaintenanceCoordinator.StorageAdminException exception)
                when (exception.ErrorCode == StorageAdminErrorCodes.PolicyConflict)
            {
                throw new SettingsConflictException(exception.Message);
            }
            catch (StorageMaintenanceCoordinator.StorageAdminException exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }
            return true;
        }, cancellationToken);

    private static StorageSnapshot Map(StorageInventorySnapshotDto snapshot) => new(
        snapshot.SnapshotId, snapshot.Revision, snapshot.SchemaVersion, snapshot.CapturedAtUtc,
        snapshot.UpdatedAtUtc, snapshot.IsRefreshing, snapshot.Warnings ?? [],
        snapshot.Databases.Select(database => new StorageDatabase(database.DatabaseId, database.DisplayName,
            database.RelativePath, database.TotalBytes, database.MainBytes, database.WalBytes,
            database.SharedMemoryBytes, database.ReusableFreeBytes)).ToArray(),
        snapshot.Classes.Select(item => new StorageInventoryClass(item.TargetId, item.DisplayName,
            item.EstimatedBytes, item.EstimatedRows, item.OldestUtc, item.NewestUtc,
            item.EstimateState.ToString(), item.UpdatedAtUtc)).ToArray());

    private static StorageRetentionPolicy Map(StorageRetentionPolicyDto policy) => new(
        policy.PolicyRevision, policy.AutomaticCleanupEnabled, policy.RunIntervalHours, policy.StartupDelaySeconds,
        policy.LastCompletedAtUtc, policy.NextRunEstimateUtc,
        policy.Targets.Select(target => new StorageRetentionTarget(target.TargetId, target.DisplayName,
            target.Enabled, target.RetentionDays, target.AutomaticCleanupAllowed, target.DefaultRetentionDays,
            target.MinRetentionDays, target.MaxRetentionDays)).ToArray(),
        policy.Warnings ?? []);
}
