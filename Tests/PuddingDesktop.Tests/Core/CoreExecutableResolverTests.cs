using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Core;

public sealed class CoreExecutableResolverTests
{
    [Fact]
    public void Resolve_ConfiguredPathWinsOverBundledAndDevelopmentOutputs()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configured = CreateExecutable(Path.Combine(root, "configured"));
            var desktopDirectory = Path.Combine(root, "desktop");
            CreateExecutable(Path.Combine(desktopDirectory, "core"));
            CreateExecutable(desktopDirectory);

            var actual = CoreExecutableResolver.Resolve(configured, desktopDirectory);

            Assert.Equal(Path.GetFullPath(configured), actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_BundledCoreWinsOverDevelopmentOutput()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var desktopDirectory = Path.Combine(root, "desktop");
            var bundled = CreateExecutable(Path.Combine(desktopDirectory, "core"));
            CreateExecutable(desktopDirectory);

            var actual = CoreExecutableResolver.Resolve(null, desktopDirectory);

            Assert.Equal(bundled, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DevelopmentOutputUsesCurrentDesktopBuild()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var desktopDirectory = Path.Combine(
                root,
                "Source",
                "PuddingDesktop",
                "bin",
                "Debug",
                "net10.0-windows10.0.17763.0");
            var currentBuild = CreateExecutable(desktopDirectory);

            var legacyRelease = CreateExecutable(Path.Combine(
                root,
                "Source",
                "PuddingAgent",
                "bin",
                "Release",
                "net10.0"));
            File.SetLastWriteTimeUtc(legacyRelease, DateTime.UtcNow.AddMinutes(1));

            var actual = CoreExecutableResolver.Resolve(null, desktopDirectory);

            Assert.Equal(currentBuild, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "desktop-core-resolver-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateExecutable(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PuddingAgent.exe");
        File.WriteAllText(path, "test");
        return Path.GetFullPath(path);
    }
}
