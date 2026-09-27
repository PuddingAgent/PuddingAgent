using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static void VerifyLazyActivity()
    {
        var events = Enumerable.Range(0, 500).Select(i => new ProcessItem($"tool-{i}", "tool_call", "running", "调用", i,
            "terminal", Arguments: new string('a', 10000), ToolCallId: $"call-{i}")).ToArray();
        var flow = new TurnContentView(); flow.Update(events, "");
        while (flow.HiddenCount > 0) flow.RevealEarlier();
        Check(flow.Children.Count == 500 && flow.Children.OfType<Expander>().All(e => e.Content is null),
            "collapsed tools do not allocate argument and output views");
        var first = (Expander)flow.Children[0]; first.IsExpanded = true;
        Check(first.Content is ScrollViewer { Content: StackPanel { Children.Count: 2 } }, "tool inputs render on first disclosure");
        first.IsExpanded = false;
        Check(first.Content is null, "collapsing releases native activity content");
        ProcessItem result = new("result", "tool_result", "done", "完成", 600, "terminal", Output: "最新输出", ToolCallId: "call-0");
        flow.Update(events.Append(result), "");
        Check(ReferenceEquals(first, flow.Children[0]) && first.Content is null && first.Header.ToString()!.Contains("完成"),
            "collapsed stream updates status without recreating its body");
        first.IsExpanded = true;
        var content = (StackPanel)((ScrollViewer)first.Content!).Content;
        Check(content.Children.OfType<MarkdownView>().Last().Children.OfType<TextBlock>().Single().Text == "最新输出",
            "reopening shows the latest canonical output");
        var thoughts = new TurnContentView();
        thoughts.Update([new("thought", "thinking", "running", "思考", 1)], "");
        var reasoning = (Expander)thoughts.Children.Single();
        Check(reasoning.IsExpanded && reasoning.Content is ScrollViewer, "reasoning remains visible by default");
    }
}
