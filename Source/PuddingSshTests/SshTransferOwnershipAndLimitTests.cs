using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Transport;

namespace PuddingSshTests;

/// <summary>
/// F1/F2 回归（2026-10-18 独立审阅）：
/// <list type="bullet">
///   <item>F1：排他创建失败（目标在检查后被他人创建）时，回滚清理**不得**删除不属于本次的文件；
///         只有"本次成功创建并持有"的文件才可回滚删除。</item>
///   <item>F2：下载的字节上限必须在复制循环内逐次执行；STAT 后远端增长也必须中止并清理。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SshTransferOwnershipAndLimitTests
{
    private string _workspace = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "pudding-f1f2-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string LocalFile(string name, string content)
    {
        var path = Path.Combine(_workspace, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static SshUploadPlan UploadPlan(string localPath, string remotePath = "/srv/upload/target.bin", long maxBytes = 1024 * 1024) =>
        new(localPath, remotePath, "/srv/upload/.target.bin.pudding-tmp-test", "/srv/upload", SshOverwritePolicy.Fail, maxBytes);

    private static SshDownloadPlan DownloadPlan(string remotePath, string localPath, long maxBytes = 1024 * 1024) =>
        new(remotePath, localPath, Path.Combine(Path.GetDirectoryName(localPath)!, "." + Path.GetFileName(localPath) + ".pudding-tmp-test"), "/srv/download", SshOverwritePolicy.Fail, maxBytes);

    private static SshSftpTransferSession Session(FakeSftpChannel channel) =>
        new(channel, "SHA256:test", "/srv/download");

    // ---------- F1 ----------

    [TestMethod]
    public async Task Upload_ExclusiveCreateLosesRace_IsRemotePathExists_AndNeverDeletesOtherClientsTarget()
    {
        // 竞争对手在"存在性检查通过之后、排他创建之前"抢先创建目标。
        // 本次排他创建失败 ⇒ 目标不属于我们 ⇒ 回滚清理绝不能删除它。
        var channel = new FakeSftpChannel();
        channel.OnBeforeCreateNew = path =>
        {
            if (path == "/srv/upload/target.bin")
            {
                channel.Seed(path, "other-client-data");
            }
        };
        var session = Session(channel);
        var local = LocalFile("a.bin", "ours");

        var outcome = await session.UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.RemotePathExists, outcome.ErrorCode, "排他创建失败应报目标已存在");
        Assert.IsFalse(outcome.TemporaryLeftBehind);
        CollectionAssert.DoesNotContain(channel.DeletedPaths, "/srv/upload/target.bin", "不得删除他人创建的目标");
        Assert.AreEqual("other-client-data", channel.ContentOf("/srv/upload/target.bin"), "他人数据必须原样保留");
    }

    [TestMethod]
    public async Task Upload_CreateOutcomeUnknown_DoesNotDeleteTheFile()
    {
        // 创建调用失败但文件可能已经落地：**所有权不可证明**，回滚删除必须放弃。
        var channel = new FakeSftpChannel { FailAfterCreatePath = "/srv/upload/target.bin" };
        var session = Session(channel);
        var local = LocalFile("a.bin", "ours");

        var outcome = await session.UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferFailed, outcome.ErrorCode);
        CollectionAssert.DoesNotContain(channel.DeletedPaths, "/srv/upload/target.bin", "所有权不可证明时不得删除");
        Assert.IsNotNull(channel.ContentOf("/srv/upload/target.bin"), "文件保留在服务端（可能属于他人）");
    }
    [TestMethod]
    public async Task Upload_LocalSourceOpenFailure_LeavesRemoteUntouched()
    {
        var channel = new FakeSftpChannel();
        var session = Session(channel);
        var missing = Path.Combine(_workspace, "does-not-exist.bin");

        _ = await session.GetCapabilitiesAsync(CancellationToken.None);
        var renamesBefore = channel.PosixRenameCount;
        var outcome = await session.UploadAsync(UploadPlan(missing), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferFailed, outcome.ErrorCode);
        CollectionAssert.DoesNotContain(channel.UploadedPaths, "/srv/upload/target.bin", "本地源打不开时不得创建远端目标");
        CollectionAssert.DoesNotContain(channel.DeletedPaths, "/srv/upload/target.bin", "不得删除远端目标");
        Assert.AreEqual(renamesBefore, channel.PosixRenameCount);
    }

    // ---------- F2 ----------

    [TestMethod]
    public async Task Download_RemoteGrowsAfterStat_AbortsAtRuntimeLimit()
    {
        // STAT 报 1 字节（通过事前检查），实际流给出 4096 字节：运行时上限必须中止。
        var channel = new FakeSftpChannel().Seed("/srv/download/src.bin", "x");
        channel.OnOpenRead = _ => new MemoryStream(Encoding.ASCII.GetBytes(new string('g', 4096)));
        var target = Path.Combine(_workspace, "out.bin");

        var outcome = await Session(channel).DownloadAsync(
            DownloadPlan("/srv/download/src.bin", target, maxBytes: 1024),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferTooLarge, outcome.ErrorCode, outcome.Diagnostic ?? "null");
        Assert.IsFalse(File.Exists(target), "超限不得发布本地目标");
        Assert.IsFalse(File.Exists(Path.Combine(_workspace, ".out.bin.pudding-tmp-test")), "超限必须删除本地临时文件");
        Assert.IsFalse(outcome.TemporaryLeftBehind);
    }

    [TestMethod]
    public async Task Download_WithinLimitStillPublishesExactBytes()
    {
        var channel = new FakeSftpChannel().Seed("/srv/download/src.bin", "payload");
        var target = Path.Combine(_workspace, "out.bin");

        var outcome = await Session(channel).DownloadAsync(
            DownloadPlan("/srv/download/src.bin", target, maxBytes: 1024),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, outcome.Status, outcome.ErrorCode ?? "null");
        Assert.AreEqual(7L, outcome.BytesTransferred);
        Assert.AreEqual("payload", File.ReadAllText(target));
    }
}
