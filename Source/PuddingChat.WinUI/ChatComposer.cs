using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;

namespace PuddingChat.WinUI;

public sealed class ChatComposer : UserControl
{
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 88,
        MaxHeight = 200, PlaceholderText = "描述这位角色需要完成的工作…", BorderThickness = new Thickness(0), Background = null };
    private readonly Button _send = new() { Content = "发送 ↑", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _attach = new() { Content = "＋ 图片" };
    private readonly Button _pasteImage = new() { Content = "粘贴图片" };
    private readonly InfoBar _transferNotice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    public Func<DataPackageView, Task>? ImportImagesAsync { get; set; }
    public string TransferError => _transferNotice.IsOpen ? _transferNotice.Message : "";
    private readonly StackPanel _images = new() { Spacing = 4 };
    public event EventHandler? AttachRequested;
    public event Action<string>? RemoveImageRequested;
    public int ImageCount => _images.Children.Count;
    public void SetImages(IReadOnlyList<AttachedImage> images)
    {
        _images.Children.Clear();
        foreach (var image in images)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var remove = new Button { Content = "移除", Tag = image.ArtifactId };
            remove.Click += (_, _) => RemoveImageRequested?.Invoke(image.ArtifactId);
            row.Children.Add(new TextBlock { Text = image.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 500, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(remove); _images.Children.Add(row);
        }
    }
    public void SetAttachmentAvailability(bool enabled) { _attach.IsEnabled = enabled; _pasteImage.IsEnabled = enabled; }
    private readonly Button _cancel = new() { Content = "停止", IsEnabled = false };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = .65, Text = "Ctrl+Enter 发送 · Enter 换行", TextWrapping = TextWrapping.Wrap };
    public event EventHandler? SendRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? DraftChanged;
    public string Draft { get => _editor.Text; set { if (_editor.Text != value) _editor.Text = value; } }
    public ChatComposer()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_attach); buttons.Children.Add(_pasteImage); buttons.Children.Add(_cancel); buttons.Children.Add(_send);
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(_editor); panel.Children.Add(new ScrollViewer { Content = _images, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); panel.Children.Add(_transferNotice); panel.Children.Add(_hint); panel.Children.Add(buttons);
        var surface = Surfaces.Card("CardBackgroundFillColorDefaultBrush");
        surface.Child = panel; surface.Padding = new Thickness(16); surface.CornerRadius = new CornerRadius(16); surface.BorderThickness = new Thickness(1);
        Content = surface;
        _editor.TextChanging += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        _editor.KeyDown += OnKeyDown;
        _editor.Paste += async (_, args) =>
        {
            if (!_attach.IsEnabled) return;
            try
            {
                var data = Clipboard.GetContent();
                if (!NativeImageTransfer.ContainsImages(data)) return;
                args.Handled = true;
                await ReceiveImagesAsync(data);
            }
            catch (Exception) { ShowTransferError("暂时无法读取剪贴板，请重试。"); }
        };
        _pasteImage.Click += async (_, _) =>
        {
            try { await ReceiveImagesAsync(Clipboard.GetContent()); }
            catch (Exception) { ShowTransferError("暂时无法读取剪贴板，请重试。"); }
        };
        AllowDrop = true;
        AddHandler(DragOverEvent, new DragEventHandler(OnDragOver), true);
        AddHandler(DropEvent, new DragEventHandler(OnDrop), true);
        _send.Click += (_, _) => SendRequested?.Invoke(this, EventArgs.Empty);
        _attach.Click += (_, _) => AttachRequested?.Invoke(this, EventArgs.Empty);
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, "当前角色的消息草稿");
    }
    public void SetContext(string? role, bool editable)
    {
        _editor.IsEnabled = editable;
        _editor.PlaceholderText = role is null ? "先在左侧选择一位角色" : editable ? $"交给 {role} 的工作…" : "当前角色已停用或冻结";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, role is null ? "请选择角色后输入" : $"发给 {role} 的消息草稿");
    }
    public void SetAvailability(bool send, bool cancel, bool retry)
    { _send.IsEnabled = send; _cancel.IsEnabled = cancel; _send.Content = retry ? "重试原消息" : "发送 ↑";
        _hint.Text = retry ? "上次发送尚未取得回执。重试沿用原消息；新草稿会保留。" : "Ctrl+Enter 发送 · Enter 换行"; }
    public void FocusEditor() => _editor.Focus(FocusState.Programmatic);
    public async Task ReceiveImagesAsync(DataPackageView data)
    {
        if (!_attach.IsEnabled || ImportImagesAsync is null) return;
        _transferNotice.IsOpen = false;
        try { await ImportImagesAsync(data); }
        catch (OperationCanceledException) { }
        catch (Exception e) { ShowTransferError(e.Message); }
    }
    private void ShowTransferError(string message) { _transferNotice.Message = message; _transferNotice.IsOpen = true; }
    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!NativeImageTransfer.ContainsImages(args.DataView)) return;
        args.Handled = true;
        args.AcceptedOperation = _attach.IsEnabled ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = _attach.IsEnabled ? "添加图片到当前角色的草稿" : "当前无法添加图片";
    }
    private async void OnDrop(object sender, DragEventArgs args)
    {
        if (!NativeImageTransfer.ContainsImages(args.DataView)) return;
        args.Handled = true;
        var deferral = args.GetDeferral();
        try { await ReceiveImagesAsync(args.DataView); }
        finally { deferral.Complete(); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && _send.IsEnabled &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { args.Handled = true; SendRequested?.Invoke(this, EventArgs.Empty); }
    }
}
