using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace PuddingChat.WinUI;

internal static class Surfaces
{
    // ThemeResource expressions remain live when the host changes Light/Dark/HighContrast.
    // Resource names are internal constants, never message or service content.
    internal static Border Card(string resource) => (Border)XamlReader.Load(
        "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "Background=\"{ThemeResource " + resource + "}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" />");
    internal static Grid Navigation() => (Grid)XamlReader.Load(
        "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource LayerOnMicaBaseAltFillColorDefaultBrush}\" />");
}
