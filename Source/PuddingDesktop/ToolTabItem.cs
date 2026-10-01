using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// WinUI projection of a <see cref="ToolTab"/>. The registry stays UI-free; this type only
/// adds the shapes the tab strip binds to (glyph, visibility flags) and is refreshed from
/// <see cref="ToolWorkspaceTabs"/> after every registry mutation.
/// </summary>
public sealed class ToolTabItem : INotifyPropertyChanged
{
    internal ToolTabItem(ToolTab tab) => Model = tab;

    internal ToolTab Model { get; }

    public string Id => Model.Id;

    public string Glyph => Model.Kind switch
    {
        ToolTabKind.Home => "\uE80F",
        ToolTabKind.Terminal => "\uE756",
        ToolTabKind.Browser => "\uE774",
        ToolTabKind.Artifact => "\uE8A5",
        ToolTabKind.Output => "\uE8B7",
        _ => "\uE71D",
    };

    public string Title => Model.Title;

    public string Tooltip => Model.Tooltip;

    public bool IsRunning => Model.IsRunning;

    public Visibility RunningVisibility => Model.IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnreadVisibility => Model.HasUnread ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloseVisibility => Model.CanClose ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(RunningVisibility));
        OnPropertyChanged(nameof(UnreadVisibility));
        OnPropertyChanged(nameof(CloseVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
