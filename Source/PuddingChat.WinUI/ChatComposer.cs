using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PuddingChat.WinUI;

public sealed class ChatComposer : UserControl
{
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 88,
        MaxHeight = 200, PlaceholderText = "描述这位角色需要完成的工作…", BorderThickness = new Thickness(0), Background = null };
    private readonly Button _send = new() { Content = "发送 ↑", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _cancel = new() { Content = "停止", IsEnabled = false };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = .65, Text = "Ctrl+Enter 发送 · Enter 换行", TextWrapping = TextWrapping.Wrap };
    public event EventHandler? SendRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? DraftChanged;
    public string Draft { get => _editor.Text; set { if (_editor.Text != value) _editor.Text = value; } }
    public ChatComposer()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel); buttons.Children.Add(_send);
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(_editor); panel.Children.Add(_hint); panel.Children.Add(buttons);
        var surface = Surfaces.Card("CardBackgroundFillColorDefaultBrush");
        surface.Child = panel; surface.Padding = new Thickness(16); surface.CornerRadius = new CornerRadius(16); surface.BorderThickness = new Thickness(1);
        Content = surface;
        _editor.TextChanging += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        _editor.KeyDown += OnKeyDown;
        _send.Click += (_, _) => SendRequested?.Invoke(this, EventArgs.Empty);
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, "当前角色的消息草稿");
    }
    public void SetAvailability(bool send, bool cancel, bool retry)
    { _send.IsEnabled = send; _cancel.IsEnabled = cancel; _send.Content = retry ? "重试原消息" : "发送 ↑";
        _hint.Text = retry ? "上次发送尚未取得回执。重试沿用原消息；新草稿会保留。" : "Ctrl+Enter 发送 · Enter 换行"; }
    public void FocusEditor() => _editor.Focus(FocusState.Programmatic);
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && _send.IsEnabled &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { args.Handled = true; SendRequested?.Invoke(this, EventArgs.Empty); }
    }
}
