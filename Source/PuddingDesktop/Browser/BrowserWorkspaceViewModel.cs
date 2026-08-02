using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingBrowser.Abstractions;

namespace PuddingDesktop.Browser;

public sealed class BrowserWorkspaceViewModel : INotifyPropertyChanged
{
    private PageId? _activePageId;
    private string _addressBarText = string.Empty;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;
    private AgentBrowserControlState _controlState;
    private BrowserBridgeConnectionState _bridgeState;
    private string _statusText = "未连接";

    public ObservableCollection<BrowserTabViewModel> Tabs { get; }

    public PageId? ActivePageId
    {
        get => _activePageId;
        set { _activePageId = value; OnPropertyChanged(); }
    }

    public string AddressBarText
    {
        get => _addressBarText;
        set { _addressBarText = value; OnPropertyChanged(); }
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set { _canGoBack = value; OnPropertyChanged(); }
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set { _canGoForward = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public AgentBrowserControlState ControlState
    {
        get => _controlState;
        set { _controlState = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public BrowserBridgeConnectionState BridgeState
    {
        get => _bridgeState;
        set { _bridgeState = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => BridgeState != BrowserBridgeConnectionState.Connected
        ? "Bridge 未连接"
        : ControlState switch
        {
            AgentBrowserControlState.AgentControlling => "Agent 正在控制",
            AgentBrowserControlState.Paused => "已暂停",
            AgentBrowserControlState.UserTakeover => "用户接管",
            _ => "就绪"
        };

    public BrowserWorkspaceViewModel()
    {
        Tabs = new ObservableCollection<BrowserTabViewModel>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
