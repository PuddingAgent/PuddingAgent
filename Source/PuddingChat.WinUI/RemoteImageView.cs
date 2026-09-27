using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace PuddingChat.WinUI;

public sealed class RemoteImageView : UserControl
{
    private readonly Uri _uri;
    private readonly IRemoteImageSource _source;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _load;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 420 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Expander _expander;
    public bool PreviewLoaded => _image.Source is not null;
    public RemoteImageView(Uri uri, string label, IRemoteImageSource source, CancellationToken lifetime)
    {
        _uri = uri; _source = source; _lifetime = lifetime;
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(_status); panel.Children.Add(_image);
        _expander = new Expander { Header = $"{(string.IsNullOrWhiteSpace(label) ? "图片" : label)} · {uri.Host}", Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _expander.Expanding += async (_, _) => { if (IsLoaded) await LoadAsync(); };
        _expander.Collapsed += (_, _) => Release();
        Loaded += async (_, _) => { if (_expander.IsExpanded) await LoadAsync(); };
        Unloaded += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!IsLoaded) Release(); });
        Content = _expander; Release();
    }
    public async Task LoadAsync()
    {
        if (PreviewLoaded || _load is not null) return;
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _load = load;
        _status.Text = "正在加载图片…";
        try
        {
            var data = await _source.LoadAsync(_uri, load.Token).WaitAsync(load.Token); load.Token.ThrowIfCancellationRequested();
            if (data.Bytes.Length is 0 or > RemoteImageData.MaxBytes) throw new InvalidDataException();
            using var bytes = new MemoryStream(data.Bytes, writable: false); using var stream = bytes.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(load.Token);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 64_000_000)
                throw new InvalidDataException();
            var bitmap = new BitmapImage();
            if (decoder.PixelWidth >= decoder.PixelHeight) bitmap.DecodePixelWidth = (int)Math.Min(1280, decoder.PixelWidth);
            else bitmap.DecodePixelHeight = (int)Math.Min(1280, decoder.PixelHeight);
            stream.Seek(0); await bitmap.SetSourceAsync(stream).AsTask(load.Token); load.Token.ThrowIfCancellationRequested();
            _image.Source = bitmap; _status.Text = $"{decoder.PixelWidth} × {decoder.PixelHeight}";
        }
        catch (OperationCanceledException) { }
        catch { _status.Text = "图片暂时无法预览。请收起后重试。"; }
        finally
        {
            if (ReferenceEquals(_load, load)) _load = null;
            if (load.IsCancellationRequested && !_lifetime.IsCancellationRequested && IsLoaded && _expander.IsExpanded)
                _ = LoadAsync();
        }
    }
    private void Release() { _load?.Cancel(); _image.Source = null; _status.Text = "展开后从图片网站加载"; }
}
