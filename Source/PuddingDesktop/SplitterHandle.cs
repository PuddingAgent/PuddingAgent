using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuddingDesktop;

/// <summary>
/// Divider between the chat and the tool workspace.
/// <para>
/// <see cref="Grid"/> (not Border, which is sealed in WinUI) only because the resize cursor
/// lives on the protected <c>UIElement.ProtectedCursor</c>. This element is the ~6 pixel
/// drag hit area and the keyboard focus target; the visible line is a 1 pixel child.
/// </para>
/// </summary>
public sealed class SplitterHandle : Grid
{
    public SplitterHandle()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        try
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
        catch (Exception)
        {
            // Cursor creation is cosmetic; an unsupported environment must not fail the Shell.
        }
    }
}
