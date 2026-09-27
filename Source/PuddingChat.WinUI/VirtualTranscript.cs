using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace PuddingChat.WinUI;

/// <summary>Data and presentation state survive recycling; native controls exist only near the viewport.</summary>
public sealed class TranscriptItem(string key, object data, Func<TranscriptItem, FrameworkElement> create,
    Action<FrameworkElement, object>? update = null)
{
    public string Key { get; } = key;
    public bool IsAnchor { get; init; } = true;
    public object Data { get; private set; } = data;
    public FrameworkElement? Element { get; internal set; }
    internal FrameworkElement Create() => create(this);
    public void Update(object data)
    { Data = data; if (Element is { } element) update?.Invoke(element, data); }
}

public sealed class VirtualTranscript
{
    private readonly ObservableCollection<TranscriptItem> _items = [];
    private readonly Factory _factory = new();
    public ItemsRepeater View { get; }
    public int Count => _items.Count;
    public int RealizedCount => _factory.RealizedCount;
    public VirtualTranscript()
    {
        View = new ItemsRepeater { Layout = new StackLayout { Spacing = 8 }, ItemsSource = _items,
            ItemTemplate = _factory, VerticalCacheLength = 1, HorizontalCacheLength = 0 };
        AutomationProperties.SetName(View, "聊天消息");
    }
    public void Clear() => _items.Clear();
    public void SetItems(IReadOnlyList<TranscriptItem> desired)
    {
        var keep = desired.ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--) if (!keep.Contains(_items[i])) _items.RemoveAt(i);
        var existing = _items.ToHashSet();
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], desired[i])) continue;
            var old = existing.Contains(desired[i]) ? _items.IndexOf(desired[i]) : -1;
            if (old >= 0) _items.Move(old, i); else { _items.Insert(i, desired[i]); existing.Add(desired[i]); }
        }
    }
    public MessageBounds[] Geometry() => _items.Where(i => i.IsAnchor && i.Element is { ActualHeight: > 0 })
        .Select(i => new MessageBounds(i.Key, i.Element!.TransformToVisual(View).TransformPoint(new Windows.Foundation.Point()).Y,
            i.Element.ActualHeight)).ToArray();
    public void Restore(ScrollViewer scroll, ReadingPosition position)
    {
        var index = position.FollowLatest ? _items.Count - 1 : _items.ToList().FindIndex(i => i.Key == position.MessageId);
        if (index >= 0)
        {
            var element = (FrameworkElement)View.GetOrCreateElement(index);
            element.UpdateLayout();
        }
        // Use one scroll request: a queued BringIntoView would overwrite the message's internal offset.
        scroll.UpdateLayout();
        scroll.ChangeView(null, position.Restore(Geometry(), scroll.ScrollableHeight), null, true);
    }
    private sealed class Factory : IElementFactory
    {
        private readonly Dictionary<UIElement, TranscriptItem> _realized = [];
        public int RealizedCount => _realized.Count;
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var item = (TranscriptItem)args.Data;
            var element = item.Create(); element.Tag = item.Key;
            item.Element = element; _realized[element] = item;
            return element;
        }
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (_realized.Remove(args.Element, out var item))
            {
                if (ReferenceEquals(item.Element, args.Element)) item.Element = null;
                (args.Element as IDisposable)?.Dispose();
            }
        }
    }
}
