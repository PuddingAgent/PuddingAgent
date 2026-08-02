using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PuddingDesktop.Configuration;
using PuddingDesktop.Runtime;
using PuddingDesktop.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PuddingDesktop.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _viewModel;
    private bool _loaded;

    public SettingsView()
    {
        var store = new FileDesktopBootstrapSettingsStore();
        var systemConfig = new SystemConfigurationService();
        var tokenService = new DesktopControlTokenService(systemConfig);
        _viewModel = new SettingsViewModel(store, systemConfig, tokenService);
        DataContext = _viewModel;

        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        try
        {
            await _viewModel.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowFeedback($"读取设置失败：{ex.Message}", isError: true);
        }
    }

    private void BrowseDataRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 Pudding 数据目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
            _viewModel.DataRoot = dialog.FolderName;
    }

    private void BrowseCoreExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Pudding Core 可执行文件",
            Filter = "PuddingAgent.exe|PuddingAgent.exe|Windows 可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
            _viewModel.CoreExecutablePath = dialog.FileName;
    }

    private async void ValidateDataRoot_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ValidateDataRootAsync();
        ShowFeedback(
            _viewModel.HasError ? _viewModel.ValidationError! : "数据目录有效。",
            _viewModel.HasError);
    }

    private async void CreateDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_viewModel.DataRoot))
            {
                ShowFeedback("请先输入数据目录。", isError: true);
                return;
            }

            Directory.CreateDirectory(_viewModel.DataRoot);
            await _viewModel.ValidateDataRootAsync();
            ShowFeedback("数据目录已创建并通过验证。", isError: false);
        }
        catch (Exception ex)
        {
            ShowFeedback($"创建目录失败：{ex.Message}", isError: true);
        }
    }

    private async void SaveDataRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNumericSettings())
            return;

        try
        {
            await _viewModel.SaveBootstrapAsync(CancellationToken.None);
            await _viewModel.SaveCoreSettingsAsync(CancellationToken.None);
            if (!_viewModel.HasError && Window.GetWindow(this) is MainWindow mainWindow)
            {
                await mainWindow.ApplyDesktopRuntimeSettingsAsync(
                    _viewModel.AutoRestart,
                    _viewModel.MinimizeToTray
                        ? DesktopCloseBehavior.MinimizeToTray
                        : DesktopCloseBehavior.ExitAndStopCore);
            }
            ShowFeedback(
                _viewModel.HasError ? _viewModel.ValidationError! : "设置已保存；Core 参数将在下次启动或重启后生效。",
                _viewModel.HasError);
        }
        catch (Exception ex)
        {
            ShowFeedback($"保存设置失败：{ex.Message}", isError: true);
        }
    }

    private async void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RegenerateTokenAsync(CancellationToken.None);
            ShowFeedback("桌面控制令牌已重新生成。请重启 Core。", isError: false);
        }
        catch (Exception ex)
        {
            ShowFeedback($"重新生成令牌失败：{ex.Message}", isError: true);
        }
    }

    private async void RestartCore_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow mainWindow)
            return;

        ShowFeedback("正在重启 Core…", isError: false);
        await mainWindow.RestartCoreViaCoordinatorAsync();
        ShowFeedback("Core 重启请求已完成。", isError: false);
    }

    private bool TryReadNumericSettings()
    {
        if (!int.TryParse(PortBox.Text, out var port))
        {
            ShowFeedback("HTTP 端口必须是整数。", isError: true);
            return false;
        }

        if (!int.TryParse(StartupTimeoutBox.Text, out var startupTimeout))
        {
            ShowFeedback("启动超时必须是整数秒。", isError: true);
            return false;
        }

        if (!int.TryParse(ShutdownTimeoutBox.Text, out var shutdownTimeout))
        {
            ShowFeedback("停止超时必须是整数秒。", isError: true);
            return false;
        }

        if (!int.TryParse(RestartAttemptsBox.Text, out var restartAttempts))
        {
            ShowFeedback("自动恢复次数必须是整数。", isError: true);
            return false;
        }

        if (!int.TryParse(RestartWindowBox.Text, out var restartWindow))
        {
            ShowFeedback("恢复统计窗口必须是整数秒。", isError: true);
            return false;
        }

        if (!int.TryParse(RestartInitialDelayBox.Text, out var restartInitialDelay))
        {
            ShowFeedback("恢复初始延迟必须是整数秒。", isError: true);
            return false;
        }

        if (!int.TryParse(RestartMaxDelayBox.Text, out var restartMaxDelay))
        {
            ShowFeedback("恢复最大延迟必须是整数秒。", isError: true);
            return false;
        }

        _viewModel.Port = port;
        _viewModel.StartupTimeoutSeconds = startupTimeout;
        _viewModel.ShutdownTimeoutSeconds = shutdownTimeout;
        _viewModel.RestartMaxAttempts = restartAttempts;
        _viewModel.RestartWindowSeconds = restartWindow;
        _viewModel.RestartInitialDelaySeconds = restartInitialDelay;
        _viewModel.RestartMaxDelaySeconds = restartMaxDelay;
        return true;
    }

    private void ShowFeedback(string message, bool isError)
    {
        SaveFeedback.Text = message;
        SaveFeedback.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "SuccessBrush");
        SaveFeedback.Visibility = Visibility.Visible;
    }
}
