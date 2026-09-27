using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyActivityStreamingAsync(Grid root)
    {
        var flow = new TurnContentView { Width = 600, VerticalAlignment = VerticalAlignment.Top };
        ProcessItem call = new("call", "tool_call", "running", "执行", 1, "terminal", Arguments: "```json\n{\"command\":\"build\"}\n```", ToolCallId: "call");
        ProcessItem result = new("result", "tool_result", "running", "", 2, "terminal", Output: "```text\nfirst", ToolCallId: "call");
        flow.Update([call, result], "");
        var disclosure = (Expander)flow.Children.Single(); disclosure.IsExpanded = true;
        Grid.SetColumnSpan(flow, 2); root.Children.Add(flow);
        try
        {
            await UntilAsync(() => flow.IsLoaded); root.UpdateLayout();
            var scroll = (ScrollViewer)disclosure.Content;
            var body = (StackPanel)scroll.Content;
            var input = body.Children.OfType<MarkdownView>().First();
            var output = body.Children.OfType<MarkdownView>().Last();
            var code = (CodeBlockView)output.Children.Single(); code.WrapLines = true;
            flow.Update([call, result with { Output = "```text\nfirst\nsecond" }], ""); root.UpdateLayout();
            Check(ReferenceEquals(scroll, disclosure.Content) && ReferenceEquals(body, scroll.Content),
                "expanded tool retains its scrolling surface and content panel during streaming");
            Check(ReferenceEquals(input, body.Children.OfType<MarkdownView>().First()) && ReferenceEquals(output, body.Children.OfType<MarkdownView>().Last()),
                "tool input and output markdown retain distinct stable slots");
            Check(ReferenceEquals(code, output.Children.Single()) && code.WrapLines && code.Code.EndsWith("second"),
                "nested streamed code retains reader wrap preference and updates text");
            flow.Update([call, result with { Status = "done", Output = "```text\nfirst\nsecond\n```" }], "");
            Check(ReferenceEquals(code, output.Children.Single()) && disclosure.Header.ToString()!.Contains("已完成"),
                "terminal status updates preserve expanded tool content");
            flow.Update([call, result with { Output = null }], "");
            Check(body.Children.OfType<MarkdownView>().Count() == 1 && ReferenceEquals(input, body.Children.OfType<MarkdownView>().Single()),
                "removed output clears stale slot without replacing input");
            flow.Update([new("thought", "thinking", "running", "推理第一段", 3)], "");
            var thought = (Expander)flow.Children.Single();
            var thoughtBody = (StackPanel)((ScrollViewer)thought.Content).Content;
            var text = thoughtBody.Children.OfType<MarkdownView>().Single();
            flow.Update([new("thought", "thinking", "running", "推理第一段继续", 3)], "");
            Check(ReferenceEquals(text, thoughtBody.Children.OfType<MarkdownView>().Single())
                && text.Children.OfType<TextBlock>().Single().Text == "推理第一段继续",
                "visible reasoning updates its existing markdown view");
        }
        finally { root.Children.Remove(flow); }
    }
}
