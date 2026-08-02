namespace PuddingDesktop.Browser;

public interface IDesktopBrowserBridgeClient : IAsyncDisposable
{
    BrowserBridgeConnectionState State { get; }
    event EventHandler<BrowserBridgeStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(
        Uri coreBaseAddress,
        string controlToken,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}
