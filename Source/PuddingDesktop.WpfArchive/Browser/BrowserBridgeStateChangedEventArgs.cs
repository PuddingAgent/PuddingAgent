namespace PuddingDesktop.Browser;

public sealed class BrowserBridgeStateChangedEventArgs : EventArgs
{
    public BrowserBridgeConnectionState OldState { get; }
    public BrowserBridgeConnectionState NewState { get; }
    public string? Reason { get; }

    public BrowserBridgeStateChangedEventArgs(
        BrowserBridgeConnectionState oldState,
        BrowserBridgeConnectionState newState,
        string? reason = null)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }
}
