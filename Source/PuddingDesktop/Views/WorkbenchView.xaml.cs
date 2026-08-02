using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PuddingDesktop.Views;

public partial class WorkbenchView : UserControl
{
    private WebView2CompositionControl? _webView;
    private CoreWebView2Environment? _environment;
    private Uri? _expectedAdminAddress;
    private string? _userDataFolder;
    private bool _isDisposed;
    private bool _eventsRegistered;

    public event EventHandler? WorkbenchReady;
    public event EventHandler<string>? WorkbenchFailed;

    public WorkbenchView()
    {
        InitializeComponent();
    }

    public async Task InitializeAsync(
        Uri coreBaseAddress,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(WorkbenchView));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowLoading("正在初始化隔离的 WebView2 环境…");

            var userDataFolder = Path.Combine(dataRoot, "browser", "workbench", "user-data");
            Directory.CreateDirectory(userDataFolder);

            if (_environment is null
                || !string.Equals(_userDataFolder, userDataFolder, StringComparison.OrdinalIgnoreCase))
            {
                DestroyWebViewControl();
                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder,
                    options: null);
                _userDataFolder = userDataFolder;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var webView = EnsureWebViewControl();
            // CompositionControl needs a visible WPF surface before the graphics
            // capture controller can finish initialization. LoadingOverlay remains
            // above it until navigation succeeds.
            webView.Visibility = Visibility.Visible;
            await webView.EnsureCoreWebView2Async(_environment);
            cancellationToken.ThrowIfCancellationRequested();

            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            RegisterEvents(webView);

            _expectedAdminAddress = new Uri(coreBaseAddress, "/admin/");
            ShowLoading($"正在连接本地 Workbench · {_expectedAdminAddress.Authority}");
            webView.Visibility = Visibility.Visible;
            webView.CoreWebView2.Navigate(_expectedAdminAddress.ToString());
        }
        catch (OperationCanceledException)
        {
            ResetForCoreStop();
            throw;
        }
        catch (Exception ex)
        {
            ShowFailure($"WebView2 初始化失败: {ex.Message}");
        }
    }

    public void ResetForCoreStop()
    {
        _expectedAdminAddress = null;
        ShowLoading("等待 Pudding Core 就绪…", showProgress: false);

        if (_webView is null)
            return;

        _webView.Visibility = Visibility.Collapsed;
        try
        {
            _webView.CoreWebView2?.Navigate("about:blank");
        }
        catch
        {
            DestroyWebViewControl();
        }
    }

    private WebView2CompositionControl EnsureWebViewControl()
    {
        if (_webView is not null)
            return _webView;

        // CompositionControl participates in WPF rendering instead of creating an
        // airspace HWND. This keeps the browser inside the NavigationView content
        // rectangle when the shell uses custom WindowChrome and a Mica backdrop.
        _webView = new WebView2CompositionControl { Visibility = Visibility.Visible };
        WebViewHost.Children.Insert(0, _webView);
        return _webView;
    }

    private void RegisterEvents(WebView2CompositionControl webView)
    {
        if (_eventsRegistered)
            return;

        webView.NavigationCompleted += OnNavigationCompleted;
        webView.CoreWebView2.ProcessFailed += OnProcessFailed;
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _eventsRegistered = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_expectedAdminAddress is null || _webView?.Source is null)
            return;

        if (!Uri.Compare(
                _webView.Source,
                _expectedAdminAddress,
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            return;
        }

        if (e.IsSuccess)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            WorkbenchReady?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowFailure($"页面加载失败: {e.WebErrorStatus}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var error = $"WebView2 进程失败: {e.ProcessFailedKind}";
        Dispatcher.BeginInvoke(() =>
        {
            ShowFailure(error);
            DestroyWebViewControl();
        });
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri, UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void ShowFailure(string error)
    {
        LoadingText.Text = error;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingProgress.Visibility = Visibility.Collapsed;
        LoadingStatusIcon.Text = "\uEA39";
        LoadingStatusIcon.FontFamily = (System.Windows.Media.FontFamily)FindResource("FluentIconFont");
        LoadingStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        WorkbenchFailed?.Invoke(this, error);
    }

    private void ShowLoading(string message, bool showProgress = true)
    {
        LoadingText.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        LoadingStatusIcon.Text = "P";
        LoadingStatusIcon.FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        LoadingStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void DestroyWebViewControl()
    {
        var webView = _webView;
        if (webView is null)
            return;

        if (_eventsRegistered)
        {
            webView.NavigationCompleted -= OnNavigationCompleted;
            if (webView.CoreWebView2 is not null)
            {
                webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
                webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
        }

        WebViewHost.Children.Remove(webView);
        webView.Dispose();
        _webView = null;
        _eventsRegistered = false;
    }

    public void DisposeWebView()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _expectedAdminAddress = null;
        DestroyWebViewControl();
        _environment = null;
    }
}
