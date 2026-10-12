using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// R5 回归（ADR-096 D5 收口）：**限额要么合法、要么拒绝**。
/// <list type="bullet">
///   <item>协调器与主机限额的每一条上限都有 下界 / 上界 / 上界+1 / 0 / 负数 的机器校验。</item>
///   <item>非法配置一律 <c>ssh.config_invalid</c>，并且**零身份、零连接、零 exec**。</item>
///   <item>请求里的 0/负数一律 <c>ssh.request_invalid</c>；合法但超过主机预算的请求只收窄。</item>
///   <item>过期的宿主 deadline 绝不把负数/零额度传给库。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SshLimitsValidationTests
{
    private static SshHostContext HostWith(SshOperationLimits limits, DateTimeOffset? deadline = null) =>
        FakeSsh.Host(limits: limits, deadline: deadline);

    private static async Task<(SshExecuteResult Execute, SshTestResult Test, int Connects, int Identities)> RunWithHostLimitsAsync(
        SshOperationLimits limits)
    {
        var factory = new FakeTransportFactory();
        var session = new FakeSession();
        factory.OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session);
        var identities = new FakeIdentityProvider();

        await using var coordinator = new SshOperationCoordinator(factory, identities, new SshCoordinatorOptions());

        var execute = await coordinator.ExecuteAsync(HostWith(limits), FakeSsh.Execute(), CancellationToken.None);
        var test = await coordinator.TestAsync(HostWith(limits), FakeSsh.Test(), CancellationToken.None);

        return (execute, test, factory.ConnectCount, identities.Calls);
    }

    // ---------- 协调器选项 ----------

    [TestMethod]
    public void CoordinatorOptions_ValidBoundaries_AreAccepted()
    {
        new SshCoordinatorOptions
        {
            CoreMaxConcurrentOperations = 1,
            MaxWaitQueueLength = 0,
            CleanupGrace = TimeSpan.FromMilliseconds(1),
            MaxQuarantinedResources = 0,
            MaxStageWorkers = 1,
        }.Validate();

        new SshCoordinatorOptions
        {
            CoreMaxConcurrentOperations = 8,
            MaxWaitQueueLength = 32,
            CleanupGrace = SshOperationLimits.CleanupGrace,
            MaxQuarantinedResources = SshCoordinatorOptions.MaxQuarantinedResourcesHardLimit,
            MaxStageWorkers = SshCoordinatorOptions.MaxStageWorkersHardLimit,
        }.Validate();
    }

    [TestMethod]
    [DataRow(0, 32, 2000, 8, 8, "core_max_concurrent_operations")]
    [DataRow(9, 32, 2000, 8, 8, "core_max_concurrent_operations")]
    [DataRow(1, -1, 2000, 8, 8, "max_wait_queue_length")]
    [DataRow(1, 33, 2000, 8, 8, "max_wait_queue_length")]
    [DataRow(1, 32, 0, 8, 8, "cleanup_grace")]
    [DataRow(1, 32, 2001, 8, 8, "cleanup_grace")]
    [DataRow(1, 32, 2000, -1, 8, "max_quarantined_resources")]
    [DataRow(1, 32, 2000, 33, 8, "max_quarantined_resources")]
    [DataRow(1, 32, 2000, 8, 0, "max_stage_workers")]
    [DataRow(1, 32, 2000, 8, 33, "max_stage_workers")]
    public void CoordinatorOptions_OutOfRange_AreRejectedWithConfigInvalid(
        int core,
        int queue,
        int cleanupMs,
        int quarantined,
        int workers,
        string expectedField)
    {
        var options = new SshCoordinatorOptions
        {
            CoreMaxConcurrentOperations = core,
            MaxWaitQueueLength = queue,
            CleanupGrace = TimeSpan.FromMilliseconds(cleanupMs),
            MaxQuarantinedResources = quarantined,
            MaxStageWorkers = workers,
        };

        var error = Assert.ThrowsExactly<SshComponentException>(options.Validate);
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, expectedField);
    }

    [TestMethod]
    public void Coordinator_WithInvalidOptions_RefusesToConstruct()
    {
        var options = new SshCoordinatorOptions { CoreMaxConcurrentOperations = 99 };

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => new SshOperationCoordinator(new FakeTransportFactory(), new FakeIdentityProvider(), options));

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }

    // ---------- 主机限额 ----------

    [TestMethod]
    public void OperationLimits_ValidBoundaries_AreAccepted()
    {
        new SshOperationLimits
        {
            ConnectTimeoutSeconds = 1,
            OperationTimeoutSeconds = 1,
            MaxOperationTimeoutSeconds = 1,
            MaxOutputBytes = 1,
            MaxConcurrentOperations = 1,
        }.Validate();

        new SshOperationLimits
        {
            ConnectTimeoutSeconds = 600,
            OperationTimeoutSeconds = 600,
            MaxOperationTimeoutSeconds = 600,
            MaxOutputBytes = SshOperationLimits.MaxOutputBytesHardLimit,
            MaxConcurrentOperations = 2,
        }.Validate();
    }

    [TestMethod]
    [DataRow(0, 60, 600, 65536, 2, "connect_timeout_seconds")]
    [DataRow(601, 60, 600, 65536, 2, "connect_timeout_seconds")]
    [DataRow(15, 0, 600, 65536, 2, "operation_timeout_seconds")]
    [DataRow(15, 601, 600, 65536, 2, "operation_timeout_seconds")]
    [DataRow(15, 60, 0, 65536, 2, "max_operation_timeout_seconds")]
    [DataRow(15, 60, 601, 65536, 2, "max_operation_timeout_seconds")]
    [DataRow(15, 120, 60, 65536, 2, "operation_timeout_seconds>max_operation_timeout_seconds")]
    [DataRow(15, 60, 600, 0, 2, "max_output_bytes")]
    [DataRow(15, 60, 600, 262145, 2, "max_output_bytes")]
    [DataRow(15, 60, 600, 65536, 0, "max_concurrent_operations")]
    [DataRow(15, 60, 600, 65536, 3, "max_concurrent_operations")]
    public void OperationLimits_OutOfRange_AreRejectedWithConfigInvalid(
        int connect,
        int operation,
        int maxOperation,
        int output,
        int concurrency,
        string expectedField)
    {
        var limits = new SshOperationLimits
        {
            ConnectTimeoutSeconds = connect,
            OperationTimeoutSeconds = operation,
            MaxOperationTimeoutSeconds = maxOperation,
            MaxOutputBytes = output,
            MaxConcurrentOperations = concurrency,
        };

        var error = Assert.ThrowsExactly<SshComponentException>(limits.Validate);
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, expectedField);
    }

    [TestMethod]
    public async Task InvalidHostLimits_HaveZeroSideEffectsOnBothEntries()
    {
        var limits = SshOperationLimits.Default with { MaxConcurrentOperations = 3 };

        var (execute, test, connects, identities) = await RunWithHostLimitsAsync(limits);

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, execute.ErrorCode);
        Assert.AreEqual(SshPhase.Validation, execute.Phase);
        Assert.AreEqual(SshExecutionState.NotSubmitted, execute.ExecutionState);
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, test.ErrorCode);
        Assert.AreEqual(0, connects, "非法配置不得建连");
        Assert.AreEqual(0, identities, "非法配置不得读取身份");
    }

    // ---------- 请求校验 ----------

    [TestMethod]
    [DataRow(0, "timeout_seconds")]
    [DataRow(-5, "timeout_seconds")]
    public void ExecuteRequest_NonPositiveTimeout_IsRequestInvalid(int timeoutSeconds, string expectedField)
    {
        var request = FakeSsh.Execute() with { TimeoutSeconds = timeoutSeconds };

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRequestValidator.ValidateExecute(request, SshOperationLimits.Default, "/srv"));

        Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, expectedField);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void ExecuteRequest_NonPositiveOutputBudget_IsRequestInvalid(int maxOutputBytes)
    {
        var request = FakeSsh.Execute() with { MaxOutputBytes = maxOutputBytes };

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRequestValidator.ValidateExecute(request, SshOperationLimits.Default, "/srv"));

        Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);
    }

    [TestMethod]
    public async Task ExecuteRequest_AboveBudget_IsOnlyNarrowed()
    {
        var session = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), new SshCoordinatorOptions());

        var request = FakeSsh.Execute() with { TimeoutSeconds = 5000, MaxOutputBytes = 4 * 1024 * 1024 };
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), request, CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.IsNotNull(session.LastTimeout);
        Assert.IsLessThanOrEqualTo(600d, session.LastTimeout!.Value.TotalSeconds, "超过主机上限的请求只收窄，不拒绝");
        Assert.IsLessThanOrEqualTo(SshOperationLimits.MaxOutputBytesHardLimit, session.LastOutputBudget!.Value);
        Assert.AreEqual(SshOperationLimits.Default.MaxOutputBytes, session.LastOutputBudget!.Value);
    }

    [TestMethod]
    public async Task ExpiredHostDeadline_NeverPassesNonPositiveBudgetToTheLibrary()
    {
        var session = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), new SshCoordinatorOptions());

        var result = await coordinator.ExecuteAsync(
            FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddSeconds(-30)),
            FakeSsh.Execute(),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        Assert.AreEqual(0, factory.ConnectCount, "过期 deadline 不得建连");
        Assert.IsNull(session.LastTimeout, "不得把非正额度传给库");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRequestValidator.ValidateExecute(FakeSsh.Execute(), SshOperationLimits.Default, "/srv", TimeSpan.Zero));
        Assert.AreEqual(SshErrorCodes.Timeout, error.ErrorCode);
    }

    // ---------- 底层防御校验 ----------

    [TestMethod]
    public async Task TransportFactory_RejectsNonPositiveConnectTimeout()
    {
        var factory = new PuddingSsh.Transport.SshNetTransportFactory();
        var request = new SshTransportConnectRequest(
            FakeSsh.Target(),
            FakeSsh.Material(),
            SshTrustSnapshot.FromPin("ssh-ed25519", "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')).CreateVerifier("127.0.0.1", 22),
            TimeSpan.Zero);

        var error = await Assert.ThrowsExactlyAsync<SshComponentException>(
            async () => await factory.ConnectAsync(request, CancellationToken.None));
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }
}
