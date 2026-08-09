using PuddingDesktop.Configuration;
using PuddingDesktop.ViewModels;

namespace PuddingDesktop.Tests.Configuration;

public sealed class SettingsViewModelPortTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task SaveCoreSettingsAsync_RejectsDynamicOrOutOfRangePort(int port)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        try
        {
            var viewModel = CreateViewModel();
            viewModel.DataRoot = tempDir;
            viewModel.Port = port;

            await viewModel.SaveCoreSettingsAsync(CancellationToken.None);

            Assert.True(viewModel.HasError);
            Assert.Contains("1 到 65535", viewModel.ValidationError);
            Assert.False(File.Exists(Path.Combine(tempDir, "config", "system.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(8080)]
    public async Task SaveCoreSettingsAsync_PersistsFixedPort(int port)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-test-{Guid.NewGuid():N}");
        try
        {
            var viewModel = CreateViewModel();
            viewModel.DataRoot = tempDir;
            viewModel.Port = port;

            await viewModel.SaveCoreSettingsAsync(CancellationToken.None);

            Assert.False(viewModel.HasError);
            var result = await new SystemConfigurationService()
                .LoadAsync(tempDir, CancellationToken.None);
            Assert.Equal(port, result.Config!.Desktop.Core.Port);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static SettingsViewModel CreateViewModel() => new(
        new StubBootstrapSettingsStore(),
        new SystemConfigurationService(),
        new StubControlTokenService());

    private sealed class StubBootstrapSettingsStore : IDesktopBootstrapSettingsStore
    {
        public Task<DesktopBootstrapSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopBootstrapSettings());

        public Task SaveAsync(
            DesktopBootstrapSettings settings,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubControlTokenService : IDesktopControlTokenService
    {
        public Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken) =>
            Task.FromResult("test-token");

        public Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken) =>
            Task.FromResult("test-token");
    }
}
