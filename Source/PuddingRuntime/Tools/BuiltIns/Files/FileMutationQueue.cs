using System.Collections.Concurrent;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Per-file serialization queue to prevent concurrent mutations of the same file.
/// Usage: using (await queue.AcquireAsync(path, ct)) { /* mutate file */ }
/// </summary>
public sealed class FileMutationQueue
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string fullPath, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var semaphore = _locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new ReleaseToken(semaphore);
    }

    private sealed class ReleaseToken(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
