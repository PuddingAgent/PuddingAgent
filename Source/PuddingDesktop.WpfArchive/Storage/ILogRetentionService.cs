namespace PuddingDesktop.Storage;

public interface ILogRetentionService
{
    Task<LogCleanupPreview> PreviewAsync(
        string dataRoot,
        TimeSpan retention,
        CancellationToken cancellationToken);

    Task<LogCleanupResult> ExecuteAsync(
        LogCleanupPreview preview,
        IProgress<LogCleanupProgress>? progress,
        CancellationToken cancellationToken);
}
