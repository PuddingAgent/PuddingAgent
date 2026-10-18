using PuddingDesktop.Bootstrap;

namespace PuddingDesktop.Tests.Bootstrap;

public sealed class DesktopBootstrapRepositoryRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pudding-bootstrap-root-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("dev-up.py")]
    [InlineData(".git")]
    public void ResolvesRepositoryAboveDeepWinUiArtifacts(string marker)
    {
        var repository = Path.Combine(_root, "repo");
        var executableDirectory = Path.Combine(repository, "temp", "build", "winui3", "bin",
            "PuddingDesktop", "Debug", "net10.0-windows10.0.19041.0", "win-x64");
        Directory.CreateDirectory(executableDirectory);
        File.WriteAllText(Path.Combine(repository, marker), "test marker");

        Assert.Equal(repository, DesktopBootstrapSignalParser.ResolveRepositoryRoot(null, executableDirectory));
    }

    [Fact]
    public void ExplicitRepositoryOverrideWins()
    {
        var repository = Path.Combine(_root, "configured");
        Assert.Equal(repository, DesktopBootstrapSignalParser.ResolveRepositoryRoot(repository, _root));
    }

    [Fact]
    public void PackagedDesktopWithoutRepositoryKeepsBaseDirectory()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(_root, DesktopBootstrapSignalParser.ResolveRepositoryRoot(null, _root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
