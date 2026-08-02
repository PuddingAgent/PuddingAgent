namespace PuddingBrowser.Protocol;

public static class BrowserBridgeProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const string EndpointPath = "/desktop/browser-bridge";
    public const string ControlTokenHeader = "X-Pudding-Desktop-Token";
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(45);
}
