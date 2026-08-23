using PuddingDesktop.Configuration;
using PuddingDesktop.Runtime;
using PuddingDesktop.ViewModels;

namespace PuddingDesktop.Tests.Debug;

public sealed class SettingsViewModelDebugSettingsTests
{
    [Fact]
    public async Task LoadAsync_ReadsDebugSectionFromBootstrapSettings()
    {
        var store = new StubBootstrapSettingsStore(new DesktopBootstrapSettings
        {
            Debug = new DesktopDebugSettings
            {
                Enabled = true,
                RepositoryRoot = "E:\\repo",
                FrontendPort = 8001,
                ProxyPort = 8081,
            },
        });
        var viewModel = CreateViewModel(store);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.DebugEnabled);
        Assert.Equal("E:\\repo", viewModel.DebugRepositoryRoot);
        Assert.Equal(8001, viewModel.DebugFrontendPort);
        Assert.Equal(8081, viewModel.DebugProxyPort);
    }

    [Fact]
    public async Task SaveBootstrapAsync_PersistsDebugSectionAndKeepsOverrides()
    {
        var store = new StubBootstrapSettingsStore(new DesktopBootstrapSettings
        {
            Debug = new DesktopDebugSettings { FrontendWorkingDirectory = "E:\\fe-override" },
        });
        var viewModel = CreateViewModel(store);
        viewModel.DebugEnabled = true;
        viewModel.DebugRepositoryRoot = "E:\\repo";
        viewModel.DebugFrontendPort = 8000;
        viewModel.DebugProxyPort = 80;
        viewModel.DebugFrontendStartupTimeoutSeconds = 240;
        viewModel.DebugBackendBuildTimeoutSeconds = 600;

        await viewModel.SaveBootstrapAsync(CancellationToken.None);

        Assert.False(viewModel.HasError);
        var saved = store.Saved;
        Assert.NotNull(saved);
        Assert.True(saved.Debug.Enabled);
        Assert.Equal("E:\\repo", saved.Debug.RepositoryRoot);
        Assert.Equal(8000, saved.Debug.FrontendPort);
        Assert.Equal(80, saved.Debug.ProxyPort);
        Assert.Equal(240, saved.Debug.FrontendStartupTimeoutSeconds);
        Assert.Equal(600, saved.Debug.BackendBuildTimeoutSeconds);
        // Manual overrides not exposed in the UI must survive the save.
        Assert.Equal("E:\\fe-override", saved.Debug.FrontendWorkingDirectory);
    }

    [Fact]
    public async Task SaveBootstrapAsync_SkipsSaveWhenDebugPortsConflict()
    {
        var store = new StubBootstrapSettingsStore(new DesktopBootstrapSettings());
        var viewModel = CreateViewModel(store);
        viewModel.DebugEnabled = true;
        viewModel.DebugProxyPort = 8000;
        viewModel.DebugFrontendPort = 8000;

        await viewModel.SaveBootstrapAsync(CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.Contains("代理端口", viewModel.ValidationError);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task SaveBootstrapAsync_RejectsProxyPortCollidingWithCorePort()
    {
        var store = new StubBootstrapSettingsStore(new DesktopBootstrapSettings());
        var viewModel = CreateViewModel(store);
        viewModel.DebugEnabled = true;
        viewModel.DebugFrontendPort = 8000;
        viewModel.DebugProxyPort = 8080;
        viewModel.Port = 8080;

        await viewModel.SaveBootstrapAsync(CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.Contains("Core 端口", viewModel.ValidationError);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task SaveBootstrapAsync_NoDebugValidationWhenDisabled()
    {
        var store = new StubBootstrapSettingsStore(new DesktopBootstrapSettings());
        var viewModel = CreateViewModel(store);
        viewModel.DebugEnabled = false;
        viewModel.DebugProxyPort = 8000;
        viewModel.DebugFrontendPort = 8000;

        await viewModel.SaveBootstrapAsync(CancellationToken.None);

        Assert.False(viewModel.HasError);
        Assert.NotNull(store.Saved);
        Assert.False(store.Saved.Debug.Enabled);
    }

    private static SettingsViewModel CreateViewModel(StubBootstrapSettingsStore store) => new(
        store,
        new SystemConfigurationService(),
        new StubControlTokenService(),
        new NoopAutoStartRegistrationService());

    private sealed class StubBootstrapSettingsStore : IDesktopBootstrapSettingsStore
    {
        private DesktopBootstrapSettings _current;

        public StubBootstrapSettingsStore(DesktopBootstrapSettings initial)
        {
            _current = initial;
        }

        public DesktopBootstrapSettings? Saved { get; private set; }

        public Task<DesktopBootstrapSettings> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_current);

        public Task SaveAsync(DesktopBootstrapSettings settings, CancellationToken cancellationToken)
        {
            _current = settings;
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StubControlTokenService : IDesktopControlTokenService
    {
        public Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken)
            => Task.FromResult("test-token");

        public Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken)
            => Task.FromResult("test-token");
    }

    private sealed class NoopAutoStartRegistrationService : AutoStartRegistrationService
    {
        public override bool IsEnabled() => false;

        public override void SetEnabled(bool enabled, string executablePath)
        {
        }
    }
}
