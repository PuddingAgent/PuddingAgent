namespace PuddingBrowser.Abstractions;

/// <summary>Stable identity for a browser context (user-data directory).</summary>
public readonly record struct BrowserContextId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a browser page/tab.</summary>
public readonly record struct PageId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a DOM element handle.</summary>
public readonly record struct ElementHandleId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a JavaScript object handle.</summary>
public readonly record struct JsHandleId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a download operation.</summary>
public readonly record struct DownloadId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a CDP event subscription.</summary>
public readonly record struct BrowserSubscriptionId(string Value)
{
    public override string ToString() => Value;
}
