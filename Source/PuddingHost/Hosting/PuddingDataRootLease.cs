namespace PuddingHost.Hosting;

/// <summary>Exclusive DataRoot lease shared by Console and Desktop child process entry points.</summary>
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
