using Microsoft.Playwright;

namespace HarnessAgent.Core.Browser;

/// <summary>
/// Browser automation via Playwright.
/// Supports headless Chromium and connecting to an existing browser via CDP.
/// </summary>
public sealed class BrowserControl : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public IPage? Page => _page;

    // ── Lifecycle ──

    /// <summary>Launch a headless Chromium browser.</summary>
    public async Task<IPage> LaunchAsync(bool headless = true,
        string[]? args = null, CancellationToken ct = default)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = args ?? Array.Empty<string>(),
        });
        _context = await _browser.NewContextAsync();
        _page = await _context.NewPageAsync();
        return _page;
    }

    /// <summary>Connect to an existing browser via CDP (Chrome DevTools Protocol).</summary>
    public async Task<IPage> ConnectAsync(string wsEndpoint,
        CancellationToken ct = default)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.ConnectOverCDPAsync(wsEndpoint);
        _context = _browser.Contexts[0];
        _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
        return _page;
    }

    // ── Navigation ──

    /// <summary>Navigate to a URL.</summary>
    public async Task GoToAsync(string url, int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        await _page.GotoAsync(url, new PageGotoOptions { Timeout = timeoutMs });
    }

    // ── Interaction ──

    /// <summary>Click an element by CSS selector.</summary>
    public async Task ClickAsync(string selector, CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        await _page.ClickAsync(selector);
    }

    /// <summary>Type text into an input element.</summary>
    public async Task TypeAsync(string selector, string text,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        await _page.FillAsync(selector, text);
    }

    /// <summary>Get page title.</summary>
    public async Task<string> GetTitleAsync(CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        return await _page.TitleAsync();
    }

    /// <summary>Get page content as text.</summary>
    public async Task<string> GetTextAsync(string? selector = null,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        if (selector != null)
            return await _page.TextContentAsync(selector) ?? "";

        return await _page.ContentAsync();
    }

    /// <summary>Screenshot the current page.</summary>
    public async Task<byte[]> ScreenshotAsync(bool fullPage = false,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        return await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = fullPage,
        });
    }

    /// <summary>Take screenshot and save to file.</summary>
    public async Task ScreenshotToFileAsync(string path, bool fullPage = false,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = fullPage,
            Path = path,
        });
    }

    /// <summary>Get page URL.</summary>
    public async Task<string> GetUrlAsync(CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        return _page.Url;
    }

    /// <summary>Execute JavaScript in the page.</summary>
    public async Task<T?> EvaluateAsync<T>(string expression,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        return await _page.EvaluateAsync<T>(expression);
    }

    /// <summary>Wait for a selector to appear.</summary>
    public async Task WaitForAsync(string selector, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        await _page.WaitForSelectorAsync(selector,
            new PageWaitForSelectorOptions { Timeout = timeoutMs });
    }

    /// <summary>Get all elements matching a selector.</summary>
    public async Task<IReadOnlyList<IElementHandle>> QueryAllAsync(
        string selector, CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("Browser not launched.");
        return await _page.QuerySelectorAllAsync(selector);
    }

    // ── Cleanup ──

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
