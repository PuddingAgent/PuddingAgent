using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using System.Runtime.InteropServices.WindowsRuntime;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyMathFormulaAsync(Grid root)
    {
        var formula = new MathFormulaView(@"\frac{1}{2}+\sqrt{x}+\sum_{i=1}^{n}i")
            { RequestedTheme = ElementTheme.Light, VerticalAlignment = VerticalAlignment.Top, Width = 420 };
        Grid.SetColumnSpan(formula, 2); root.Children.Add(formula);
        try
        {
            await UntilAsync(() => formula.Rendered || formula.RenderError is not null);
            Check(formula.Rendered, "fraction root and sum render in native formula control: " + formula.RenderError);
            var image = (Image)((ScrollViewer)formula.Content).Content;
            Check(image.Source is BitmapImage bitmap && bitmap.PixelWidth > 20 && bitmap.PixelHeight > 20,
                "formula has a decoded bitmap with nonempty dimensions");
            root.UpdateLayout(); await NextVisualFrameAsync();
            var light = new RenderTargetBitmap(); await light.RenderAsync(image);
            var pixels = (await light.GetPixelsAsync()).ToArray();
            Check(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 128 && pixels[i * 4] < 32) > 30,
                "light formula bitmap contains visible dark glyph pixels");
            var old = formula.Rendering; formula.RequestedTheme = ElementTheme.Dark;
            await UntilAsync(() => !ReferenceEquals(old, formula.Rendering)); await formula.Rendering;
            Check(formula.Rendered && ReferenceEquals(image, ((ScrollViewer)formula.Content).Content), "theme rerender retains native image without reparenting");
            root.UpdateLayout(); await NextVisualFrameAsync();
            var dark = new RenderTargetBitmap(); await dark.RenderAsync(image);
            pixels = (await dark.GetPixelsAsync()).ToArray();
            Check(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 128 && pixels[i * 4] > 128) > 30,
                "dark formula bitmap contains visible light glyph pixels");
        }
        finally { root.Children.Remove(formula); }
        await UntilAsync(() => !formula.IsLoaded && !formula.Rendered);
        Check(formula.Content is TextBlock, "unloaded formula releases its rendered bitmap");
        foreach (var latex in new[] { @"\thiscommanddoesnotexist{x}", new string('{', 65) + "x" + new string('}', 65), new string('x', 4097) })
        {
            var fallback = new MathFormulaView(latex); root.Children.Add(fallback);
            try
            {
                await UntilAsync(() => fallback.IsLoaded); await Task.Delay(50); await fallback.Rendering;
                Check(!fallback.Rendered && fallback.Content is TextBlock text && text.IsTextSelectionEnabled && text.Text == latex,
                    "unsupported or excessive formula preserves selectable exact source");
            }
            finally { root.Children.Remove(fallback); }
        }
    }

    private static async Task NextVisualFrameAsync()
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> handler = (_, _) => frame.TrySetResult();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler; }
    }

    private static async Task VerifyMathMarkdownAsync(Grid root)
    {
        var source = "公式 $x^2$ 和 **$y^2$**。\n\n$$\n\\frac{1}{2}\n$$";
        var markdown = new MarkdownView(source) { Width = 420, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(markdown, 2); root.Children.Add(markdown);
        try
        {
            var rich = (RichTextBlock)markdown.Children[0];
            var paragraph = (Paragraph)rich.Blocks.Single();
            var inline = (MathFormulaView)paragraph.Inlines.OfType<InlineUIContainer>().Single().Child;
            var block = (MathFormulaView)markdown.Children[1];
            await UntilAsync(() => inline.Rendered && block.Rendered);
            Check(inline.Latex == "x^2" && block.Latex.Contains(@"\frac{1}{2}"), "Markdown inline and display formulas use native controls");
            var bold = paragraph.Inlines.OfType<Bold>().Single();
            Check(bold.Inlines.OfType<InlineUIContainer>().Single().Child is MathFormulaView, "nested emphasis retains formula content");
            markdown.Update(source + "\n\n新增正文");
            Check(ReferenceEquals(rich, markdown.Children[0]) && ReferenceEquals(block, markdown.Children[1]), "stream append retains unchanged formula controls");
            markdown.Update("未闭合 $x");
            Check(markdown.Children.Single() is TextBlock plain && plain.Text.Contains("$x"), "unfinished inline formula keeps visible source");
            markdown.Update("完成 $x^2$");
            Check(markdown.Children.Single() is RichTextBlock, "closing streamed formula switches to native math layout");
            markdown.Update("```tex\n$x^2$\n```");
            Check(markdown.Children.Single() is CodeBlockView code && code.Code == "$x^2$", "code fences preserve literal math delimiters");
        }
        finally { root.Children.Remove(markdown); }
    }
}
