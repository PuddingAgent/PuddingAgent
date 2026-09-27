using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyCodeHighlightAsync(Grid root)
    {
        const string source = "public class Example { string Value = \"hello\"; } // note";
        var view = new CodeBlockView(source, "cs") { Width = 600, RequestedTheme = ElementTheme.Light };
        Grid.SetColumnSpan(view, 2); root.Children.Add(view);
        try
        {
            await UntilAsync(() => view.IsLoaded); root.UpdateLayout();
            var text = (TextBlock)((StackPanel)((Border)view.Content).Child).Children.OfType<ScrollViewer>().Single().Content;
            static IEnumerable<Run> Runs(IEnumerable<Inline> inlines) => inlines.SelectMany(i => i switch
            { Run run => new[] { run }, Span span => Runs(span.Inlines), _ => [] });
            var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            Check(view.Code == source && (highContrast ? !view.IsSyntaxHighlighted : view.IsSyntaxHighlighted && Runs(text.Inlines).Any(r => r.Foreground is SolidColorBrush)),
                "native C# syntax uses colored inlines with exact copy source or high-contrast fallback");
            var lightColors = Runs(text.Inlines).Select(r => (r.Foreground as SolidColorBrush)?.Color).ToArray();
            view.RequestedTheme = ElementTheme.Dark; root.UpdateLayout(); await UntilAsync(() => view.ActualTheme == ElementTheme.Dark);
            var darkColors = Runs(text.Inlines).Select(r => (r.Foreground as SolidColorBrush)?.Color).ToArray();
            Check(highContrast || !lightColors.SequenceEqual(darkColors), "code highlighting follows actual light and dark theme");
            view.WrapLines = true; view.Update(source + "\nreturn;", "csharp");
            Check(view.WrapLines && view.Code == source + "\nreturn;" && (highContrast || view.IsSyntaxHighlighted), "highlight update preserves wrap choice and latest source");
            foreach (var (language, code) in new[] { ("json", "{\"key\":true}"), ("js", "const value = 1;"), ("py", "def test():\n    return True"), ("pwsh", "$value = 'text'") })
            {
                view.Update(code, language);
                if (!highContrast && !view.IsSyntaxHighlighted) throw new InvalidOperationException("missing syntax: " + language);
            }
            Check(true, "JSON JavaScript Python and PowerShell grammars resolve through aliases");
            view.Update("<unknown>&原文", "not-a-language");
            Check(!view.IsSyntaxHighlighted && text.Text == view.Code, "unknown language renders exact plain source");
            view.Update(new string('x', 20_000), "cs");
            Check(!view.IsSyntaxHighlighted && text.Text.Length == 20_000 && view.Code.Length == 20_000, "long code bypasses coloring without truncating source");
            view.Update("public class A\r\n{\r\n}\r\n", "cs");
            Check(view.Code == "public class A\r\n{\r\n}\r\n" && (view.IsSyntaxHighlighted
                ? string.Concat(Runs(text.Inlines).Select(r => r.Text)) == view.Code : text.Text == view.Code),
                "CRLF source is lossless even when formatter requires plain-text fallback");
        }
        finally { root.Children.Remove(view); }
    }
}
