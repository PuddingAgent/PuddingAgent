using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

public sealed class FrontendDevSupervisorTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"pudding-frontend-supervisor-{Guid.NewGuid():N}");

    [Fact]
    public void CreateProcessStartInfo_RunsPnpmStartDevOnCmd()
    {
        var workingDirectory = Path.Combine(_tempRoot, "frontend");
        Directory.CreateDirectory(workingDirectory);

        var startInfo = FrontendDevSupervisor.CreateProcessStartInfo(workingDirectory, 8000);

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal("/c pnpm run start:dev -- --host 127.0.0.1 --port 8000", startInfo.Arguments);
        Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Theory]
    [InlineData(8001)]
    [InlineData(3000)]
    public void CreateProcessStartInfo_ForwardsFrontendPort(int port)
    {
        var workingDirectory = Path.Combine(_tempRoot, $"frontend-{port}");
        Directory.CreateDirectory(workingDirectory);

        var startInfo = FrontendDevSupervisor.CreateProcessStartInfo(workingDirectory, port);

        Assert.Equal($"/c pnpm run start:dev -- --host 127.0.0.1 --port {port}", startInfo.Arguments);
    }

    [Fact]
    public void CreateInstallStartInfo_RunsPnpmInstallOnCmd()
    {
        var workingDirectory = Path.Combine(_tempRoot, "frontend-install");
        Directory.CreateDirectory(workingDirectory);

        var startInfo = FrontendDevSupervisor.CreateInstallStartInfo(workingDirectory);

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal("/c pnpm install", startInfo.Arguments);
        Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task StartAsync_RejectsOutOfRangePort(int port)
    {
        await using var supervisor = new FrontendDevSupervisor();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            supervisor.StartAsync(
                new FrontendDevStartOptions { WorkingDirectory = _tempRoot, Port = port },
                CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
