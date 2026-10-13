using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;

namespace PuddingSshTests;

/// <summary>
/// B3 门禁（切片 B）：传输接到协调器后必须**复用**准入/围栏/租约/关闭预算/隔离区——
/// 主机未配置传输限额即关闭式失败，校验失败零副作用，清理不收敛要如实报告。
/// </summary>
[TestClass]
public sealed class SshTransferCoordinatorTests
{
    private string _workspace = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "pudding-b3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static SshTransferLimits Limits(string localRoot, bool upload = true) => new()
    {
        LocalRoot = localRoot,
        UploadRoot = "/srv/upload",
        DownloadRoot = "/srv/download",
        MaxBytes = 1024 * 1024,
    };

    private SshUploadRequest Upload(string remotePath = "/srv/upload/a.bin", SshOverwritePolicy overwrite = SshOverwritePolicy.Fail)
    {
        var local = Path.Combine(_workspace, "src.bin");
        File.WriteAllText(local, "payload");
        return new SshUploadRequest(FakeSsh.Target(), local, remotePath, overwrite);
    }

    private SshDownloadRequest Download(string remotePath = "/srv/download/a.bin") =>
        new(FakeSsh.Target(), remotePath, Path.Combine(_workspace, "out.bin"));

    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 6000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回");
        return await operation;
    }

    [TestMethod]
    public async Task Transfer_WithoutHostTransferLimits_IsUnavailable_WithZeroSideEffects()
    {
        var factory = new FakeTransferFactory();
        var execFactory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(execFactory, identities, new SshCoordinatorOptions(), transferFactory: factory);

        var result = await coordinator.UploadAsync(FakeSsh.Host(), Upload(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferUnavailable, result.ErrorCode);
        Assert.AreEqual(SshPhase.Validation, result.Phase);
        Assert.AreEqual(0, identities.Calls, "未配置传输限额时不得读取身份");
        Assert.AreEqual(0, factory.ConnectCount, "未配置传输限额时不得建连");
    }

    [TestMethod]
    public async Task Upload_HappyPath_CompletesAndReleasesEverything()
    {
        var session = new FakeTransferSession();
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(execFactory, new FakeIdentityProvider(), new SshCoordinatorOptions(), transferFactory: factory);

        var result = await WaitOrFailAsync(coordinator.UploadAsync(
            FakeSsh.Host(transfer: Limits(_workspace)),
            Upload(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.AreEqual(SshPhase.Completed, result.Phase);
        Assert.AreEqual(7L, result.BytesTransferred);
        Assert.AreEqual("/srv/upload/a.bin", result.RemotePath);
        Assert.IsTrue(session.Disposed, "传输会话必须被释放");
        Assert.AreEqual(SshCleanupState.Completed, result.CleanupState);
        Assert.AreEqual(0, coordinator.ActiveLeases);
        Assert.AreEqual(0, coordinator.PendingResourceLeases);
        Assert.AreEqual("/srv/upload", factory.ScratchDirectories[0], "能力探测在允许根内进行");
    }

    [TestMethod]
    public async Task Download_HappyPath_UsesDownloadPlanAndRoot()
    {
        var session = new FakeTransferSession();
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(execFactory, new FakeIdentityProvider(), new SshCoordinatorOptions(), transferFactory: factory);

        var result = await WaitOrFailAsync(coordinator.DownloadAsync(
            FakeSsh.Host(transfer: Limits(_workspace)),
            Download(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.AreEqual(1, session.DownloadCount);
        Assert.AreEqual(0, session.UploadCount);
        Assert.AreEqual("/srv/download", factory.ScratchDirectories[0]);
        Assert.AreEqual(7L, result.BytesTransferred);
    }

    [TestMethod]
    public async Task Upload_LocalPathOutsideRoot_IsLocalPathInvalid_WithZeroSideEffects()
    {
        var factory = new FakeTransferFactory();
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(execFactory, new FakeIdentityProvider(), new SshCoordinatorOptions(), transferFactory: factory);

        var escaping = new SshUploadRequest(FakeSsh.Target(), "../escape.bin", "/srv/upload/a.bin");
        var result = await coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), escaping, CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.LocalPathInvalid, result.ErrorCode);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task Upload_RemotePathOutsideUploadRoot_IsRemotePathInvalid()
    {
        var factory = new FakeTransferFactory();
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(execFactory, new FakeIdentityProvider(), new SshCoordinatorOptions(), transferFactory: factory);

        var result = await coordinator.UploadAsync(
            FakeSsh.Host(transfer: Limits(_workspace)),
            Upload(remotePath: "/srv/download/a.bin"),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, result.ErrorCode);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task Upload_UnsupportedPublish_PassesStableCodeThrough()
    {
        var session = new FakeTransferSession
        {
            OnUpload = (_, _, _) => Task.FromResult(SshTransferOutcome.Failed(SshErrorCodes.TransferUnsupported, 0, "服务端不支持所需发布语义")),
        };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(execFactory, new FakeIdentityProvider(), new SshCoordinatorOptions(), transferFactory: factory);

        var result = await WaitOrFailAsync(coordinator.UploadAsync(
            FakeSsh.Host(transfer: Limits(_workspace)),
            Upload(),
            CancellationToken.None));

        Assert.AreEqual(SshErrorCodes.TransferUnsupported, result.ErrorCode);
        Assert.AreEqual(SshPhase.Cleanup, result.Phase);
        Assert.IsNotNull(result.Capabilities, "结果必须带实测能力（可审计）");
    }

    [TestMethod]
    public async Task Upload_CancelledBeforeStart_HasZeroSideEffects()
    {
        var factory = new FakeTransferFactory();
        var execFactory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(execFactory, identities, new SshCoordinatorOptions(), transferFactory: factory);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Upload(), cts.Token);

        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(0, identities.Calls);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task Upload_SessionThatNeverCloses_IsQuarantinedAndReported()
    {
        var session = new FakeTransferSession { DisposeNeverCompletes = true };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(
            execFactory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(60), MaxQuarantinedResources = 4 },
            transferFactory: factory);

        var result = await WaitOrFailAsync(coordinator.UploadAsync(
            FakeSsh.Host(transfer: Limits(_workspace)),
            Upload(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, "清理问题不得改写业务结果");
        Assert.AreEqual(SshCleanupState.Pending, result.CleanupState, "关闭不收敛必须如实报告 Pending");
        Assert.IsTrue(session.AbortRequested, "预算用尽前必须登记中止请求");
        Assert.IsGreaterThan(0, coordinator.PendingResourceLeases, "未结束的传输资源必须继续占账");

        var report = await WaitOrFailAsync(coordinator.StopAsync());
        Assert.IsGreaterThan(0, report.PendingResources + report.PendingCleanups, "Stop 必须如实报告未收敛资源");
    }

    [TestMethod]
    public async Task Upload_LateConnect_DoesNotRunTransfer()
    {
        var connectGate = new TaskCompletionSource<ISshTransferSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransferFactory { OnConnect = (_, _) => connectGate.Task };
        var execFactory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(
            execFactory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(60) },
            transferFactory: factory);

        using var cts = new CancellationTokenSource();
        var operation = coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Upload(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));
        await Task.Delay(60);
        await cts.CancelAsync();
        var result = await WaitOrFailAsync(operation);

        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);

        var late = new FakeTransferSession();
        connectGate.SetResult(late);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => late.Disposed, 8000), "迟到传输会话必须被回收");
        Assert.AreEqual(0, late.UploadCount, "迟到传输会话绝不能执行上传");
        Assert.AreEqual(0, late.DownloadCount);
    }

    [TestMethod]
    public async Task Upload_InvalidHostLimits_IsConfigInvalid_BeforeAnyTransferWork()
    {
        var factory = new FakeTransferFactory();
        var execFactory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(execFactory, identities, new SshCoordinatorOptions(), transferFactory: factory);

        var host = FakeSsh.Host(transfer: Limits(_workspace), maxConcurrentOperations: 3);
        var result = await coordinator.UploadAsync(host, Upload(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, result.ErrorCode);
        Assert.AreEqual(0, factory.ConnectCount);
        Assert.AreEqual(0, identities.Calls);
    }
}
