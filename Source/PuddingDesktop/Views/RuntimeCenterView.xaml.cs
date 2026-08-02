using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PuddingDesktop.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PuddingDesktop.Views;

public partial class RuntimeCenterView : UserControl
{
    private readonly DispatcherTimer _refreshTimer;

    public RuntimeCenterView()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is RuntimeCenterViewModel viewModel)
                viewModel.RefreshTransient();
        }, Dispatcher);
        Loaded += (_, _) => _refreshTimer.Start();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private RuntimeCenterViewModel? ViewModel => DataContext as RuntimeCenterViewModel;

    private async void Start_Click(object sender, RoutedEventArgs e)
        => await RunAsync(viewModel => viewModel.StartAsync(CancellationToken.None), "Core 启动请求已完成。");

    private async void Stop_Click(object sender, RoutedEventArgs e)
        => await RunAsync(viewModel => viewModel.StopAsync(CancellationToken.None), "Core 已停止。");

    private async void Restart_Click(object sender, RoutedEventArgs e)
        => await RunAsync(viewModel => viewModel.RestartAsync(CancellationToken.None), "Core 重启请求已完成。");

    private async void AutoRestart_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        var enabled = AutoRestartCheck.IsChecked == true;
        await RunAsync(
            viewModel => viewModel.SetAutoRestartAsync(enabled, CancellationToken.None),
            enabled ? "异常退出自动恢复已启用。" : "异常退出自动恢复已关闭。");
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        Clipboard.SetText(ViewModel.CoreLogText);
        ShowFeedback("最近 Core 输出已复制到剪贴板。", isError: false);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        try
        {
            var path = ViewModel.GetLogDirectory();
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ShowFeedback("已打开日志目录。", isError: false);
        }
        catch (Exception ex)
        {
            ShowFeedback($"无法打开日志目录：{ex.Message}", isError: true);
        }
    }

    private async void CreateDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        try
        {
            var filePath = await ViewModel.CreateDiagnosticBundleAsync(CancellationToken.None);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true,
            });
            ShowFeedback($"诊断包已生成：{filePath}", isError: false);
        }
        catch (Exception ex)
        {
            ShowFeedback($"生成诊断包失败：{ex.Message}", isError: true);
        }
    }

    private async Task RunAsync(
        Func<RuntimeCenterViewModel, Task> operation,
        string successMessage)
    {
        if (ViewModel is null)
            return;
        try
        {
            await operation(ViewModel);
            ViewModel.RefreshTransient();
            ShowFeedback(successMessage, isError: false);
        }
        catch (Exception ex)
        {
            ShowFeedback(ex.Message, isError: true);
        }
    }

    private void ShowFeedback(string message, bool isError)
    {
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "SuccessBrush");
        FeedbackText.Visibility = Visibility.Visible;
    }

    public void DisposeOperations() => _refreshTimer.Stop();
}
