using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Core;

public class CoreStartupProgressLeaseTests
{
    [Fact]
    public void TryRenew_OnlyAcceptsMonotonicallyIncreasingSequence()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lease = new CoreStartupProgressLease(startedAt);
        var firstRenewal = startedAt.AddSeconds(5);
        var staleRenewal = startedAt.AddSeconds(10);
        var secondRenewal = startedAt.AddSeconds(15);

        Assert.True(lease.TryRenew(1, firstRenewal));
        Assert.False(lease.TryRenew(1, staleRenewal));
        Assert.False(lease.TryRenew(0, staleRenewal));
        Assert.Equal(firstRenewal, lease.LastProgressAt);

        Assert.True(lease.TryRenew(2, secondRenewal));
        Assert.Equal(secondRenewal, lease.LastProgressAt);
    }
}
