using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public sealed class PuddingDataRootLeaseTests
{
    [Fact]
    public void CompetingHostCannotOpenSameRootAndReleasedLeaseCanBeReused()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-recovery-lease", Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new PuddingDataRootLease(root))
                Assert.Throws<IOException>(() => new PuddingDataRootLease(root));
            Assert.True(File.Exists(Path.Combine(root, ".pudding-host.lock")));
            using var next = new PuddingDataRootLease(root);
        }
        finally { Directory.Delete(root, true); }
    }
}
