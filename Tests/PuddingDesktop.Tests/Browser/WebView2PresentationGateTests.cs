using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Tests.Browser;

public sealed class WebView2PresentationGateTests
{
    /// <summary>
    /// watchdog（探针）：只用于在**真的挂住**时给出可诊断失败，不承担"够不够快"的验收职责。
    /// 本文件原先用 `thread.Join(10s)` 与"排一个 ApplicationIdle 空回调就假定前一步已发生"两种
    /// 时间型判据，是在负载下会被登记为 flake 的根因。
    /// </summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(60);

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

        // **状态等待，而不是"排个空回调"**：门在 Source 变化时把停放动作以 Render 优先级排队，
        // 而消息只有在派发器被泵时才会跑。原来的写法只投递一个 ApplicationIdle 空回调就假定
        // 停放已经发生 —— 负载下它就是假失败来源。这里改为泵消息直到**观察到的状态**成立。
        PumpUntil(image, () => image.Source is null);

        gate.SetPresenting(true);
        Assert.Same(latest, image.Source);
        gate.SetPresenting(false);
        gate.Dispose();
        image.Source = latest;
        Assert.Same(latest, image.Source);
    });

    /// <summary>泵 STA 派发器直到谓词成立；超过 watchdog 才失败（并说明是挂住而非变慢）。</summary>
    private static void PumpUntil(DispatcherObject owner, Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + Watchdog;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The presentation gate did not reach the expected state within the {Watchdog.TotalSeconds:0}s "
                    + "watchdog; this indicates a hang, not slowness.");
            }

            // 以低优先级投递并等待：既泵消息（让 Render 优先级的停放回调先跑），又不与它抢优先级。
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(initialState: false);
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!done.Wait(Watchdog))
        {
            throw new TimeoutException(
                $"The STA test thread did not finish within the {Watchdog.TotalSeconds:0}s watchdog; "
                + "this indicates a hang, not slowness.");
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
