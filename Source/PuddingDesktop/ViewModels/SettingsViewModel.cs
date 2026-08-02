using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingDesktop.Configuration;
using PuddingCode.Configuration;

namespace PuddingDesktop.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBootstrapSettingsStore _bootstrapStore;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly IDesktopControlTokenService _tokenService;

    private string _dataRoot = "D:\\data";
    private string _coreExecutablePath = "";
    private int _port;
    private bool _autoStart = true;
    private int _startupTimeoutSeconds = 60;
    private int _shutdownTimeoutSeconds = 15;
    private bool _hasToken;
    private string? _validationError;

    public SettingsViewModel(
        IDesktopBootstrapSettingsStore bootstrapStore,
        ISystemConfigurationService systemConfigService,
        IDesktopControlTokenService tokenService)
    {
        _bootstrapStore = bootstrapStore;
        _systemConfigService = systemConfigService;
        _tokenService = tokenService;
    }

    public string DataRoot
    {
        get => _dataRoot;
        set { _dataRoot = value; OnPropertyChanged(); }
    }

    public string CoreExecutablePath
    {
        get => _coreExecutablePath;
        set { _coreExecutablePath = value; OnPropertyChanged(); }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set { _autoStart = value; OnPropertyChanged(); }
    }

    public int StartupTimeoutSeconds
    {
        get => _startupTimeoutSeconds;
        set { _startupTimeoutSeconds = value; OnPropertyChanged(); }
    }

    public int ShutdownTimeoutSeconds
    {
        get => _shutdownTimeoutSeconds;
        set { _shutdownTimeoutSeconds = value; OnPropertyChanged(); }
    }

    public bool HasToken
    {
        get => _hasToken;
        set { _hasToken = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenStatusText)); }
    }

    public string TokenStatusText => HasToken ? "已生成" : "未生成";

    public string? ValidationError
    {
        get => _validationError;
        set { _validationError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_validationError);

    public async Task LoadAsync(CancellationToken ct)
    {
        var bootstrap = await _bootstrapStore.LoadAsync(ct);
        DataRoot = bootstrap.DataRoot ?? "D:\\data";
        CoreExecutablePath = bootstrap.CoreExecutablePath ?? "";

        if (!string.IsNullOrEmpty(bootstrap.DataRoot))
        {
            var systemResult = await _systemConfigService.LoadAsync(bootstrap.DataRoot, ct);
            if (systemResult.Success && systemResult.Config is not null)
            {
                var core = systemResult.Config.Desktop.Core;
                Port = core.Port;
                AutoStart = core.AutoStart;
                StartupTimeoutSeconds = core.StartupTimeoutSeconds;
                ShutdownTimeoutSeconds = core.ShutdownTimeoutSeconds;
                HasToken = !string.IsNullOrEmpty(core.ControlToken);
            }
        }
    }

    public async Task SaveBootstrapAsync(CancellationToken ct)
    {
        var current = await _bootstrapStore.LoadAsync(ct);
        var settings = current with
        {
            SchemaVersion = 1,
            DataRoot = DataRoot.Trim(),
            CoreExecutablePath = string.IsNullOrWhiteSpace(CoreExecutablePath) ? null : CoreExecutablePath,
        };
        await _bootstrapStore.SaveAsync(settings, ct);
    }

    public async Task SaveCoreSettingsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            ValidationError = "请先设置数据目录";
            return;
        }

        if (Port is < 0 or > 65535)
        {
            ValidationError = "端口必须为 0 到 65535；0 表示动态端口";
            return;
        }

        if (StartupTimeoutSeconds is < 1 or > 600)
        {
            ValidationError = "启动超时必须为 1 到 600 秒";
            return;
        }

        if (ShutdownTimeoutSeconds is < 1 or > 120)
        {
            ValidationError = "停止超时必须为 1 到 120 秒";
            return;
        }

        // Patch: only update UI-mutable fields; ControlToken is preserved
        await _systemConfigService.UpdateDesktopCoreSettingsAsync(
            DataRoot,
            current => current with
            {
                AutoStart = AutoStart,
                Port = Port,
                StartupTimeoutSeconds = StartupTimeoutSeconds,
                ShutdownTimeoutSeconds = ShutdownTimeoutSeconds,
            },
            ct);

        ValidationError = null;
    }

    public async Task ValidateDataRootAsync()
    {
        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            ValidationError = "数据目录不能为空";
            return;
        }

        if (DataRoot.Length < 2 || DataRoot[1] != ':')
        {
            ValidationError = "请输入有效路径（如 D:\\data）";
            return;
        }

        var driveRoot = DataRoot[..2];
        if (!Directory.Exists(driveRoot))
        {
            ValidationError = $"驱动器 {driveRoot} 不存在";
            return;
        }

        ValidationError = null;
    }

    public async Task RegenerateTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            ValidationError = "请先设置数据目录";
            return;
        }

        await _tokenService.RegenerateAsync(DataRoot, ct);
        HasToken = true;
        ValidationError = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
