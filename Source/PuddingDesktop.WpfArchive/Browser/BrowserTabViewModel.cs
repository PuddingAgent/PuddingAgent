using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingBrowser.Abstractions;

namespace PuddingDesktop.Browser;

public sealed class BrowserTabViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _url = string.Empty;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isAgentTarget;
    private bool _isActive;

    public required PageId PageId { get; init; }

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
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

    /// <summary>
    /// True when this tab is the Agent target page (2.7 fix).
    /// Displayed as a marker on the tab strip.
    /// </summary>
    public bool IsAgentTarget
    {
        get => _isAgentTarget;
        set { _isAgentTarget = value; OnPropertyChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
