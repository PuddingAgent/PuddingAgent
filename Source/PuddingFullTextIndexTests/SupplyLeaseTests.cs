using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A5（断言）与文件租约单元：外部 owner 持锁 ⇒ <c>Busy</c> 且不构建；过期租约 ⇒ 可接管并**记录接管原因**；
/// 并给出反向对照（未过期不得接管）、OS 级独占证据、续期/释放语义。
/// </summary>
[TestClass]
public sealed class SupplyLeaseTests
{
    [TestMethod]
    public async Task A5a_External_Lease_Holder_Makes_Build_Busy_Without_Running_The_Builder()
    {
        using var fixture = new TempSupplyFixture();
        var lease = new FileSupplyLease(fixture.Options);
        var external = new SupplyLeaseOwner("external-agent#9999", 9999, "OTHER-MACHINE");

        var held = await lease.TryAcquireAsync(fixture.ScopeKey, external, "external-job");
        Assert.IsTrue(held.Acquired, held.Message);

        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, lease);

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));

        Assert.AreEqual(SupplyOutcome.Busy, outcome.Outcome);
        Assert.IsNotNull(outcome.Holder, "Busy 必须带 holder 信息");
        Assert.AreEqual("external-agent#9999", outcome.Holder!.OwnerId);
        Assert.AreEqual(9999, outcome.Holder.ProcessId);
        Assert.AreEqual("OTHER-MACHINE", outcome.Holder.MachineName);
        Assert.AreEqual("external-job", outcome.Holder.JobId);
        Assert.AreNotEqual(default, outcome.Holder.StartedAtUtc, "Busy 必须给出占用者的开始时间");
        Assert.IsFalse(outcome.Holder.IsExpired);
        StringAssert.Contains(outcome.Reason!, "external-agent#9999");

        Assert.AreEqual(0, builder.BuildCallCount, "Busy 时绝不能执行构建");
        Assert.AreEqual(0, (await coordinator.ListStatusAsync()).Count, "Busy 不得产生 job");

        var entries = SupplyTestHelpers.CaptureEntries(fixture.IndexRoot);
        Assert.IsTrue(
            entries.All(e => e.StartsWith(".supply-leases", StringComparison.Ordinal)),
            "Busy 时索引根下只允许存在租约文件，不得写任何索引产物：" + string.Join(", ", entries));
    }

    [TestMethod]
    public async Task A5b_Expired_Lease_Is_Taken_Over_And_The_Reason_Is_Recorded()
    {
        using var fixture = new TempSupplyFixture();
        var t0 = new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
        var external = new SupplyLeaseOwner("external-agent#9999", 9999, "OTHER-MACHINE");

        var stale = await new FileSupplyLease(fixture.Options, utcNow: () => t0)
            .TryAcquireAsync(fixture.ScopeKey, external, "external-job");
        Assert.IsTrue(stale.Acquired, stale.Message);

        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            await releaseBuilder.Task.WaitAsync(ct);
            return SupplyTestHelpers.Success();
        });

        // 协调器时钟推进 3 分钟 > 默认有效期 2 分钟 ⇒ 旧租约已过期，可接管
        var coordinatorLease = new FileSupplyLease(fixture.Options, utcNow: () => t0.AddMinutes(3));
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, coordinatorLease);

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var scopeOutcome = SupplyTestHelpers.SingleScopeOutcome(outcome);

        Assert.AreEqual(SupplyOutcome.Started, scopeOutcome.Outcome, "过期租约必须可被接管，而不是 Busy");
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var leaseFile = FileSupplyLease.ResolveLeaseFilePath(fixture.Options, fixture.ScopeKey);
        Assert.IsTrue(File.Exists(leaseFile), $"接管后租约文件必须存在：{leaseFile}");
        var document = JsonSerializer.Deserialize<SupplyLeaseDocument>(File.ReadAllText(leaseFile))!;
        Assert.AreEqual("external-agent#9999", document.PreviousOwnerId, "必须记录被接管的上一位持有者");
        Assert.IsNotNull(document.TakeoverReason, "必须记录接管原因");
        StringAssert.Contains(document.TakeoverReason!, "接管过期租约");

        var status = await coordinator.GetStatusAsync(scopeOutcome.JobId!);
        Assert.IsNotNull(status!.LeaseHolder);
        StringAssert.Contains(status.LeaseHolder!.TakeoverReason!, "接管过期租约");

        Assert.AreEqual(1, builder.BuildCallCount, "接管后构建执行一次");

        releaseBuilder.TrySetResult();
        await SupplyTestHelpers.AwaitJobAsync(coordinator, scopeOutcome.JobId!);
    }

    [TestMethod]
    public async Task A5_Control_Unexpired_Lease_Is_Not_Taken_Over_Under_The_Same_Clock_Skew()
    {
        using var fixture = new TempSupplyFixture();
        var t0 = new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
        var external = new SupplyLeaseOwner("external-agent#9999", 9999, "OTHER-MACHINE");

        Assert.IsTrue((await new FileSupplyLease(fixture.Options, utcNow: () => t0)
            .TryAcquireAsync(fixture.ScopeKey, external, "external-job")).Acquired);

        var builder = new StubSupplyBuilder();
        // 时钟前进 30 秒 < 有效期 2 分钟 ⇒ 不得接管（证明阈值真的在生效，A5b 不是恒真）
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(fixture.Options, utcNow: () => t0.AddSeconds(30)));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));

        Assert.AreEqual(SupplyOutcome.Busy, outcome.Outcome);
        Assert.AreEqual(0, builder.BuildCallCount);
    }

    [TestMethod]
    public async Task Lease_File_Lives_Under_Supply_Leases_With_Sha256_Of_The_ScopeKey()
    {
        using var fixture = new TempSupplyFixture();
        var lease = new FileSupplyLease(fixture.Options);

        var acquired = await lease.TryAcquireAsync(fixture.ScopeKey, SupplyLeaseOwner.ForCurrentProcess(), "job-xyz");
        Assert.IsTrue(acquired.Acquired, acquired.Message);

        var expectedName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.ScopeKey))) + ".json";
        var expectedPath = Path.Combine(fixture.IndexRoot, ".supply-leases", expectedName);
        Assert.AreEqual(expectedPath, FileSupplyLease.ResolveLeaseFilePath(fixture.Options, fixture.ScopeKey));
        Assert.IsTrue(File.Exists(expectedPath), $"租约文件必须落在 <IndexRoot>/.supply-leases/<sha256(scopeKey)>.json：{expectedPath}");
        Assert.AreEqual(64, Path.GetFileNameWithoutExtension(expectedPath).Length, "文件名必须是 sha256 十六进制");

        var document = JsonSerializer.Deserialize<SupplyLeaseDocument>(File.ReadAllText(expectedPath))!;
        Assert.AreEqual(fixture.ScopeKey, document.ScopeKey);
        Assert.AreEqual("job-xyz", document.JobId);
        Assert.AreEqual(Environment.ProcessId, document.ProcessId);
        Assert.AreEqual(Environment.MachineName, document.MachineName);
        Assert.IsNull(document.TakeoverReason, "首次取得不得有接管原因");
        Assert.IsNull(document.PreviousOwnerId);
        Assert.AreEqual(document.StartedAtUtc, document.HeartbeatUtc);
    }

    [TestMethod]
    public async Task Renew_Refreshes_Heartbeat_And_Release_Rejects_Foreign_Owner()
    {
        using var fixture = new TempSupplyFixture();
        var t0 = new DateTimeOffset(2026, 9, 25, 2, 0, 0, TimeSpan.Zero);
        var now = t0;
        var lease = new FileSupplyLease(fixture.Options, utcNow: () => now);
        var owner = SupplyLeaseOwner.ForCurrentProcess();

        var acquired = await lease.TryAcquireAsync(fixture.ScopeKey, owner, "job-1");
        Assert.IsTrue(acquired.Acquired, acquired.Message);
        Assert.AreEqual(t0, acquired.Lease!.HeartbeatUtc);

        now = t0.AddSeconds(50);
        Assert.IsTrue(await lease.RenewAsync(fixture.ScopeKey, owner.OwnerId), "持有者必须能续期");
        var holder = await lease.DescribeHolderAsync(fixture.ScopeKey);
        Assert.IsNotNull(holder);
        Assert.AreEqual(t0.AddSeconds(50), holder!.HeartbeatUtc, "续期必须刷新心跳");
        Assert.AreEqual(t0, holder.StartedAtUtc, "续期不得改写取得时刻");
        Assert.IsFalse(holder.IsExpired);

        Assert.IsFalse(await lease.RenewAsync(fixture.ScopeKey, "someone-else"), "非持有者不得续期");
        Assert.IsFalse(await lease.ReleaseAsync(fixture.ScopeKey, "someone-else"), "非持有者不得释放");
        Assert.IsTrue(
            File.Exists(FileSupplyLease.ResolveLeaseFilePath(fixture.Options, fixture.ScopeKey)),
            "越权释放不得删掉别人的租约");

        Assert.IsTrue(await lease.ReleaseAsync(fixture.ScopeKey, owner.OwnerId));
        Assert.IsFalse(File.Exists(FileSupplyLease.ResolveLeaseFilePath(fixture.Options, fixture.ScopeKey)));
        Assert.IsNull(await lease.DescribeHolderAsync(fixture.ScopeKey));
    }

    [TestMethod]
    public async Task Exclusive_Open_Of_The_Lease_File_Blocks_Acquire_Until_Closed()
    {
        using var fixture = new TempSupplyFixture();
        var lease = new FileSupplyLease(fixture.Options);
        var owner = new SupplyLeaseOwner("owner-a#111", 111, "MACHINE-1");

        Assert.IsTrue((await lease.TryAcquireAsync(fixture.ScopeKey, owner, "job-a")).Acquired);

        var path = FileSupplyLease.ResolveLeaseFilePath(fixture.Options, fixture.ScopeKey);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await lease.TryAcquireAsync(
                fixture.ScopeKey,
                new SupplyLeaseOwner("owner-b#222", 222, "MACHINE-2"),
                "job-b");

            Assert.IsFalse(blocked.Acquired, "OS 级独占句柄在场时必须拿不到租约（跨进程同语义）");
            StringAssert.Contains(blocked.Message, "独占");
        }

        // 句柄释放后：同一 owner 重入取租约应成功，且**不**记为接管
        var reentrant = await lease.TryAcquireAsync(fixture.ScopeKey, owner, "job-a");
        Assert.IsTrue(reentrant.Acquired);
        Assert.IsNull(reentrant.Lease!.TakeoverReason);
        Assert.AreEqual(owner.OwnerId, reentrant.Lease.Owner.OwnerId);
    }
}
