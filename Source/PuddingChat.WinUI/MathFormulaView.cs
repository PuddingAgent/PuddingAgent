using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PuddingChat.WinUI;

/// <summary>Native formula presentation with a selectable source fallback; no browser or TeX process.</summary>
public sealed class MathFormulaView : UserControl
{
    private static readonly SemaphoreSlim RendererGate = new(1, 1);
    private readonly Image _image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _source = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly bool _inline;
    private readonly UIElement _presentation;
    private CancellationTokenSource? _renderCancellation;
    private long _generation;
    private ElementTheme? _renderTheme;
    public string Latex { get; }
    public bool Rendered { get; private set; }
    public string? RenderError { get; private set; }
    public Task Rendering { get; private set; } = Task.CompletedTask;
    public MathFormulaView(string latex, bool inline = false)
    {
        Latex = latex; _inline = inline; _source.Text = latex;
        _presentation = inline ? _image : new ScrollViewer { Content = _image, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var surface = new Grid(); surface.Children.Add(_source); surface.Children.Add(_presentation);
        Content = surface; ShowSource();
        ToolTipService.SetToolTip(this, latex);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "公式：" + latex);
        var menu = new MenuFlyout(); var copy = new MenuFlyoutItem { Text = "复制 LaTeX" };
        copy.Click += (_, _) =>
        {
            try { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(Latex); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); }
            catch { ShowSource(); Rendered = false; }
        };
        menu.Items.Add(copy); ContextFlyout = menu;
        Loaded += (_, _) =>
        {
            if (_renderCancellation is null || _renderTheme != ActualTheme || (!Rendered && Rendering.IsCompleted)) Rendering = RenderAsync();
        };
        ActualThemeChanged += (_, _) => { if (IsLoaded && _renderTheme != ActualTheme) Rendering = RenderAsync(); };
        // InlineUIContainer can transiently unload/reload its child during line reflow.
        // Release only after that layout turn, if the control is genuinely detached.
        Unloaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) return;
            _generation++; _renderCancellation?.Cancel(); _renderCancellation?.Dispose(); _renderCancellation = null;
            _image.Source = null; Rendered = false; ShowSource();
        });
    }
    private void ShowSource() { _source.Visibility = Visibility.Visible; _presentation.Visibility = Visibility.Collapsed; }
    private async Task RenderAsync()
    {
        var generation = ++_generation;
        _renderTheme = ActualTheme;
        _renderCancellation?.Cancel(); _renderCancellation?.Dispose(); _renderCancellation = new();
        var token = _renderCancellation.Token;
        Rendered = false; RenderError = null; ShowSource();
        if (Latex.Length is 0 or > 4096 || Latex.Count(c => c == '\\') > 128
            || new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) return;
        // Bound recursive groups before invoking the TeX parser.
        var depth = 0;
        foreach (var c in Latex) { if (c == '{' && ++depth > 64) return; if (c == '}') depth = Math.Max(0, depth - 1); }
        var dark = ActualTheme == ElementTheme.Dark;
        try
        {
            var rendered = await Task.Run(async () =>
            {
                await RendererGate.WaitAsync(token);
                try
                {
                    var painter = new CSharpMath.SkiaSharp.MathPainter
                    { LaTeX = Latex, FontSize = _inline ? 14 : 22, TextColor = dark ? SKColors.White : SKColors.Black };
                    if (painter.ErrorMessage is not null) throw new FormatException(painter.ErrorMessage);
                    var bounds = painter.Measure();
                    var padding = _inline ? 2 : 6;
                    var width = (int)Math.Ceiling(bounds.Width + padding * 2); var height = (int)Math.Ceiling(bounds.Height + padding * 2);
                    if (width is <= 0 or > 2048 || height is <= 0 or > 1024) throw new FormatException("Formula dimensions exceed the rendering limit.");
                    using var bitmap = new SKBitmap(width * 2, height * 2);
                    using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent); canvas.Scale(2);
                    painter.Draw(canvas, padding, padding - bounds.Top);
                    using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    return (encoded.ToArray(), (double)width, (double)height);
                }
                finally { RendererGate.Release(); }
            }, token);
            if (generation != _generation || !IsLoaded) return;
            using var stream = new MemoryStream(rendered.Item1);
            var bitmapImage = new BitmapImage(); await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation != _generation || !IsLoaded) return;
            _image.Source = bitmapImage; _image.Width = rendered.Item2; _image.Height = rendered.Item3;
            _image.Stretch = Stretch.Fill;
            _source.Visibility = Visibility.Collapsed; _presentation.Visibility = Visibility.Visible;
            Rendered = true;
        }
        catch (Exception error) { if (generation == _generation) { ShowSource(); Rendered = false; RenderError = error.Message; } }
    }
}
