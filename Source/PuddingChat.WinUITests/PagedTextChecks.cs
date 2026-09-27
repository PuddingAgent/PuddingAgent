using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyPagedTextAsync(Grid root)
    {
        var source = string.Concat(Enumerable.Repeat("工具输出 😀\r\n", 12000));
        var view = new PagedTextView(source) { Width = 300, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(view);
        try
        {
            await UntilAsync(() => view.IsLoaded); root.UpdateLayout();
            Check(view.VisibleText.Length <= TextPageWindow.PageSize + 1 && view.VisibleText.Length < source.Length,
                "large output lays out only one bounded text page");
            var next = Descendants<Button>(view).Single(b => b.Content?.ToString() == "下一页");
            ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke();
            Check(view.Page == 1 && view.VisibleText.Length <= TextPageWindow.PageSize + 1,
                "native next page action advances bounded content");
            var reading = view.VisibleText; view.Update(source + "streamed suffix");
            Check(view.Page == 1 && view.VisibleText == reading,
                "streaming append preserves the current output page");
            Check(await view.CreateCopyData().GetView().GetTextAsync() == source + "streamed suffix",
                "copy payload contains complete unmodified output across pages");
            Check(Descendants<Button>(view).All(b => b.TransformToVisual(view).TransformPoint(new()).X + b.ActualWidth <= view.ActualWidth + 1),
                "pagination controls fit narrow activity cards");
            root.UpdateLayout(); await NextVisualFrameAsync();
            var viewport = Descendants<ScrollViewer>(view).Single();
            Check(viewport.ActualHeight <= 360 && viewport.ScrollableHeight > 360 && view.ActualHeight < 500,
                "large tool output has a bounded native scroll viewport instead of expanding the transcript");
            viewport.ChangeView(null, 200, null, true);
            await UntilAsync(() => viewport.VerticalOffset > 100);
            var offset = viewport.VerticalOffset;
            view.Update(source + "another streamed suffix"); root.UpdateLayout(); await NextVisualFrameAsync();
            Check(Math.Abs(viewport.VerticalOffset - offset) < 1 && next.TransformToVisual(view).TransformPoint(new()).Y < viewport.TransformToVisual(view).TransformPoint(new()).Y,
                "streaming preserves output reading position while pagination stays outside the scroll region");
            ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => viewport.VerticalOffset < 1);
            Check(view.Page == 2 && viewport.VerticalOffset < 1, "explicit page navigation starts the new output page at its beginning");
        }
        finally { root.Children.Remove(view); }
    }
}
