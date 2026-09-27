using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Native equivalent of Web TurnContentStream: stable ordered text/activity blocks.</summary>
public sealed class TurnContentView : StackPanel
{
    private readonly Dictionary<string, (FlowBlock Value, FrameworkElement View)> _blocks = [];
    public TurnContentView() { Spacing = 10; }
    public void Update(IEnumerable<ProcessItem> items, string fallbackText)
    {
        var desired = new List<FrameworkElement>();
        var keys = new HashSet<string>();
        foreach (var block in TurnFlow.Build(items, fallbackText))
        {
            keys.Add(block.Key);
            if (!_blocks.TryGetValue(block.Key, out var old))
            {
                FrameworkElement view = block.Kind == "text" ? new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch }
                    : new Expander { IsExpanded = block.Kind == "thinking", HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch };
                old = (null!, view);
            }
            if (old.Value != block)
            {
                if (old.View is Expander expander)
                {
                    expander.Header = block.Kind == "thinking" ? "思考过程" :
                        $"{(block.Kind == "tool" ? "工具" : "活动")} · {block.Name ?? block.Kind} · {block.Status}" +
                        (block.ExitCode is { } exit ? $" · exit {exit}" : "");
                    var content = new StackPanel { Spacing = 8 };
                    if (block.Kind != "tool" || (string.IsNullOrEmpty(block.Arguments) && string.IsNullOrEmpty(block.Output))) content.Children.Add(MessageCard.RenderText(block.Text));
                    if (block.Arguments is { Length: > 0 }) { content.Children.Add(new TextBlock { Text = "输入", Opacity = .6 }); content.Children.Add(MessageCard.RenderText(block.Arguments)); }
                    if (block.Output is { Length: > 0 }) { content.Children.Add(new TextBlock { Text = "输出", Opacity = .6 }); content.Children.Add(MessageCard.RenderText(block.Output)); }
                    var scroll = expander.Content as ScrollViewer ?? new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    var offset = scroll.VerticalOffset;
                    var followLatest = scroll.ScrollableHeight - offset < 24;
                    scroll.Content = content; expander.Content = scroll;
                    if (scroll.IsLoaded) { scroll.UpdateLayout(); scroll.ChangeView(null, followLatest ? scroll.ScrollableHeight : offset, null, true); }
                }
                else if (((ContentControl)old.View).Content is MarkdownView markdown) markdown.Update(block.Text);
                else ((ContentControl)old.View).Content = MessageCard.RenderText(block.Text);
            }
            _blocks[block.Key] = (block, old.View); desired.Add(old.View);
        }
        foreach (var key in _blocks.Keys.Except(keys).ToArray()) _blocks.Remove(key);
        foreach (var child in Children.Where(c => !desired.Contains(c)).ToArray()) Children.Remove(child);
        for (var i = 0; i < desired.Count; i++)
            if (i >= Children.Count || !ReferenceEquals(Children[i], desired[i])) { Children.Remove(desired[i]); Children.Insert(i, desired[i]); }
    }
}
