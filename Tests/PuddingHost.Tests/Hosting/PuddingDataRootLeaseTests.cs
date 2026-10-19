using System.Diagnostics;
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
            {
                var error = Assert.Throws<IOException>(
                    () => new PuddingDataRootLease(root, TimeSpan.FromMilliseconds(100)));
                Assert.Contains("独占租约", error.Message);
                Assert.Contains(PuddingDataRootLease.LockFileName, error.Message);
            }
            Assert.True(File.Exists(Path.Combine(root, PuddingDataRootLease.LockFileName)));
            using var next = new PuddingDataRootLease(root);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AcquireWaitsForReleasingInstance()
    {
        // 重启窗口内旧 Core 正在退出：新 Core 不得因瞬时租约占用而崩溃，应等待旧实例释放后继续启动。
        var root = Path.Combine(Path.GetTempPath(), "pudding-recovery-lease", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new PuddingDataRootLease(root);
            var acquireTask = Task.Run(() => new PuddingDataRootLease(root, TimeSpan.FromSeconds(10)));
            await Task.Delay(500);

            Assert.False(acquireTask.IsCompleted);
            first.Dispose();

            using var second = await acquireTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(Path.Combine(root, PuddingDataRootLease.LockFileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TimeoutReportsHowLongItWaited()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-recovery-lease", Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new PuddingDataRootLease(root);
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<IOException>(
                () => new PuddingDataRootLease(root, TimeSpan.FromMilliseconds(300)));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250));
            Assert.Contains("DataRoot 已被另一个 Pudding Core 实例占用", error.Message);
            Assert.NotNull(error.InnerException);
        }
        finally { Directory.Delete(root, true); }
    }
}
