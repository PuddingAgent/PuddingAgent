using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Stable content slots within one expanded execution activity.</summary>
internal sealed class ActivityContentView : StackPanel
{
    private readonly Dictionary<string, (string Text, UIElement View)> _slots = [];
    private readonly MarkdownImageContext? _images;
    public ActivityContentView(MarkdownImageContext? images = null) { _images = images; Spacing = 8; }
    public void Update(FlowBlock block, Action<string>? inspectDelegation = null)
    {
        var desired = new List<UIElement>();
        var keys = new HashSet<string>();
        if (block.Kind == "delegation" && block.DelegationExecutionId is { Length: > 0 } runId && inspectDelegation is not null)
        {
            const string key = "inspect"; keys.Add(key);
            if (!_slots.TryGetValue(key, out var slot) || slot.Text != runId)
            {
                var open = new Button { Content = "查看子代理详情" };
                open.Click += (_, _) => inspectDelegation(runId);
                slot = (runId, open); _slots[key] = slot;
            }
            desired.Add(slot.View);
        }
        void Add(string key, string text, bool label = false)
        {
            keys.Add(key);
            if (!_slots.TryGetValue(key, out var current))
                current = (text, label ? new TextBlock { Text = text, Opacity = .6 } : new MarkdownView(text, _images));
            else if (current.Text != text)
            {
                if (current.View is MarkdownView markdown) markdown.Update(text);
                else ((TextBlock)current.View).Text = text;
                current = (text, current.View);
            }
            _slots[key] = current; desired.Add(current.View);
        }
        if (block.Kind != "tool" || (string.IsNullOrEmpty(block.Arguments) && string.IsNullOrEmpty(block.Output))) Add("body", block.Text);
        if (block.Arguments is { Length: > 0 }) { Add("input-label", "输入", true); Add("input", block.Arguments); }
        if (block.Output is { Length: > 0 })
        {
            Add("output-label", block.Kind == "delegation" ? "结果摘要" : "输出", true);
            Add("output", block.Kind == "delegation" && block.Output.Length > 300 ? block.Output[..300] + "…（摘要）" : block.Output);
        }
        foreach (var key in _slots.Keys.Except(keys).ToArray()) _slots.Remove(key);
        foreach (var child in Children.Where(c => !desired.Contains(c)).ToArray()) Children.Remove(child);
        for (var i = 0; i < desired.Count; i++)
            if (i >= Children.Count || !ReferenceEquals(Children[i], desired[i]))
            { Children.Remove(desired[i]); Children.Insert(i, desired[i]); }
    }
}
