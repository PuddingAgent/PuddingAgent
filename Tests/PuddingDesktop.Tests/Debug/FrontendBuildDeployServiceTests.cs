using System.Text;
using System.Security.Cryptography;
using PuddingDesktop.Core;
using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

public sealed class FrontendBuildDeployServiceTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"pudding-frontend-deploy-{Guid.NewGuid():N}");

    [Fact]
    public void CreateBuildStartInfo_RunsPnpmBuildInFrontendDirectory()
    {
        var workingDirectory = Path.Combine(_tempRoot, "Source", "PuddingPlatformAdmin");

        var startInfo = FrontendBuildDeployService.CreateBuildStartInfo(workingDirectory);

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal("/c pnpm run build", startInfo.Arguments);
        Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);
    }

    [Fact]
    public void DeployDistFiles_ReplacesStaleFilesAndCopiesNested()
    {
        var dist = CreateDist(indexContent: "new index", extraFiles:
        [
            Path.Combine("static", "umi.abc123.js"),
            Path.Combine("static", "css", "app.def456.css"),
        ]);
        var wwwroot = Path.Combine(_tempRoot, "core", "wwwroot");
        var adminDirectory = Path.Combine(wwwroot, "admin");
        Directory.CreateDirectory(Path.Combine(adminDirectory, "static"));
        var siblingAsset = Path.Combine(wwwroot, "assets", "agent.png");
        Directory.CreateDirectory(Path.GetDirectoryName(siblingAsset)!);
        File.WriteAllText(Path.Combine(adminDirectory, "umi.old.js"), "stale bundle");
        File.WriteAllText(Path.Combine(adminDirectory, "static", "umi.old.js"), "stale bundle");
        File.WriteAllText(siblingAsset, "sibling content");

        var copiedFileCount = FrontendBuildDeployService.DeployDistFiles(dist, adminDirectory);

        Assert.Equal(3, copiedFileCount);
        Assert.Equal("new index", File.ReadAllText(Path.Combine(adminDirectory, "index.html")));
        Assert.True(File.Exists(Path.Combine(adminDirectory, "static", "umi.abc123.js")));
        Assert.True(File.Exists(Path.Combine(adminDirectory, "static", "css", "app.def456.css")));
        Assert.False(File.Exists(Path.Combine(adminDirectory, "umi.old.js")));
        Assert.False(File.Exists(Path.Combine(adminDirectory, "static", "umi.old.js")));
        // Only the admin subtree is replaced; sibling wwwroot content survives.
        Assert.True(File.Exists(Path.Combine(wwwroot, "assets", "agent.png")));
    }

    [Fact]
    public void DeployDistFiles_CreatesTargetWhenMissing()
    {
        var dist = CreateDist(indexContent: "fresh");
        var adminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin");

        var copiedFileCount = FrontendBuildDeployService.DeployDistFiles(dist, adminDirectory);

        Assert.Equal(1, copiedFileCount);
        Assert.True(File.Exists(Path.Combine(adminDirectory, "index.html")));
    }

    [Fact]
    public void DeployDistFiles_ThrowsWhenDistIndexMissing()
    {
        var dist = Path.Combine(_tempRoot, "dist-empty");
        Directory.CreateDirectory(dist);
        var adminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin");

        Assert.Throws<FileNotFoundException>(() =>
            FrontendBuildDeployService.DeployDistFiles(dist, adminDirectory));
    }

    [Fact]
    public void DeployDistFiles_RejectsTargetOutsideWwwrootAdmin()
    {
        var dist = CreateDist(indexContent: "fresh");
        var wrongTarget = Path.Combine(_tempRoot, "core", "wwwroot");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FrontendBuildDeployService.DeployDistFiles(dist, wrongTarget));

        Assert.Contains("wwwroot", ex.Message);
        // The rejected target must not have been deleted or created.
        Assert.False(Directory.Exists(wrongTarget));
    }

    [Fact]
    public void DeployDistFiles_RejectsOverlappingSourceAndTargetBeforeDelete()
    {
        var adminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin");
        Directory.CreateDirectory(adminDirectory);
        File.WriteAllText(Path.Combine(adminDirectory, "index.html"), "loaded artifact");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FrontendBuildDeployService.DeployDistFiles(adminDirectory, adminDirectory));

        Assert.Contains("overlapping", ex.Message);
        Assert.Equal("loaded artifact", File.ReadAllText(Path.Combine(adminDirectory, "index.html")));
    }

    [Fact]
    public void DeployPrebuiltArtifacts_VerifiesIdentityAndReportsPrebuiltMode()
    {
        var dist = CreateDist(
            indexContent: "prebuilt index",
            extraFiles: [Path.Combine("static", "app.hash.js")]);
        var adminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin");
        var expectedSha256 = ComputeSha256(Path.Combine(dist, "index.html"));

        var result = new FrontendBuildDeployService().DeployPrebuiltArtifacts(
            dist,
            adminDirectory,
            expectedSha256,
            new CoreProcessLogBuffer());

        Assert.Equal(2, result.CopiedFileCount);
        Assert.False(result.BuiltFromSource);
        Assert.False(result.RanInstall);
        Assert.Equal(expectedSha256, result.IndexSha256);
        Assert.Equal("prebuilt index", File.ReadAllText(Path.Combine(adminDirectory, "index.html")));
    }

    [Fact]
    public void DeployPrebuiltArtifacts_RejectsHashMismatchBeforeReplacingTarget()
    {
        var dist = CreateDist(indexContent: "wrong artifact");
        var adminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin");
        Directory.CreateDirectory(adminDirectory);
        File.WriteAllText(Path.Combine(adminDirectory, "index.html"), "currently loaded");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FrontendBuildDeployService().DeployPrebuiltArtifacts(
                dist,
                adminDirectory,
                new string('0', 64),
                new CoreProcessLogBuffer()));

        Assert.Contains("SHA-256 mismatch", ex.Message);
        Assert.Equal("currently loaded", File.ReadAllText(Path.Combine(adminDirectory, "index.html")));
    }

    [Fact]
    public async Task DeployAsync_ThrowsWhenBuildFailsAndIncludesLogTail()
    {
        // `pnpm run build` inside a directory without package.json exits
        // non-zero quickly; a missing pnpm surfaces the same way through cmd
        // ('pnpm' is not recognized). Either path exercises the failure branch.
        var frontendDirectory = Path.Combine(_tempRoot, "FrontendEmpty");
        Directory.CreateDirectory(Path.Combine(frontendDirectory, "node_modules"));

        var service = new FrontendBuildDeployService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeployAsync(
                new FrontendDeployOptions
                {
                    FrontendWorkingDirectory = frontendDirectory,
                    TargetAdminDirectory = Path.Combine(_tempRoot, "core", "wwwroot", "admin"),
                    BuildTimeout = TimeSpan.FromMinutes(2),
                },
                new CoreProcessLogBuffer(),
                CancellationToken.None));

        Assert.Contains("exit code", ex.Message);
    }

    private string CreateDist(string indexContent, params string[] extraFiles)
    {
        var dist = Path.Combine(_tempRoot, $"dist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "index.html"), indexContent);
        foreach (var relativePath in extraFiles)
        {
            var fullPath = Path.Combine(dist, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "artifact");
        }

        return dist;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
