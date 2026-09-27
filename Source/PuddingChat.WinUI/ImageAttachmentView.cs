using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PuddingChat.WinUI;

/// <summary>On-demand native image preview. Only a Core-resolved file can be decoded.</summary>
public sealed class ImageAttachmentView : UserControl
{
    private readonly IImageAttachmentClient _client;
    private readonly string _workspace, _artifact;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _loading;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 420 };
    private readonly TextBlock _status = new() { Text = "展开查看图片", TextWrapping = TextWrapping.Wrap };
    private readonly Expander _expander;
    public bool PreviewLoaded => _image.Source is not null;
    public ImageAttachmentView(IImageAttachmentClient client, string workspace, string artifact, string label, CancellationToken ct,
        bool expanded = false, Action<bool>? expansionChanged = null)
    {
        _client = client; _workspace = workspace; _artifact = artifact; _lifetime = ct;
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(_status);
        panel.Children.Add(new ScrollViewer { Content = _image, ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 1, MaxZoomFactor = 8, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        _expander = new Expander { Header = label, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _expander.Expanding += async (_, _) => { expansionChanged?.Invoke(true); if (IsLoaded) await LoadAsync(); };
        _expander.Collapsed += (_, _) => { expansionChanged?.Invoke(false); Release(); };
        Loaded += async (_, _) => { if (_expander.IsExpanded) await LoadAsync(); };
        Unloaded += (_, _) => Release();
        Content = _expander;
        _expander.IsExpanded = expanded;
    }
    public async Task LoadAsync()
    {
        if (PreviewLoaded || _loading is not null) return;
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _loading = load;
        _status.Text = "正在加载图片…";
        try
        {
            var source = await _client.GetImagePreviewAsync(_workspace, _artifact, load.Token);
            load.Token.ThrowIfCancellationRequested();
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(source.LocalPath).AsTask(load.Token);
            using var stream = await file.OpenReadAsync().AsTask(load.Token);
            var bitmap = new BitmapImage { DecodePixelWidth = Math.Min(source.Width ?? 1280, 1280) };
            await bitmap.SetSourceAsync(stream).AsTask(load.Token);
            load.Token.ThrowIfCancellationRequested();
            _image.Source = bitmap; _status.Text = $"{source.MimeType} · {source.Width} × {source.Height} · Ctrl+滚轮缩放";
        }
        catch (OperationCanceledException) { }
        catch { _status.Text = "图片暂时无法预览。请收起后重试。"; }
        finally
        {
            if (ReferenceEquals(_loading, load)) _loading = null;
            if (load.IsCancellationRequested && !_lifetime.IsCancellationRequested && IsLoaded && _expander.IsExpanded)
                _ = LoadAsync();
        }
    }
    private void Release() { _loading?.Cancel(); _image.Source = null; _status.Text = "展开查看图片"; }
}
