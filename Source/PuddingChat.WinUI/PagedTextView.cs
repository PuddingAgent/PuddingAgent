using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.ApplicationModel.DataTransfer;

namespace PuddingChat.WinUI;

/// <summary>Large activity payloads use bounded plain-text layout, without truncating the source.</summary>
internal sealed class PagedTextView : StackPanel
{
    private readonly TextPageWindow _window = new();
    private readonly TextBlock _text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly TextBlock _position = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    private readonly Button _previous = new() { Content = "上一页" };
    private readonly Button _next = new() { Content = "下一页" };
    private readonly Button _copy = new() { Content = "复制全文" };
    internal int Page => _window.Page;
    internal string VisibleText => _text.Text;
    internal DataPackage CreateCopyData() { var data = new DataPackage(); data.SetText(_window.Source); return data; }
    public PagedTextView(string text)
    {
        Spacing = 8;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_previous); actions.Children.Add(_next); actions.Children.Add(_copy);
        Children.Add(_position); Children.Add(actions); Children.Add(_text);
        _previous.Click += (_, _) => Move(Page - 1); _next.Click += (_, _) => Move(Page + 1);
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetContent(CreateCopyData()); _copy.Content = "已复制"; }
            catch { _copy.Content = "复制失败"; }
        };
        AutomationProperties.SetName(_text, "当前页原文");
        Update(text);
    }
    public void Update(string text) { _window.Update(text); _copy.Content = "复制全文"; Render(); }
    internal void Move(int page) { _window.Move(page); Render(); }
    private void Render()
    {
        var text = _window.Text;
        if (_text.Text != text) _text.Text = text;
        _position.Text = $"内容较长，以原文分页显示 · 第 {Page + 1}/{_window.Count} 页 · 可复制全文";
        _previous.IsEnabled = Page > 0; _next.IsEnabled = Page + 1 < _window.Count;
    }
}
