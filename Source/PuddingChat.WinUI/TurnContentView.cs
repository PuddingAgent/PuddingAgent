using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Native equivalent of Web TurnContentStream: stable ordered text/activity blocks.</summary>
public sealed class TurnContentView : StackPanel
{
    private readonly Dictionary<string, (FlowBlock Value, FrameworkElement View)> _blocks = [];
    private readonly IDictionary<string, bool> _expansions;
    private readonly FlowWindow _window;
    private FlowBlock[] _snapshot = [];
    private readonly Button _earlier = new() { HorizontalAlignment = HorizontalAlignment.Left };
    public int HiddenCount => _window.Start(_snapshot);
    public TurnContentView() : this(new Dictionary<string, bool>()) { }
    public TurnContentView(IDictionary<string, bool> expansions, FlowWindow? window = null)
    {
        _expansions = expansions; _window = window ?? new(); Spacing = 10;
        _earlier.Click += (_, _) => RevealEarlier();
    }
    public void Update(IEnumerable<ProcessItem> items, string fallbackText)
    {
        _snapshot = TurnFlow.Build(items, fallbackText);
        Render();
    }
    public void RevealEarlier()
    {
        var anchor = Children.OfType<FrameworkElement>().FirstOrDefault(c => c != _earlier);
        DependencyObject? parent = this;
        while (parent is not null && parent is not ScrollViewer) parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        var scroll = parent as ScrollViewer;
        var top = anchor is not null && scroll is not null ? anchor.TransformToVisual(scroll).TransformPoint(new()).Y : 0;
        _window.RevealEarlier(_snapshot); Render();
        if (anchor is not null && scroll is not null)
        {
            scroll.UpdateLayout();
            var delta = anchor.TransformToVisual(scroll).TransformPoint(new()).Y - top;
            scroll.ChangeView(null, scroll.VerticalOffset + delta, null, true);
        }
    }
    private void Render()
    {
        var desired = new List<FrameworkElement>();
        var keys = new HashSet<string>();
        if (HiddenCount > 0)
        {
            _earlier.Content = $"显示更早记录（还有 {HiddenCount} 项）";
            desired.Add(_earlier);
        }
        foreach (var block in _snapshot.Skip(HiddenCount))
        {
            keys.Add(block.Key);
            if (!_blocks.TryGetValue(block.Key, out var old))
            {
                FrameworkElement view = block.Kind == "text" ? new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch }
                    : new Expander { IsExpanded = _expansions.TryGetValue(block.Key, out var expanded) ? expanded : block.Kind == "thinking", HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch };
                if (view is Expander disclosure)
                {
                    var disclosureKey = block.Key;
                    disclosure.Expanding += (_, _) =>
                    {
                        _expansions[disclosureKey] = true;
                        if (_blocks.TryGetValue(disclosureKey, out var current)) RenderDisclosure(disclosure, current.Value);
                    };
                    disclosure.Collapsed += (_, _) =>
                    {
                        _expansions[disclosureKey] = false;
                        disclosure.Content = null;
                    };
                }
                old = (null!, view);
            }
            old.View.Margin = new Thickness(Math.Min(block.Depth, 8) * 16, 0, 0, 0);
            if (old.Value != block)
            {
                if (old.View is Expander expander)
                {
                    expander.Header = block.Kind == "thinking" ? "思考过程" :
                        $"{(block.Kind == "tool" ? "工具" : block.Kind == "delegation" ? "子代理" : "活动")} · {block.Name ?? block.Kind} · {TurnFlow.StatusLabel(block.Status)}" +
                        (block.ExitCode is { } exit ? $" · exit {exit}" : "");
                    if (expander.IsExpanded) RenderDisclosure(expander, block);
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
    private static void RenderDisclosure(Expander expander, FlowBlock block)
    {
        var scroll = expander.Content as ScrollViewer ?? new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var offset = scroll.VerticalOffset;
        var followLatest = scroll.ScrollableHeight - offset < 24;
        var content = scroll.Content as ActivityContentView ?? new ActivityContentView();
        content.Update(block);
        scroll.Content = content; expander.Content = scroll;
        if (scroll.IsLoaded) { scroll.UpdateLayout(); scroll.ChangeView(null, followLatest ? scroll.ScrollableHeight : offset, null, true); }
    }
}
