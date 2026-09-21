using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Detaches the SDK's D3DImage from WPF while its surface cannot be presented.
/// Collapsing the control alone leaves the shared texture on the render channel.
/// Browser execution, navigation and automation remain alive.
/// </summary>
public sealed class WebView2PresentationGate : IDisposable
{
    private static readonly DependencyPropertyDescriptor SourceDescriptor =
        DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
    private readonly FrameworkElement _owner;
    private readonly Image _image;
    private ImageSource? _parkedSource;
    private Window? _window;
    private bool _presenting;
    private bool _updating;
    private bool _refreshQueued;
    private bool _disposed;

    internal WebView2PresentationGate(FrameworkElement owner, Image image)
    {
        _owner = owner;
        _image = image;
        SourceDescriptor.AddValueChanged(image, OnSourceChanged);
        owner.IsVisibleChanged += OnVisibilityChanged;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
        AttachWindow();
        Refresh();
    }

    public static IDisposable Attach(WebView2CompositionControl control)
    {
        control.ApplyTemplate();
        // PART_image is the SDK's declared TemplatePart contract, not a private field.
        var image = control.Template.FindName("PART_image", control) as Image
            ?? throw new InvalidOperationException("WebView2 composition template is missing PART_image.");
        return new WebView2PresentationGate(control, image);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) { AttachWindow(); Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) { DetachWindow(); SetPresenting(false); }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();
    private void OnWindowStateChanged(object? sender, EventArgs e) => Refresh();
    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_disposed || _updating || _presenting || _refreshQueued)
            return;
        // A Freezable is still attaching its inheritance context during this callback.
        // Detaching it reentrantly would corrupt that context; coalesce until Render.
        _refreshQueued = true;
        _owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _refreshQueued = false;
            SetPresenting(_presenting);
        });
    }

    private void Refresh() => SetPresenting(_owner.IsLoaded && _owner.IsVisible
        && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized);

    internal void SetPresenting(bool presenting)
    {
        if (_disposed || _updating)
            return;
        _presenting = presenting;
        _updating = true;
        try
        {
            if (!presenting && _image.Source is { } source)
            {
                _parkedSource = source;
                _image.SetCurrentValue(Image.SourceProperty, null);
            }
            else if (presenting && _parkedSource is not null)
            {
                if (_image.Source is null)
                    _image.SetCurrentValue(Image.SourceProperty, _parkedSource);
                _parkedSource = null;
            }
        }
        finally { _updating = false; }
    }

    private void AttachWindow()
    {
        DetachWindow();
        _window = Window.GetWindow(_owner);
        if (_window is null)
            return;
        _window.StateChanged += OnWindowStateChanged;
        _window.IsVisibleChanged += OnVisibilityChanged;
    }

    private void DetachWindow()
    {
        if (_window is null)
            return;
        _window.StateChanged -= OnWindowStateChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SourceDescriptor.RemoveValueChanged(_image, OnSourceChanged);
        _owner.IsVisibleChanged -= OnVisibilityChanged;
        _owner.Loaded -= OnLoaded;
        _owner.Unloaded -= OnUnloaded;
        DetachWindow();
        _parkedSource = null;
    }
}
