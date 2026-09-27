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
    private readonly Button _attachFile = new() { Content = "文件" };
    private readonly Button _voice = new() { Content = "语音", Visibility = Visibility.Collapsed };
    public void SetVoiceInput(VoiceInputControl control)
    {
        _voice.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 400, Width = 280, Content = control } };
        _voice.Visibility = Visibility.Visible;
        UpdateToolbarLayout();
        ToolTipService.SetToolTip(_voice, "录音转写，确认后加入草稿");
    }
    public void SetVoiceAvailability(bool enabled) => _voice.IsEnabled = enabled;
    public void CloseVoiceInput() => _voice.Flyout?.Hide();
    private IReadOnlyList<AttachedImage> _imageItems = [];
    private IReadOnlyList<TextFileContext> _fileItems = [];
    public event EventHandler? AttachFileRequested;
    public event Action<string>? RemoveFileRequested;
    public int FileCount => _fileItems.Count;
    public void SetFiles(IReadOnlyList<TextFileContext> files) { _fileItems = files.ToArray(); RenderAttachments(); }
    public void SetFileAvailability(bool enabled) => _attachFile.IsEnabled = enabled;
    private readonly InfoBar _transferNotice = new() { IsOpen = false, Visibility = Visibility.Collapsed, IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly ScrollViewer _attachmentScroll = new() { MaxHeight = 96, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public Func<DataPackageView, Task>? ImportImagesAsync { get; set; }
    public Func<DataPackageView, Task>? ImportFilesAsync { get; set; }
    public string TransferError => _transferNotice.IsOpen ? _transferNotice.Message : "";
    private readonly StackPanel _images = new() { Spacing = 4 };
    private readonly Grid _toolbar = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly StackPanel _attachmentActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _messageActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    public event EventHandler? AttachRequested;
    public event Action<string>? RemoveImageRequested;
    public int ImageCount => _imageItems.Count;
    public void SetImages(IReadOnlyList<AttachedImage> images)
    { _imageItems = images.ToArray(); RenderAttachments(); }
    private void RenderAttachments()
    {
        _images.Children.Clear();
        _attachmentScroll.Visibility = _imageItems.Count + _fileItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var image in _imageItems)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var remove = new Button { Content = "移除", Tag = image.ArtifactId };
            remove.Click += (_, _) => RemoveImageRequested?.Invoke(image.ArtifactId);
            row.Children.Add(new TextBlock { Text = image.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            ToolTipService.SetToolTip(row, image.Name); Grid.SetColumn(remove, 1);
            row.Children.Add(remove); _images.Children.Add(row);
        }
        foreach (var file in _fileItems)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var preview = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis } };
            ToolTipService.SetToolTip(preview, $"{file.SourcePath}\n{file.ByteCount:N0} 字节 · 点击查看导入时的文本快照");
            preview.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 300, MaxWidth = 420,
                Content = new TextBlock { Text = file.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } };
            var remove = new Button { Content = "移除", Tag = file.Id };
            remove.Click += (_, _) => RemoveFileRequested?.Invoke(file.Id);
            Grid.SetColumn(remove, 1); row.Children.Add(preview); row.Children.Add(remove); _images.Children.Add(row);
        }
    }
    public void SetAttachmentAvailability(bool enabled) { _attach.IsEnabled = enabled; _pasteImage.IsEnabled = enabled; }
    private readonly Button _cancel = new() { Content = "停止", IsEnabled = false };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = .65, Text = "Ctrl+Enter 发送 · Enter 换行", TextWrapping = TextWrapping.Wrap };
    public event EventHandler? SendRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? DraftChanged;
    private bool _composing;
    internal void SetCompositionState(bool composing) => _composing = composing;
    public string Draft { get => _editor.Text; set { if (_editor.Text != value) _editor.Text = value; } }
    public ChatComposer()
    {
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _attachmentActions.Children.Add(_attach); _attachmentActions.Children.Add(_pasteImage); _attachmentActions.Children.Add(_attachFile);
        _messageActions.Children.Add(_voice); _messageActions.Children.Add(_cancel); _messageActions.Children.Add(_send);
        _toolbar.Children.Add(_attachmentActions); _toolbar.Children.Add(_messageActions);
        Grid.SetColumn(_messageActions, 1);
        SizeChanged += (_, _) => UpdateToolbarLayout();
        _attachmentScroll.Content = _images;
        _transferNotice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            _transferNotice.Visibility = _transferNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(_editor); panel.Children.Add(_attachmentScroll); panel.Children.Add(_transferNotice); panel.Children.Add(_hint); panel.Children.Add(_toolbar);
        var surface = Surfaces.Card("CardBackgroundFillColorDefaultBrush");
        surface.Child = panel; surface.Padding = new Thickness(16); surface.CornerRadius = new CornerRadius(16); surface.BorderThickness = new Thickness(1);
        Content = surface;
        _editor.TextChanging += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        _editor.KeyDown += OnKeyDown;
        _editor.TextCompositionStarted += (_, _) => SetCompositionState(true);
        _editor.TextCompositionEnded += (_, _) => SetCompositionState(false);
        _editor.Unloaded += (_, _) => SetCompositionState(false);
        _editor.Paste += async (_, args) =>
        {
            if (!_attach.IsEnabled && !_attachFile.IsEnabled) return;
            try
            {
                var data = Clipboard.GetContent();
                if (!NativeImageTransfer.ContainsImages(data)) return;
                args.Handled = true;
                await ReceiveAttachmentsAsync(data);
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
        _attachFile.Click += (_, _) => AttachFileRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(_attachFile, "添加文本或源代码文件（UTF-8 / 带 BOM 的 UTF-16，单文件最多 256 KiB）");
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, "当前角色的消息草稿");
    }
    private void UpdateToolbarLayout()
    {
        var compact = ActualWidth < (_voice.Visibility == Visibility.Visible ? 560 : 480);
        Grid.SetColumnSpan(_attachmentActions, compact ? 2 : 1);
        Grid.SetRow(_messageActions, compact ? 1 : 0);
        Grid.SetColumn(_messageActions, compact ? 0 : 1);
        Grid.SetColumnSpan(_messageActions, compact ? 2 : 1);
    }
    public void SetContext(string? role, bool editable)
    {
        if (!editable) SetCompositionState(false);
        _editor.IsEnabled = editable;
        _editor.PlaceholderText = role is null ? "先在左侧选择一位角色" : editable ? $"交给 {role} 的工作…" : "当前角色已停用或冻结";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, role is null ? "请选择角色后输入" : $"发给 {role} 的消息草稿");
    }
    public void SetAvailability(bool send, bool cancel, bool retry, bool stopping = false)
    { _send.IsEnabled = send; _cancel.IsEnabled = cancel && !stopping; _cancel.Content = stopping ? "请求停止中…" : "停止"; _send.Content = retry ? "重试原消息" : "发送 ↑";
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
    public async Task ReceiveAttachmentsAsync(DataPackageView data)
    {
        if (!data.Contains(StandardDataFormats.StorageItems) || ImportFilesAsync is null)
        { await ReceiveImagesAsync(data); return; }
        if (!_attachFile.IsEnabled) return;
        _transferNotice.IsOpen = false;
        try { await ImportFilesAsync(data); }
        catch (OperationCanceledException) { }
        catch (Exception e) { ShowTransferError(e.Message); }
    }
    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!NativeImageTransfer.ContainsImages(args.DataView)) return;
        args.Handled = true;
        var enabled = args.DataView.Contains(StandardDataFormats.StorageItems) ? _attachFile.IsEnabled : _attach.IsEnabled;
        args.AcceptedOperation = enabled ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.DragUIOverride.Caption = enabled ? "添加图片或文本文件到当前角色的草稿" : "当前无法添加附件";
    }
    private async void OnDrop(object sender, DragEventArgs args)
    {
        if (!NativeImageTransfer.ContainsImages(args.DataView)) return;
        args.Handled = true;
        var deferral = args.GetDeferral();
        try { await ReceiveAttachmentsAsync(args.DataView); }
        finally { deferral.Complete(); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (HandleSubmitKey(args.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu), args.KeyStatus.WasKeyDown))
            args.Handled = true;
    }
    // Keep IME candidate confirmation and key auto-repeat out of the submit path.
    internal bool HandleSubmitKey(VirtualKey key, bool control, bool shift, bool alt, bool repeated)
    {
        if (key != VirtualKey.Enter || !control || shift || alt || _composing || !_editor.IsEnabled) return false;
        if (!repeated && _send.IsEnabled) SendRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
