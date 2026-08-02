using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Runtime;

public sealed class CoreRestartAttemptWindowTests
{
    [Fact]
    public void TryRegister_OpensAfterConfiguredAttemptsInsideWindow()
    {
        var window = new CoreRestartAttemptWindow(3, TimeSpan.FromSeconds(60));
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.True(window.TryRegister(now, out var first));
        Assert.True(window.TryRegister(now.AddSeconds(10), out var second));
        Assert.True(window.TryRegister(now.AddSeconds(20), out var third));
        Assert.False(window.TryRegister(now.AddSeconds(30), out var rejected));

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.Equal(3, rejected);
    }

    [Fact]
    public void TryRegister_DropsAttemptsOutsideWindow()
    {
        var window = new CoreRestartAttemptWindow(2, TimeSpan.FromSeconds(60));
        var now = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.True(window.TryRegister(now, out _));
        Assert.True(window.TryRegister(now.AddSeconds(1), out _));
        Assert.True(window.TryRegister(now.AddMilliseconds(60_500), out var attempt));

        Assert.Equal(2, attempt);
    }

    [Fact]
    public void GetDelay_UsesCappedExponentialBackoff()
    {
        var policy = new CoreRestartPolicy
        {
            InitialDelaySeconds = 2,
            MaxDelaySeconds = 5,
        };

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(8));
    }
}
