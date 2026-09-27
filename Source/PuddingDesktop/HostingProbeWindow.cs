using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace PuddingDesktop;

/// <summary>Opt-in isolated integration harness, not a business browser or privileged bridge.</summary>
internal sealed class HostingProbeWindow : Window
{
    private readonly WebView2 _left = new();
    private readonly WebView2 _right = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PuddingWinUiHostingProbe", Guid.NewGuid().ToString("N"));

    public HostingProbeWindow()
    {
        Title = "Pudding · 隔离 WebView2 宿主验证";
        AppWindow.Resize(new SizeInt32(1000, 680));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        Grid.SetColumn(_right, 1);
        grid.Children.Add(_left); grid.Children.Add(_right);
        var overlay = new Button { Content = "原生浮层 · 点击验证", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16) };
        overlay.Click += async (_, _) => await new ContentDialog { XamlRoot = grid.XamlRoot, Title = "原生浮层", Content = "应可覆盖 WebView2，关闭后网页保持可操作。", CloseButtonText = "关闭" }.ShowAsync();
        grid.Children.Add(overlay);
        grid.Loaded += (_, _) => _loaded.TrySetResult();
        Content = grid;
        Closed += (_, _) => { _left.Close(); _right.Close(); };
    }

    public async Task<string> RunAsync()
    {
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var pages = Path.Combine(_directory, "pages"); Directory.CreateDirectory(pages);
        await File.WriteAllTextAsync(Path.Combine(pages, "index.html"), """
            <!doctype html><html lang="zh"><meta charset="utf-8"><title>Pudding isolation probe</title>
            <style>body{font:18px system-ui;background:#eaf4ec;padding:80px 28px}input,button{font:inherit;padding:10px}p{line-height:1.8}</style>
            <h1>独立浏览器环境</h1><p>这是本机生成的测试页面，没有业务数据或特权桥。</p>
            <input placeholder="输入中文测试焦点"><button onclick="this.textContent='交互成功'">点击测试</button>
            <div style="height:1000px">滚动测试区域</div></html>
            """);
        var leftEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(_directory, "left"), null);
        var rightEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(_directory, "right"), null);
        await _left.EnsureCoreWebView2Async(leftEnvironment);
        await _right.EnsureCoreWebView2Async(rightEnvironment);
        await Task.WhenAll(NavigateAsync(_left, pages), NavigateAsync(_right, pages));
        var cookie = _left.CoreWebView2.CookieManager.CreateCookie("left-only", "yes", "pudding-hosting.test", "/");
        _left.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        var leftCookies = await _left.CoreWebView2.CookieManager.GetCookiesAsync("https://pudding-hosting.test/");
        var rightCookies = await _right.CoreWebView2.CookieManager.GetCookiesAsync("https://pudding-hosting.test/");
        if (!leftCookies.Any(item => item.Name == "left-only") || rightCookies.Any(item => item.Name == "left-only"))
            throw new InvalidOperationException("WebView2 cookie isolation failed.");
        return "双 WebView2 初始化、离线页面加载和 Cookie 隔离通过。浮层、输入、DPI 仍需人工检查。";
    }

    private static async Task NavigateAsync(WebView2 view, string directory)
    {
        view.CoreWebView2.Settings.IsWebMessageEnabled = false;
        view.CoreWebView2.SetVirtualHostNameToFolderMapping("pudding-hosting.test", directory, CoreWebView2HostResourceAccessKind.DenyCors);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException(args.WebErrorStatus.ToString()));
        }
        view.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            view.CoreWebView2.Navigate("https://pudding-hosting.test/index.html");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { view.CoreWebView2.NavigationCompleted -= Completed; }
    }
}
