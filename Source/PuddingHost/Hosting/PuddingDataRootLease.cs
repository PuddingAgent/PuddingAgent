namespace PuddingHost.Hosting;

/// <summary>Shared entry-point lease for Desktop DLL, Console development and legacy child hosts.</summary>
public sealed class PuddingDataRootLease : IDisposable
{
    private readonly FileStream _file;
    public PuddingDataRootLease(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _file = new FileStream(Path.Combine(dataRoot, ".pudding-host.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => _file.Dispose();
}
