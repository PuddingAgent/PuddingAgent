namespace PuddingBrowser.Protocol;

public static class BrowserBridgeErrorCodes
{
    public const string BrowserNotAvailable = "browser_not_available";
    public const string BrowserBridgeDisconnected = "browser_bridge_disconnected";
    public const string BrowserProtocolMismatch = "browser_protocol_mismatch";
    public const string BrowserInvalidCommand = "browser_invalid_command";
    public const string BrowserDeadlineExceeded = "browser_deadline_exceeded";
    public const string BrowserCancelled = "browser_cancelled";
    public const string BrowserContextNotFound = "browser_context_not_found";
    public const string BrowserPageNotFound = "browser_page_not_found";
    public const string BrowserOperationNotSupported = "browser_operation_not_supported";
    public const string BrowserOperationFailed = "browser_operation_failed";
    public const string BrowserPaused = "browser_paused";
    public const string BrowserUserTakeover = "browser_user_takeover";
}
