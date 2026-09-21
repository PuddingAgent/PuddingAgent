using System.Windows.Controls;
using System.Windows.Media;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Tests.Browser;

public sealed class WebView2PresentationGateTests
{
    [Fact]
    public void HiddenSurface_DetachesAndRestoresTheSameImageWithoutRecreatingIt() => RunSta(() =>
    {
        var source = new DrawingImage();
        var image = new Image { Source = source };
        using var gate = new WebView2PresentationGate(image, image);
        Assert.Null(image.Source);
        gate.SetPresenting(true);
        Assert.Same(source, image.Source);
        gate.SetPresenting(false);
        Assert.Null(image.Source);
        gate.SetPresenting(true);
        Assert.Same(source, image.Source);
    });

    [Fact]
    public void HiddenSurface_ParksLateSdkFramesAndStopsObservingAfterDisposal() => RunSta(() =>
    {
        var image = new Image();
        var gate = new WebView2PresentationGate(image, image);
        var latest = new DrawingImage();
        image.Source = new DrawingImage();
        image.Source = latest;
        image.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Null(image.Source);
        gate.SetPresenting(true);
        Assert.Same(latest, image.Source);
        gate.SetPresenting(false);
        gate.Dispose();
        image.Source = latest;
        Assert.Same(latest, image.Source);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
