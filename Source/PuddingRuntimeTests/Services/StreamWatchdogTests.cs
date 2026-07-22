using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class StreamWatchdogTests
{
    [TestMethod]
    public void FeedSwitchesFromFirstChunkWindowToShorterStreamIdleWindow()
    {
        using var watchdog = new StreamWatchdog(
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(60),
            NullLogger.Instance,
            "test-provider",
            pollIntervalMs: 5);

        watchdog.Start();
        Assert.IsFalse(watchdog.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)));

        watchdog.Feed();

        Assert.IsTrue(
            watchdog.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)),
            "The post-first-chunk idle window should cancel the stalled stream.");
    }

    [TestMethod]
    public void ConstructorRejectsNonPositiveWindows()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StreamWatchdog(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            NullLogger.Instance,
            "test-provider"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StreamWatchdog(
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            NullLogger.Instance,
            "test-provider"));
    }
}
