namespace PuddingDesktop.Storage;

public interface IStorageAnalysisService
{
    Task<StorageSnapshot> AnalyzeAsync(
        string dataRoot,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken);
}
