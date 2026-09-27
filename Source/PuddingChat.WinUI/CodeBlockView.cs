using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace PuddingChat.WinUI;

/// <summary>Selectable native code; updates preserve the scrolling surface and reader's wrap choice.</summary>
public sealed class CodeBlockView : UserControl
{
    private readonly TextBlock _language = new() { Opacity = .6, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _code = new() { IsTextSelectionEnabled = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, LineHeight = 21 };
    private readonly Button _copy = new() { Content = "复制代码" };
    private readonly ToggleButton _wrap = new() { Content = "自动换行" };
    private readonly ScrollViewer _scroll;
    public string Code => _code.Text;
    public bool WrapLines { get => _wrap.IsChecked == true; set => _wrap.IsChecked = value; }
    public CodeBlockView(string code, string language)
    {
        _scroll = new ScrollViewer { Content = _code, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_wrap); actions.Children.Add(_copy);
        header.Children.Add(_language); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(header); panel.Children.Add(_scroll);
        var surface = Surfaces.Card("SubtleFillColorSecondaryBrush"); surface.Padding = new Thickness(12);
        surface.CornerRadius = new CornerRadius(8); surface.Child = panel; Content = surface;
        _wrap.Checked += (_, _) => ApplyWrapping(); _wrap.Unchecked += (_, _) => ApplyWrapping();
        _copy.Click += (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(Code);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); _copy.Content = "已复制";
            }
            catch { _copy.Content = "复制失败，请重试"; }
        };
        Update(code, language);
    }
    public void Update(string code, string language)
    {
        if (_code.Text != code) { _code.Text = code; _copy.Content = "复制代码"; }
        _language.Text = string.IsNullOrWhiteSpace(language) ? "代码" : language;
        ToolTipService.SetToolTip(_language, _language.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_code, $"{_language.Text} 代码");
    }
    private void ApplyWrapping()
    {
        _code.TextWrapping = WrapLines ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _scroll.HorizontalScrollMode = WrapLines ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = WrapLines ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }
}
