using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyCodeBlockAsync(Grid root)
    {
        var line = "var value = \"" + new string('x', 300) + "\";";
        var markdown = new MarkdownView("```cs\n" + line) { Width = 420, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(markdown, 2); root.Children.Add(markdown);
        try
        {
            await UntilAsync(() => markdown.IsLoaded); root.UpdateLayout();
            var code = (CodeBlockView)markdown.Children.Single();
            var panel = (StackPanel)((Border)code.Content).Child;
            var scroll = panel.Children.OfType<ScrollViewer>().Single();
            Check(scroll.ScrollableWidth > 100 && code.Code == line, "unfinished native code fence preserves exact source and horizontal overflow");
            scroll.ChangeView(100, null, null, true); await UntilAsync(() => scroll.HorizontalOffset > 90);
            markdown.Update("```cs\n" + line + "\nreturn value;\n```"); root.UpdateLayout(); await Task.Delay(30);
            Check(ReferenceEquals(code, markdown.Children.Single()) && Math.Abs(scroll.HorizontalOffset - 100) < 2,
                "streamed code keeps its native control and horizontal reading position");
            code.WrapLines = true; root.UpdateLayout();
            Check(scroll.HorizontalScrollMode == ScrollMode.Disabled && ((TextBlock)scroll.Content).TextWrapping == TextWrapping.Wrap,
                "code wrapping uses native text layout without horizontal scrolling");
            markdown.Update("```cs\n" + line + "\nreturn value + 1;\n```"); root.UpdateLayout();
            Check(ReferenceEquals(code, markdown.Children.Single()) && code.WrapLines && code.Code.EndsWith("return value + 1;"),
                "stream append retains wrap preference and exposes latest complete code");
            code.WrapLines = false; root.UpdateLayout();
            Check(scroll.HorizontalScrollMode == ScrollMode.Enabled && scroll.ScrollableWidth > 100,
                "reader can return to unwrapped code");
        }
        finally { root.Children.Remove(markdown); }
    }
}
