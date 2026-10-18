using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;

namespace PuddingSshTests;

/// <summary>
/// F3/F4 回归（2026-10-18 独立审阅）：
/// <list type="bullet">
///   <item>F3：能力探测必须是**提交前的有界阶段**（worker/deadline/租约/错误分类）；结果阶段只读快照；
///         下载不执行远端探测（不为诊断额外写远端）。</item>
///   <item>F4：取消后的清理残留证据必须合并（保留 Cancelled/TimedOut 终态；未知残留要显式 Unknown）。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SshTransferLifecycleStageTests
{
    private string _workspace = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "pudding-f3f4-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static SshTransferLimits Limits(string localRoot) => new()
    {
        LocalRoot = localRoot,
        UploadRoot = "/srv/upload",
        DownloadRoot = "/srv/download",
        MaxBytes = 1024 * 1024,
    };

    private SshUploadRequest Upload()
    {
        var local = Path.Combine(_workspace, "src.bin");
        File.WriteAllText(local, "payload");
        return new SshUploadRequest(FakeSsh.Target(), local, "/srv/upload/a.bin");
    }

    private SshDownloadRequest Download() =>
        new(FakeSsh.Target(), "/srv/download/a.bin", Path.Combine(_workspace, "out.bin"));

    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 6000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回");
        return await operation;
    }

    private static SshOperationCoordinator Coordinator(FakeTransferFactory factory, SshCoordinatorOptions options) =>
        new(new FakeTransportFactory(), new FakeIdentityProvider(), options, transferFactory: factory);

    // ---------- F3 ----------

    [TestMethod]
    public async Task Upload_CapabilityProbeHanging_IsBoundedAndNeverCompletes()
    {
        var probeGate = new TaskCompletionSource<SshTransferCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTransferSession { OnGetCapabilities = _ => probeGate.Task };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };

        await using var coordinator = Coordinator(
            factory,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80) });

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(
            coordinator.UploadAsync(
                FakeSsh.Host(transfer: Limits(_workspace), deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
                Upload(),
                CancellationToken.None));
        stopwatch.Stop();

        Assert.IsLessThan(2000, (int)stopwatch.ElapsedMilliseconds, "能力探测不得越过 deadline/清理宽限");
        Assert.AreNotEqual(SshExecutionStatus.Completed, result.Status, "探测未返回时不得报 Completed");
        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        Assert.IsNull(result.Capabilities, "结果阶段只能读快照；未测到就是 null");
        Assert.AreEqual(0, session.UploadCount, "能力未就绪时不得开始传输");

        // 解除探测等待：结果已经返回，不得被迟到完成改写。
        probeGate.TrySetResult(new SshTransferCapabilities(true, true, true));
        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(() => coordinator.ActiveStageWorkers == 0, 5000),
            "探测 worker 最终必须归还");
        Assert.AreNotEqual(SshExecutionStatus.Completed, result.Status);
    }

    [TestMethod]
    public async Task Upload_CapabilityProbeThrows_IsStructuredFailureWithoutEscape()
    {
        var session = new FakeTransferSession
        {
            OnGetCapabilities = _ => throw new UnauthorizedAccessException("scratch write denied"),
        };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };

        await using var coordinator = Coordinator(factory, new SshCoordinatorOptions());

        var result = await WaitOrFailAsync(coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Upload(), CancellationToken.None));

        Assert.AreEqual(SshErrorCodes.TransferFailed, result.ErrorCode);
        StringAssert.Contains(result.Diagnostic ?? string.Empty, "exception=UnauthorizedAccessException");
        Assert.AreEqual(0, session.UploadCount, "探测失败时不得继续传输");
    }

    [TestMethod]
    public async Task Download_DoesNotProbeCapabilities()
    {
        var session = new FakeTransferSession();
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };

        await using var coordinator = Coordinator(factory, new SshCoordinatorOptions());

        var result = await WaitOrFailAsync(coordinator.DownloadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Download(), CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.AreEqual(0, session.CapabilityProbeCount, "下载不需要远端原子写能力，不得为诊断额外探测");
        Assert.IsNull(result.Capabilities, "未探测时结果不带能力快照");
    }

    // ---------- F4 ----------

    [TestMethod]
    public async Task Upload_Cancelled_MergesLateResidueEvidence_AndKeepsTermination()
    {
        var uploadCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTransferSession
        {
            OnUpload = async (_, _, _) =>
            {
                uploadCalled.TrySetResult();
                await lateGate.Task.ConfigureAwait(false);
                return SshTransferOutcome.Cancelled(
                    4,
                    "清理失败：残留",
                    tempLeftBehind: true,
                    tempPath: "/srv/upload/a.bin");
            },
        };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };

        await using var coordinator = Coordinator(
            factory,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(600) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Upload(), cts.Token);
        await uploadCalled.Task.ConfigureAwait(false);
        await cts.CancelAsync();

        // 在清理宽限内交还"迟到"的传输结果（含残留证据）。
        lateGate.TrySetResult();
        var result = await WaitOrFailAsync(operation);

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status, "取消终态必须保留");
        Assert.AreEqual(4L, result.BytesTransferred, "必须合并已完成传输的字节数");
        Assert.AreEqual(SshTemporaryResidue.LeftBehind, result.TemporaryResidue, "残留证据必须如实表达");
        Assert.AreEqual("/srv/upload/a.bin", result.TemporaryPath);
    }

    [TestMethod]
    public async Task Upload_Cancelled_ResidueUnknownWhenLateResultExceedsBudget()
    {
        var uploadCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<SshTransferOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTransferSession
        {
            OnUpload = async (_, _, _) =>
            {
                uploadCalled.TrySetResult();
                return await never.Task.ConfigureAwait(false);
            },
        };
        var factory = new FakeTransferFactory { OnConnect = (_, _) => Task.FromResult<ISshTransferSession>(session) };

        await using var coordinator = Coordinator(
            factory,
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(60) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.UploadAsync(FakeSsh.Host(transfer: Limits(_workspace)), Upload(), cts.Token);
        await uploadCalled.Task.ConfigureAwait(false);
        await cts.CancelAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(operation);
        stopwatch.Stop();

        Assert.IsLessThan(2000, (int)stopwatch.ElapsedMilliseconds);
        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(
            SshTemporaryResidue.Unknown,
            result.TemporaryResidue,
            "到期仍不知道是否残留时必须显式 Unknown，不得默认没有残留");
    }
}
