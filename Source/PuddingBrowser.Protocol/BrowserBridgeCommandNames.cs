namespace PuddingBrowser.Protocol;

public static class BrowserBridgeCommandNames
{
    public const string ContextCreate = "context.create";
    public const string ContextList = "context.list";
    public const string ContextGetInfo = "context.getInfo";
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
    public const string PageSnapshot = "page.snapshot";
    public const string PageLocate = "page.locate";
    public const string PageInteract = "page.interact";
    public const string PageWaitFor = "page.waitFor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ContextCreate, ContextList, ContextGetInfo, ContextClose,
        PageCreate, PageList, PageGetInfo, PageActivate, PageClose,
        PageGoto, PageGoBack, PageGoForward, PageReload, PageStop,
        PageSnapshot, PageLocate, PageInteract, PageWaitFor
    };
}
