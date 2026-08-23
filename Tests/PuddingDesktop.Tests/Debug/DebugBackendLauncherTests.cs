using PuddingDesktop.Core;
using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

public sealed class DebugBackendLauncherTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"pudding-debug-launcher-{Guid.NewGuid():N}");

    [Fact]
    public void CreateBuildStartInfo_UsesDotnetBuildWithQuotedProject()
    {
        var projectPath = Path.Combine(_tempRoot, "Source", "PuddingAgent", "PuddingAgent.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

        var startInfo = DebugBackendLauncher.CreateBuildStartInfo(projectPath);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(
            $"build \"{Path.GetFullPath(projectPath)}\" -v minimal --nologo",
            startInfo.Arguments);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(projectPath)), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void ResolveOutputExecutable_PrefersPrimaryFrameworkOutput()
    {
        var projectPath = CreateProjectWithOutput("net10.0");

        var executablePath = DebugBackendLauncher.ResolveOutputExecutable(projectPath);

        Assert.EndsWith(
            Path.Combine("bin", "Debug", "net10.0", "PuddingAgent.exe"),
            executablePath);
    }

    [Fact]
    public void ResolveOutputExecutable_FallsBackToAnyFrameworkOutput()
    {
        var projectPath = CreateProjectWithOutput("net11.0");

        var executablePath = DebugBackendLauncher.ResolveOutputExecutable(projectPath);

        Assert.EndsWith(
            Path.Combine("bin", "Debug", "net11.0", "PuddingAgent.exe"),
            executablePath);
    }

    [Fact]
    public void ResolveOutputExecutable_ThrowsWhenBuildProducedNothing()
    {
        var projectPath = Path.Combine(_tempRoot, "Empty", "PuddingAgent.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

        Assert.Throws<FileNotFoundException>(() =>
            DebugBackendLauncher.ResolveOutputExecutable(projectPath));
    }

    [Fact]
    public async Task BuildAsync_ThrowsOnBuildFailureAndIncludesLogTail()
    {
        var projectPath = Path.Combine(_tempRoot, "Failing", "PuddingAgent.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var logBuffer = new CoreProcessLogBuffer();

        // dotnet build of an empty/invalid project file fails fast and exercises
        // the non-zero exit path without depending on pnpm or the frontend.
        File.WriteAllText(projectPath, "<Project Sdk=\"This.Sdk.Does.Not.Exist\" />");

        var launcher = new DebugBackendLauncher();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.BuildAsync(
                projectPath,
                TimeSpan.FromSeconds(120),
                logBuffer,
                CancellationToken.None));

        Assert.Contains("exit code", ex.Message);
    }

    private string CreateProjectWithOutput(string framework)
    {
        var projectPath = Path.Combine(_tempRoot, framework, "PuddingAgent.csproj");
        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(projectPath)!, "bin", "Debug", framework);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(outputDirectory, "PuddingAgent.exe"), "stub");
        return projectPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
