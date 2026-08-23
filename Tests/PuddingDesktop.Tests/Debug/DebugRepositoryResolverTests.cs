using PuddingDesktop.Configuration;
using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

public sealed class DebugRepositoryResolverTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"pudding-debug-resolver-{Guid.NewGuid():N}");

    private string CreateRepository()
    {
        var repositoryRoot = Path.Combine(_tempRoot, "repo");
        var backendProject = Path.Combine(
            repositoryRoot, DebugRepositoryResolver.BackendProjectRelativePath);
        var frontendPackage = Path.Combine(
            repositoryRoot,
            DebugRepositoryResolver.FrontendDirectoryRelativePath,
            "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backendProject)!);
        File.WriteAllText(backendProject, "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(frontendPackage)!);
        File.WriteAllText(frontendPackage, "{}");
        return repositoryRoot;
    }

    [Fact]
    public void ResolveRepositoryRoot_WalksUpFromNestedDirectory()
    {
        var repositoryRoot = CreateRepository();
        var nested = Path.Combine(repositoryRoot, "Source", "PuddingDesktop", "bin", "Debug");
        Directory.CreateDirectory(nested);

        var resolved = DebugRepositoryResolver.ResolveRepositoryRoot(null, nested);

        Assert.Equal(Path.GetFullPath(repositoryRoot), resolved);
    }

    [Fact]
    public void ResolveRepositoryRoot_PrefersConfiguredRoot()
    {
        var repositoryRoot = CreateRepository();
        var otherRoot = CreateRepository();

        var resolved = DebugRepositoryResolver.ResolveRepositoryRoot(otherRoot, repositoryRoot);

        Assert.Equal(Path.GetFullPath(otherRoot), resolved);
    }

    [Fact]
    public void ResolveRepositoryRoot_ThrowsWhenConfiguredRootMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            DebugRepositoryResolver.ResolveRepositoryRoot(
                Path.Combine(_tempRoot, "missing"),
                _tempRoot));
    }

    [Fact]
    public void ResolveRepositoryRoot_ThrowsWhenNoRepositoryAboveStart()
    {
        Directory.CreateDirectory(_tempRoot);

        Assert.Throws<DirectoryNotFoundException>(() =>
            DebugRepositoryResolver.ResolveRepositoryRoot(null, _tempRoot));
    }

    [Fact]
    public void ResolveFrontendWorkingDirectory_DerivesFromRepositoryRoot()
    {
        var repositoryRoot = CreateRepository();
        var settings = new DesktopDebugSettings { RepositoryRoot = repositoryRoot };

        var frontendDirectory = DebugRepositoryResolver.ResolveFrontendWorkingDirectory(settings);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, DebugRepositoryResolver.FrontendDirectoryRelativePath)),
            frontendDirectory);
    }

    [Fact]
    public void ResolveFrontendWorkingDirectory_HonorsExplicitOverride()
    {
        var repositoryRoot = CreateRepository();
        var overrideDirectory = Path.Combine(_tempRoot, "fe-override");
        Directory.CreateDirectory(overrideDirectory);
        var settings = new DesktopDebugSettings
        {
            RepositoryRoot = repositoryRoot,
            FrontendWorkingDirectory = overrideDirectory,
        };

        var frontendDirectory = DebugRepositoryResolver.ResolveFrontendWorkingDirectory(settings);

        Assert.Equal(Path.GetFullPath(overrideDirectory), frontendDirectory);
    }

    [Fact]
    public void ResolveBackendProjectPath_DerivesFromRepositoryRoot()
    {
        var repositoryRoot = CreateRepository();
        var settings = new DesktopDebugSettings { RepositoryRoot = repositoryRoot };

        var backendProject = DebugRepositoryResolver.ResolveBackendProjectPath(settings);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, DebugRepositoryResolver.BackendProjectRelativePath)),
            backendProject);
    }

    [Fact]
    public void ResolveBackendProjectPath_HonorsExplicitOverride()
    {
        var repositoryRoot = CreateRepository();
        var overrideProject = Path.Combine(_tempRoot, "Override.csproj");
        File.WriteAllText(overrideProject, "<Project />");
        var settings = new DesktopDebugSettings
        {
            RepositoryRoot = repositoryRoot,
            BackendProjectPath = overrideProject,
        };

        var backendProject = DebugRepositoryResolver.ResolveBackendProjectPath(settings);

        Assert.Equal(Path.GetFullPath(overrideProject), backendProject);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
