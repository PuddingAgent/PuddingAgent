using System.Text.Json;
using PuddingCode.Configuration;
using PuddingDesktop.Bootstrap;
using PuddingDesktop.Configuration;
using PuddingDesktop.Debug;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Bootstrap;

/// <summary>
/// Frontend-step orchestration tests for the rebuild-restart loop. All
/// external effects (Core stop/start, dotnet build, frontend deploy) are
/// injected via DesktopBootstrapTestHooks, so no test ever launches a real
/// process, triggers a real reboot, or touches the real wwwroot.
/// </summary>
public sealed class DesktopBootstrapFrontendStepTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly List<string> _order = [];
    private readonly List<(string Mode, string? ArtifactDirectory)> _frontendCalls = [];
    private readonly string _buildOutputDirectory;
    private int _startCount;
    private int _stopCount;
    private int _buildCount;
    private string _coreExecutablePath;

    public DesktopBootstrapFrontendStepTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("pudding-bootstrap-frontend-test").FullName;
        _buildOutputDirectory = Path.Combine(_tempRoot, "build-output");
        Directory.CreateDirectory(_buildOutputDirectory);
        File.WriteAllBytes(Path.Combine(_buildOutputDirectory, "PuddingAgent.dll"), [1, 2, 3]);
        _coreExecutablePath = Path.Combine(_tempRoot, "deployment", "PuddingAgent.exe");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task FrontendSkip_ByDefault_NeverRunsTheFrontendStep()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test", yolo: false, ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("skip", result.Frontend.Mode);
        Assert.False(result.Frontend.Ran);
        Assert.Empty(_frontendCalls);
        Assert.Equal(["stop", "wait", "build", "start"], _order);
    }

    [Fact]
    public async Task FrontendBuild_Success_RunsBeforeStopAndBuild_AndCompletesRestart()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "desktop-build",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "build",
            frontendArtifactDirectory: null,
            frontendArtifactIndexSha256: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Frontend.Ran);
        Assert.True(result.Frontend.BuiltFromSource);
        Assert.Equal(3, result.Frontend.CopiedFileCount);
        Assert.Equal("abc123", result.Frontend.IndexSha256);
        Assert.False(result.Frontend.Failed);
        Assert.Equal(["frontend:build", "stop", "wait", "build", "start"], _order);
        Assert.True(result.CoreRestarted);
    }

    [Fact]
    public async Task FrontendBuild_Failure_AbortsFailClosed_BeforeStopBuildAndRestart()
    {
        var service = CreateService(frontendException: new InvalidOperationException(
            "pnpm failed with exit code 1. Last output: [chat-bundle-budget] over budget"));

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "desktop-build",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "build",
            frontendArtifactDirectory: null,
            frontendArtifactIndexSha256: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Frontend.Failed);
        Assert.True(result.Frontend.Ran);
        Assert.Contains("pnpm failed", result.Frontend.Error);
        Assert.Contains("前端步骤失败", result.Errors[0]);
        Assert.Contains("已中止本次重启", result.Errors[0]);
        Assert.Equal(0, _stopCount);
        Assert.Equal(0, _buildCount);
        Assert.Equal(0, _startCount);
        Assert.DoesNotContain("stop", _order);
        Assert.DoesNotContain("wait", _order);
        Assert.DoesNotContain("build", _order);
        Assert.DoesNotContain("start", _order);
    }

    [Fact]
    public async Task FrontendLoad_WithoutArtifactDirectory_FailsClosed_WithoutCallingFrontendOrBuild()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "desktop-build",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "load",
            frontendArtifactDirectory: null,
            frontendArtifactIndexSha256: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("load", result.Frontend.Mode);
        Assert.True(result.Frontend.Failed);
        Assert.Contains("frontendArtifactDirectory", result.Frontend.Error);
        Assert.Empty(_frontendCalls);
        Assert.Equal(0, _stopCount);
        Assert.Equal(0, _buildCount);
        Assert.Equal(0, _startCount);
        Assert.Empty(_order);
    }

    [Fact]
    public async Task FrontendLoad_WithArtifactDirectory_RunsBeforeBuild()
    {
        var artifactDirectory = Path.Combine(_tempRoot, "dist");
        Directory.CreateDirectory(artifactDirectory);
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "desktop-build",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "load",
            frontendArtifactDirectory: artifactDirectory,
            frontendArtifactIndexSha256: "cafe01",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Frontend.Ran);
        Assert.False(result.Frontend.BuiltFromSource);
        Assert.Single(_frontendCalls);
        Assert.Equal(("load", artifactDirectory), _frontendCalls[0]);
        Assert.True(_order.IndexOf("frontend:load") < _order.IndexOf("build"));
    }

    [Fact]
    public async Task RestartOnly_IgnoresRequestedFrontendStep()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "restart-only",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "build",
            frontendArtifactDirectory: null,
            frontendArtifactIndexSha256: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("build", result.Frontend.Mode);
        Assert.False(result.Frontend.Ran);
        Assert.Empty(_frontendCalls);
        Assert.Equal(["stop", "wait", "start"], _order);
    }

    [Fact]
    public async Task UnsupportedFrontendMode_FailsClosed_InTheOrchestrator()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test",
            yolo: false,
            deploymentMode: "desktop-build",
            artifactDirectory: null,
            artifactAssemblySha256: null,
            frontendMode: "hot-swap",
            frontendArtifactDirectory: null,
            frontendArtifactIndexSha256: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hot-swap", result.Frontend.Mode);
        Assert.True(result.Frontend.Failed);
        Assert.Empty(_order);
    }

    [Fact]
    public async Task ResultJson_AlwaysCarriesFrontendFields_EvenForSkip()
    {
        var service = CreateService();

        var result = await service.TriggerRebuildRestartAsync(
            "test", yolo: false, ct: CancellationToken.None);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"frontend\"", json);
        Assert.Contains("\"mode\":\"skip\"", json);
        Assert.Contains("\"ran\":false", json);
    }

    [Fact]
    public void ResultRecord_DefaultsCarrySkipFrontend()
    {
        var json = JsonSerializer.Serialize(new DesktopBootstrapResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"frontend\"", json);
        Assert.Contains("\"mode\":\"skip\"", json);
    }

    [Theory]
    [InlineData(null, "skip")]
    [InlineData("", "skip")]
    [InlineData("SKIP", "skip")]
    [InlineData("build", "build")]
    [InlineData(" load ", "load")]
    public void NormalizeFrontendMode_SupportedAliases_ReturnCanonical(string? value, string expected)
        => Assert.Equal(expected, DesktopBootstrapSignalParser.NormalizeFrontendMode(value));

    [Fact]
    public void NormalizeFrontendMode_Unknown_ReturnsNull()
        => Assert.Null(DesktopBootstrapSignalParser.NormalizeFrontendMode("hot-swap"));

    private DesktopBootstrapSignalService CreateService(
        FrontendDeployResult? frontendResult = null,
        Exception? frontendException = null)
    {
        var deploymentDirectory = Path.GetDirectoryName(_coreExecutablePath)!;

        var hooks = new DesktopBootstrapTestHooks
        {
            StopCoreAsync = _ =>
            {
                Interlocked.Increment(ref _stopCount);
                _order.Add("stop");
                return Task.CompletedTask;
            },
            WaitForCoreFullyStoppedAsync = _ =>
            {
                _order.Add("wait");
                return Task.FromResult(true);
            },
            BuildAsync = _ =>
            {
                Interlocked.Increment(ref _buildCount);
                _order.Add("build");
                return Task.FromResult(new DesktopBootstrapSignalService.BuildRunResult(
                    0,
                    [],
                    $"PuddingAgent -> {Path.Combine(_buildOutputDirectory, "PuddingAgent.dll")}"));
            },
            StartCoreAsync = _ =>
            {
                Interlocked.Increment(ref _startCount);
                _order.Add("start");
                return Task.CompletedTask;
            },
            RuntimeState = () => _order.Contains("start")
                ? DesktopRuntimeState.Ready
                : DesktopRuntimeState.Stopped,
            CoreExecutablePath = () => _coreExecutablePath,
            FrontendDeployAsync = (mode, artifactDirectory, _, _) =>
            {
                _order.Add($"frontend:{mode}");
                _frontendCalls.Add((mode, artifactDirectory));
                if (frontendException is not null)
                    return Task.FromException<FrontendDeployResult>(frontendException);

                // Simulate the real deploy effect: the entry file lands in the
                // Core directory's wwwroot\admin so the post-deployment gate
                // (SPA fallback registration precondition) passes.
                Directory.CreateDirectory(Path.Combine(deploymentDirectory, "wwwroot", "admin"));
                File.WriteAllText(
                    Path.Combine(deploymentDirectory, "wwwroot", "admin", "index.html"),
                    "<html>fake-admin</html>");

                return Task.FromResult(frontendResult ?? new FrontendDeployResult
                {
                    TargetAdminDirectory = Path.Combine(deploymentDirectory, "wwwroot", "admin"),
                    CopiedFileCount = 3,
                    RanInstall = false,
                    BuiltFromSource = mode == "build",
                    IndexSha256 = "abc123",
                });
            },
        };

        return new DesktopBootstrapSignalService(
            hooks,
            _tempRoot,
            new PuddingDesktopBootstrapConfig(),
            new StubControlTokenService());
    }

    private sealed class StubControlTokenService : IDesktopControlTokenService
    {
        public Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken)
            => Task.FromResult("test-token");

        public Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken)
            => Task.FromResult("test-token");
    }
}
