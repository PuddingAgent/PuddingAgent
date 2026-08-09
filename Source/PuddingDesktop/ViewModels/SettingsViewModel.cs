using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingDesktop.Configuration;
using PuddingDesktop.Runtime;
using PuddingCode.Configuration;

namespace PuddingDesktop.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBootstrapSettingsStore _bootstrapStore;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly AutoStartRegistrationService _autoStartRegistrationService;

    private string _dataRoot = "D:\\data";
    private string _coreExecutablePath = "";
    private int _port = PuddingDesktopCoreConfig.DefaultPort;
    private bool _autoStart = true;
    private bool _autoRestart = true;
    private int _restartMaxAttempts = 3;
    private int _restartWindowSeconds = 60;
    private int _restartInitialDelaySeconds = 2;
    private int _restartMaxDelaySeconds = 30;
    private bool _minimizeToTray = true;
    private bool _startWithWindows;
    private int _startupTimeoutSeconds = 60;
    private int _shutdownTimeoutSeconds = 15;
    private bool _hasToken;
    private string? _validationError;

    public SettingsViewModel(
        IDesktopBootstrapSettingsStore bootstrapStore,
        ISystemConfigurationService systemConfigService,
        IDesktopControlTokenService tokenService,
        AutoStartRegistrationService? autoStartRegistrationService = null)
    {
        _bootstrapStore = bootstrapStore;
        _systemConfigService = systemConfigService;
        _tokenService = tokenService;
        _autoStartRegistrationService = autoStartRegistrationService ?? new AutoStartRegistrationService();
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

    public bool AutoRestart
    {
        get => _autoRestart;
        set { _autoRestart = value; OnPropertyChanged(); }
    }

    public int RestartMaxAttempts
    {
        get => _restartMaxAttempts;
        set { _restartMaxAttempts = value; OnPropertyChanged(); }
    }

    public int RestartWindowSeconds
    {
        get => _restartWindowSeconds;
        set { _restartWindowSeconds = value; OnPropertyChanged(); }
    }

    public int RestartInitialDelaySeconds
    {
        get => _restartInitialDelaySeconds;
        set { _restartInitialDelaySeconds = value; OnPropertyChanged(); }
    }

    public int RestartMaxDelaySeconds
    {
        get => _restartMaxDelaySeconds;
        set { _restartMaxDelaySeconds = value; OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { _minimizeToTray = value; OnPropertyChanged(); }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { _startWithWindows = value; OnPropertyChanged(); }
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
        MinimizeToTray = bootstrap.CloseBehavior == DesktopCloseBehavior.MinimizeToTray;
        StartWithWindows = bootstrap.StartWithWindows;

        if (!string.IsNullOrEmpty(bootstrap.DataRoot))
        {
            var systemResult = await _systemConfigService.LoadAsync(bootstrap.DataRoot, ct);
            if (systemResult.Success && systemResult.Config is not null)
            {
                var core = systemResult.Config.Desktop.Core;
                Port = core.Port;
                AutoStart = core.AutoStart;
                AutoRestart = core.AutoRestart;
                RestartMaxAttempts = core.RestartMaxAttempts;
                RestartWindowSeconds = core.RestartWindowSeconds;
                RestartInitialDelaySeconds = core.RestartInitialDelaySeconds;
                RestartMaxDelaySeconds = core.RestartMaxDelaySeconds;
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
            CloseBehavior = MinimizeToTray
                ? DesktopCloseBehavior.MinimizeToTray
                : DesktopCloseBehavior.ExitAndStopCore,
            StartWithWindows = StartWithWindows,
        };

        var previouslyRegistered = _autoStartRegistrationService.IsEnabled();
        try
        {
            _autoStartRegistrationService.SetEnabled(
                StartWithWindows,
                Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定 PuddingDesktop 可执行文件路径。"));
            await _bootstrapStore.SaveAsync(settings, ct);
        }
        catch
        {
            try
            {
                _autoStartRegistrationService.SetEnabled(
                    previouslyRegistered,
                    Environment.ProcessPath ?? string.Empty);
            }
            catch { }
            throw;
        }
    }

    public async Task SaveCoreSettingsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            ValidationError = "请先设置数据目录";
            return;
        }

        if (Port is < 1 or > 65535)
        {
            ValidationError = "固定监听端口必须为 1 到 65535";
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

        try
        {
            _ = new CoreRestartPolicy
            {
                Enabled = AutoRestart,
                MaxAttempts = RestartMaxAttempts,
                WindowSeconds = RestartWindowSeconds,
                InitialDelaySeconds = RestartInitialDelaySeconds,
                MaxDelaySeconds = RestartMaxDelaySeconds,
            }.Validate();
        }
        catch (Exception ex)
        {
            ValidationError = ex.Message;
            return;
        }

        // Patch: only update UI-mutable fields; ControlToken is preserved
        await _systemConfigService.UpdateDesktopCoreSettingsAsync(
            DataRoot,
            current => current with
            {
                AutoStart = AutoStart,
                AutoRestart = AutoRestart,
                RestartMaxAttempts = RestartMaxAttempts,
                RestartWindowSeconds = RestartWindowSeconds,
                RestartInitialDelaySeconds = RestartInitialDelaySeconds,
                RestartMaxDelaySeconds = RestartMaxDelaySeconds,
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
