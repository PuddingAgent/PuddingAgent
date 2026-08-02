namespace PuddingHost.Hosting;

/// <summary>
/// Factory for creating PuddingHostOptions from command-line args or defaults.
/// </summary>
public static class PuddingHostOptionsFactory
{
    /// <summary>
    /// Create options for Console (dev server) mode.
    /// DataRoot is resolved from --data-root arg or PUDDING_DATA_ROOT env var.
    /// </summary>
    public static PuddingHostOptions ForConsole(string[] args)
    {
        return new PuddingHostOptions
        {
            Mode = PuddingHostMode.Console,
            DataRoot = PuddingDataRootBootstrapper.ResolveDataRoot(args),
            ServeAdminSpa = true,
            OpenExternalBrowser = false,
        };
    }

    /// <summary>
    /// Create options for Desktop (WPF in-process) mode with loopback binding.
    /// Legacy Phase 0 mode; Phase 1A uses DesktopChild instead.
    /// </summary>
    public static PuddingHostOptions ForDesktop(string dataRoot)
    {
        return new PuddingHostOptions
        {
            Mode = PuddingHostMode.Desktop,
            DataRoot = dataRoot,
            Urls = ["http://127.0.0.1:0"],
            ServeAdminSpa = true,
            BrowserAutomationEnabled = true,
        };
    }

    /// <summary>
    /// Create options for DesktopChild mode (launched by PuddingDesktop.exe).
    /// Parses --desktop-child, --desktop-parent-pid, --data-root, --urls from args.
    /// </summary>
    public static PuddingHostOptions ForDesktopChild(string[] args)
    {
        string? dataRoot = null;
        string? urls = null;
        int? parentPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length:
                    dataRoot = args[++i];
                    break;
                case "--urls" when i + 1 < args.Length:
                    urls = args[++i];
                    break;
                case "--desktop-parent-pid" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var pid))
                        parentPid = pid;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new InvalidOperationException("--data-root is required for DesktopChild mode.");

        if (parentPid is not > 0)
            throw new InvalidOperationException("--desktop-parent-pid must be a positive process ID.");

        var listenUrl = string.IsNullOrWhiteSpace(urls) ? "http://127.0.0.1:0" : urls;
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenAddress)
            || listenAddress.Scheme != Uri.UriSchemeHttp
            || !listenAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                $"DesktopChild must bind a loopback HTTP URL. Received: {listenUrl}");
        }

        return new PuddingHostOptions
        {
            Mode = PuddingHostMode.DesktopChild,
            DataRoot = dataRoot,
            Urls = [listenUrl],
            ServeAdminSpa = true,
            BrowserAutomationEnabled = true,
            DesktopParentPid = parentPid,
        };
    }
}
