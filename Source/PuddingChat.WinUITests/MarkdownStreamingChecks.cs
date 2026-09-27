using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private void VerifyLongMarkdownStreaming()
    {
        // Exercise many completed blocks, rather than one oversized literal paragraph.
        var source = string.Join("\n\n", Enumerable.Range(0, 800).Select(i => $"第 {i} 段正文，保留读者位置。"))
            + "\n\n```csharp\nvar value = 1;\n```";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var view = new MarkdownView(source);
        var previous = view.Children.ToArray();
        var code = view.Children.OfType<CodeBlockView>().Single(); code.WrapLines = true;
        var initialMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
        for (var i = 1; i <= 12; i++) view.Update(source + "\n\n正在追加 " + new string('文', i));
        var appendMs = watch.Elapsed.TotalMilliseconds;
        Check(previous.Select((child, i) => ReferenceEquals(child, view.Children[i])).All(same => same)
            && code.WrapLines && view.Children.Count == previous.Length + 1,
            "long streaming reply retains all completed blocks and code reader preferences");
        var current = view.Children.ToArray(); view.Update(source + "\n\n正在追加 " + new string('文', 12));
        Check(current.SequenceEqual(view.Children), "duplicate Markdown snapshot retains the entire visual tree");
        var references = new MarkdownView("[文档][target]\n\n尚未定义");
        var unresolved = references.Children[0];
        references.Update("[文档][target]\n\n尚未定义\n\n[target]: https://example.invalid/docs");
        Check(!ReferenceEquals(unresolved, references.Children[0])
            && ((TextBlock)references.Children[0]).Inlines.OfType<Hyperlink>().Single().NavigateUri.AbsoluteUri == "https://example.invalid/docs",
            "appended reference definitions still invalidate earlier unresolved links");
        view.Update("替换后的正文");
        Check(view.Children.Count == 1 && ((TextBlock)view.Children[0]).Text == "替换后的正文",
            "large reply replacement removes all obsolete blocks");
        File.WriteAllText(Path.ChangeExtension(Report, ".markdown-performance.json"), System.Text.Json.JsonSerializer.Serialize(new
        { blocks = previous.Length, appends = 12, initialMs, appendMs, note = "UI-thread control construction only; no layout, no latency guarantee" }));
    }
}
