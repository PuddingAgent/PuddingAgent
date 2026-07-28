using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class YoloSignalServiceTests
{
    [TestMethod]
    public void ResolveSignalPath_UsesConfiguredRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "pudding-yolo-root");

        var result = YoloSignalService.ResolveSignalPath(repositoryRoot, AppContext.BaseDirectory);

        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(repositoryRoot), "yolo.signal"),
            result);
    }

    [TestMethod]
    public void ResolveSignalPath_WalksUpToRepositoryMarker()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "dev-up.py"), string.Empty);
        var nested = Path.Combine(temp.Path, "Source", "PuddingAgent", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(nested);

        var result = YoloSignalService.ResolveSignalPath(repositoryRoot: null, nested);

        Assert.AreEqual(Path.Combine(temp.Path, "yolo.signal"), result);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-yolo-signal-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
