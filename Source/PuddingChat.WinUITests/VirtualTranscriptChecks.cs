using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyVirtualTranscriptAsync(Grid root)
    {
        var transcript = new VirtualTranscript(); var created = 0;
        var rows = Enumerable.Range(0, 1000).Select(i =>
        {
            var state = new MessageViewState();
            var message = new ChatMessage($"v{i}", "run", "agent", "角色", DateTimeOffset.UnixEpoch,
                $"消息 {i}", "done", [new("call", "tool_call", "running", "build", 1, "terminal", ToolCallId: "call")]);
            return new TranscriptItem(message.MessageId, message,
                row => { created++; return new MessageCard((ChatMessage)row.Data, state: state); },
                (view, data) => ((MessageCard)view).Update((ChatMessage)data));
        }).ToArray();
        var scroll = new ScrollViewer { Content = transcript.View, Height = 300, Width = 760, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(scroll, 2); root.Children.Add(scroll);
        try
        {
            transcript.SetItems(rows); scroll.UpdateLayout();
            await UntilAsync(() => rows[0].Element is not null);
            Check(transcript.Count == 1000 && transcript.RealizedCount < 80 && created < 80, "1000 messages create only viewport controls");
            var first = (MessageCard)rows[0].Element!;
            static Expander Tool(MessageCard card) => ((StackPanel)((Border)card.Content).Child).Children.OfType<TurnContentView>()
                .Single().Children.OfType<Expander>().First();
            Tool(first).IsExpanded = true;
            var beforeUpdate = created;
            rows[500].Update(((ChatMessage)rows[500].Data) with { Content = "后台更新" });
            Check(created == beforeUpdate && rows[500].Element is null, "offscreen updates do not construct message controls");
            transcript.Restore(scroll, ReadingPosition.Latest);
            await UntilAsync(() => rows[^1].Element is not null && rows[0].Element is null);
            Check(transcript.RealizedCount < 80, "offscreen message controls are released after scrolling");
            await UntilAsync(() => scroll.ScrollableHeight - scroll.VerticalOffset < 2);
            Check(scroll.ScrollableHeight - scroll.VerticalOffset < 2, "virtualized transcript reaches the actual last message");
            transcript.Restore(scroll, new("v500", 24, 0, false));
            await UntilAsync(() => rows[500].Element is not null);
            await Task.Delay(50);
            var middle = transcript.Geometry().Single(m => m.Id == "v500");
            Check(Math.Abs(scroll.VerticalOffset - middle.Top - 24) < 2, "offscreen message restores its internal reading offset");
            transcript.Restore(scroll, new("v0", 0, 0, false));
            await UntilAsync(() => rows[0].Element is MessageCard);
            Check(!ReferenceEquals(first, rows[0].Element) && Tool((MessageCard)rows[0].Element!).IsExpanded,
                "recreated message retains tool disclosure state without retaining old controls");
        }
        finally { transcript.Clear(); scroll.UpdateLayout(); root.Children.Remove(scroll); }
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(16, timeout.Token);
    }
}
