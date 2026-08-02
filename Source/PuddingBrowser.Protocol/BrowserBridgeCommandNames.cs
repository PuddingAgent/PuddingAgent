namespace PuddingBrowser.Protocol;

public static class BrowserBridgeCommandNames
{
    public const string ContextCreate = "context.create";
    public const string ContextClose = "context.close";
    public const string PageCreate = "page.create";
    public const string PageList = "page.list";
    public const string PageGetInfo = "page.getInfo";
    public const string PageActivate = "page.activate";
    public const string PageClose = "page.close";
    public const string PageGoto = "page.goto";
    public const string PageGoBack = "page.goBack";
    public const string PageGoForward = "page.goForward";
    public const string PageReload = "page.reload";
    public const string PageStop = "page.stop";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ContextCreate, ContextClose,
        PageCreate, PageList, PageGetInfo, PageActivate, PageClose,
        PageGoto, PageGoBack, PageGoForward, PageReload, PageStop
    };
}
