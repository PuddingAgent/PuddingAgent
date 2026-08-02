using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;

namespace PuddingBrowser.WebView2.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SmokeOptions.Parse(args);
        var application = new SmokeApplication(options);
        application.Run();
        return application.ExitCode;
    }
}

internal sealed class SmokeApplication(SmokeOptions options) : Application
{
    public int ExitCode { get; private set; } = 1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = RunSmokeAsync();
    }

    private async Task RunSmokeAsync()
    {
        var surface = new Grid();
        var window = new Window
        {
            Title = "Pudding Browser Phase 2A-3 Smoke",
            Width = 1100,
            Height = 760,
            Content = surface,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        MainWindow = window;
        window.Show();

        WebView2BrowserRuntime? runtime = null;
        try
        {
            Directory.CreateDirectory(options.DataRoot);
            var dispatcher = new WpfUiDispatcher(Dispatcher);
            var surfaceHost = new WpfBrowserSurfaceHost(dispatcher, surface);
            runtime = new WebView2BrowserRuntime(dispatcher, surfaceHost, options.DataRoot);
            var context = await runtime.CreateContextAsync(new BrowserContextOptions
            {
                Id = new BrowserContextId("ctx-phase2a3-smoke"),
                Persistent = false
            }, CancellationToken.None);
            var page = await context.NewPageAsync(new PageCreateOptions
            {
                InitialUrl = options.Url,
                Activate = true
            }, CancellationToken.None);
            await surfaceHost.ActivateAsync(page.Id, CancellationToken.None);

            var initial = await page.SnapshotAsync(new SnapshotOptions
            {
                MaxNodes = 500,
                MaxTextLength = 30_000,
                MaxDepth = 20
            }, CancellationToken.None);
            var save = AssertSingle(await page.QueryAllAsync(new Locator
            {
                Kind = LocatorKind.Role,
                Value = "button",
                Name = "Save profile",
                Exact = true
            }, CancellationToken.None), "save button");
            var staleRef = save.Info.Ref;

            var nameLocator = new Locator { Kind = LocatorKind.Label, Value = "Name", Exact = true };
            var saveLocator = new Locator
            {
                Kind = LocatorKind.Role,
                Value = "button",
                Name = "Save profile",
                Exact = true
            };
            await page.FillAsync(nameLocator,
                "Pudding Phase 2A-3", new FillOptions(), CancellationToken.None);
            await page.TypeAsync(nameLocator, " typed", new TypeOptions(), CancellationToken.None);
            await page.PressAsync(nameLocator, "Tab", new KeyOptions(), CancellationToken.None);
            await page.HoverAsync(saveLocator, new PointerOptions(), CancellationToken.None);
            await page.ScrollAsync(new ScrollOptions { DeltaY = 240 }, CancellationToken.None);
            await AssertSelectorAsync(page, "#name[data-observed-value='Pudding Phase 2A-3 typed']", "type");
            await AssertSelectorAsync(page, "#name[data-pressed='true']", "press");
            await AssertSelectorAsync(page, "#save[data-hovered='true']", "hover");
            await AssertSelectorAsync(page, "body[data-scrolled='true']", "scroll");
            await page.SelectAsync(new Locator { Kind = LocatorKind.Css, Value = "#role" },
                ["designer"], CancellationToken.None);
            await page.CheckAsync(new Locator { Kind = LocatorKind.Css, Value = "#terms" },
                true, CancellationToken.None);
            await page.ClickAsync(new Locator { Kind = LocatorKind.Ref, Value = save.Info.Ref },
                new ClickOptions(), CancellationToken.None);
            var wait = await page.WaitForAsync(new WaitCondition
            {
                Selector = "#saved",
                TimeoutMs = 5_000
            }, CancellationToken.None);
            if (wait.TimedOut)
                throw new InvalidOperationException("Saved status did not become visible");

            var final = await page.SnapshotAsync(new SnapshotOptions
            {
                MaxNodes = 500,
                MaxTextLength = 30_000,
                MaxDepth = 20
            }, CancellationToken.None);
            if (!string.Equals(final.AccessibilityTree?.Contains("Saved", StringComparison.Ordinal), true))
                throw new InvalidOperationException("Final snapshot does not contain Saved status");

            await page.GotoAsync(new Uri(options.Url, "frame"), new NavigationOptions(), CancellationToken.None);
            string? staleCode = null;
            try
            {
                _ = await page.QueryAsync(new Locator { Kind = LocatorKind.Ref, Value = staleRef }, CancellationToken.None);
            }
            catch (BrowserOperationException ex)
            {
                staleCode = ex.Code;
            }
            if (staleCode != "stale_element_reference")
                throw new InvalidOperationException($"Expected stale_element_reference, got {staleCode ?? "no error"}");

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "phase2a3-webview2-smoke-passed",
                pageId = page.Id.Value,
                pageVersion = page.PageVersion,
                initial.NodeCount,
                initial.Truncated,
                saveRef = staleRef,
                finalContainsSaved = true,
                staleCode,
                dataRoot = options.DataRoot
            }));
            ExitCode = 0;
            if (options.HoldSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "phase2a3-webview2-smoke-failed",
                error = ex.GetType().Name,
                message = ex.Message,
                dataRoot = options.DataRoot
            }));
            ExitCode = 1;
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
            window.Close();
            Shutdown(ExitCode);
        }
    }

    private static IElementHandle AssertSingle(IReadOnlyList<IElementHandle> handles, string description)
        => handles.Count == 1
            ? handles[0]
            : throw new InvalidOperationException($"Expected one {description}, found {handles.Count}");

    private static async Task AssertSelectorAsync(IBrowserPage page, string selector, string operation)
    {
        var result = await page.WaitForAsync(new WaitCondition
        {
            Selector = selector,
            TimeoutMs = 5_000
        }, CancellationToken.None);
        if (result.TimedOut)
            throw new InvalidOperationException($"{operation} did not produce its observable TestSite state");
    }
}

internal sealed record SmokeOptions(Uri Url, string DataRoot, int HoldSeconds)
{
    public static SmokeOptions Parse(string[] args)
    {
        string? url = null;
        string? dataRoot = null;
        var holdSeconds = 0;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--url" when index + 1 < args.Length: url = args[++index]; break;
                case "--data-root" when index + 1 < args.Length: dataRoot = args[++index]; break;
                case "--hold-seconds" when index + 1 < args.Length: holdSeconds = int.Parse(args[++index]); break;
            }
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("--url must be an absolute http/https URL");
        dataRoot ??= Path.Combine(Path.GetTempPath(), "PuddingAgent", $"phase2a3-smoke-{Guid.NewGuid():N}");
        return new SmokeOptions(uri, Path.GetFullPath(dataRoot), Math.Clamp(holdSeconds, 0, 120));
    }
}
