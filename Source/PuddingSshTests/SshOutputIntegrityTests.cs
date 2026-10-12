using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Validation;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// R4 回归（ADR-096 D4/D5）：**输出完整性**与**公开诊断有界**。
/// <list type="bullet">
///   <item>EOF / 读取失败 / 被中止是三个不同事实；读异常不得伪装成正常 EOF。</item>
///   <item>只有「退出码 0 + 两条流读完 + 无执行失败」才是 Completed；输出不完整要有独立错误码并保留退出码。</item>
///   <item>预算截断 ≠ 输出不完整；清理状态是结构化字段，而不是诊断文字。</item>
///   <item>公开诊断不得携带第三方异常 Message、known_hosts 整行等原始载荷，且有长度上限。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SshOutputIntegrityTests
{
    // ---------- 采集器：三个事实分离 ----------

    [TestMethod]
    public async Task NormalEndOfStream_IsComplete()
    {
        var collector = new SshBoundedOutputCollector(64);
        var stdout = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var stderr = new MemoryStream(Encoding.UTF8.GetBytes("warn"));

        await Task.WhenAll(collector.DrainStdoutAsync(stdout), collector.DrainStderrAsync(stderr));

        Assert.IsTrue(collector.ReachedEnd);
        Assert.IsFalse(collector.AnyReadFailed);
        Assert.IsFalse(collector.AnyAborted);
        Assert.IsNull(collector.IncompleteReason);
    }

    [TestMethod]
    public async Task ReadFailure_IsNotReportedAsEndOfStream()
    {
        var collector = new SshBoundedOutputCollector(64);
        var stream = new FailingStream(Encoding.UTF8.GetBytes("hello"));

        var result = await collector.DrainStdoutAsync(stream);

        Assert.IsFalse(result.ReachedEnd, "读取失败不得被当作读完");
        Assert.IsTrue(result.ReadFailed);
        Assert.IsFalse(result.Aborted);
        Assert.IsFalse(collector.ReachedEnd);
        Assert.AreEqual(SshOutputIncompleteReasons.ReadFailed, collector.IncompleteReason);
    }

    [TestMethod]
    public async Task AbortedStream_IsReportedAsAborted_NotAsFailure()
    {
        var collector = new SshBoundedOutputCollector(64);
        var stream = new DisposedStream();

        var result = await collector.DrainStdoutAsync(stream);

        Assert.IsFalse(result.ReachedEnd);
        Assert.IsFalse(result.ReadFailed, "本端中止不是「读取失败」");
        Assert.IsTrue(result.Aborted);
        Assert.AreEqual(SshOutputIncompleteReasons.Aborted, collector.IncompleteReason);
    }

    [TestMethod]
    public async Task BudgetTruncation_StillCountsAsCompleteOutput()
    {
        var collector = new SshBoundedOutputCollector(4);
        var stdout = new MemoryStream(Encoding.ASCII.GetBytes(new string('a', 100)));

        // 聚合语义是「两条流都读到 EOF」，所以这里也要排空 stderr（空流）。
        await Task.WhenAll(collector.DrainStdoutAsync(stdout), collector.DrainStderrAsync(new MemoryStream()));

        Assert.AreEqual(4, collector.CapturedBytes);
        Assert.IsTrue(collector.IsTruncated, "预算截断要如实标记");
        Assert.IsTrue(collector.ReachedEnd, "预算截断 ≠ 输出不完整：仍然读到了 EOF");
        Assert.IsNull(collector.IncompleteReason);
    }

    // ---------- 结果判定策略 ----------

    [TestMethod]
    public void ExitZeroWithCompleteOutput_IsCompleted()
    {
        var decision = SshOutcomePolicy.Decide(false, SshExecutionStatus.Completed, null, 0, null, true, false, false);

        Assert.AreEqual(SshExecutionStatus.Completed, decision.Status);
        Assert.IsNull(decision.ErrorCode);
    }

    [TestMethod]
    public void ExitZeroWithIncompleteOutput_IsFailedAndKeepsTheExitCodeFact()
    {
        var decision = SshOutcomePolicy.Decide(false, SshExecutionStatus.Completed, null, 0, null, false, true, false);

        Assert.AreEqual(SshExecutionStatus.Failed, decision.Status);
        Assert.AreEqual(SshErrorCodes.OutputIncomplete, decision.ErrorCode);
    }

    [TestMethod]
    public void SignalOnlyExit_IsItsOwnError()
    {
        var decision = SshOutcomePolicy.Decide(false, SshExecutionStatus.Completed, null, null, "KILL", true, false, false);

        Assert.AreEqual(SshExecutionStatus.Failed, decision.Status);
        Assert.AreEqual(SshErrorCodes.RemoteExitSignal, decision.ErrorCode);
    }

    [TestMethod]
    public void MissingExitEvidence_IsExitStatusMissing()
    {
        var decision = SshOutcomePolicy.Decide(false, SshExecutionStatus.Completed, null, null, null, true, false, false);

        Assert.AreEqual(SshErrorCodes.ExitStatusMissing, decision.ErrorCode);
    }

    [TestMethod]
    public void NonZeroExit_IsRemoteExitNonzero()
    {
        var decision = SshOutcomePolicy.Decide(false, SshExecutionStatus.Completed, null, 3, null, true, false, false);

        Assert.AreEqual(SshErrorCodes.RemoteExitNonzero, decision.ErrorCode);
    }

    [TestMethod]
    public void ExecutionFailure_IsNotRewrittenByOutputFacts()
    {
        var decision = SshOutcomePolicy.Decide(true, SshExecutionStatus.TimedOut, SshErrorCodes.Timeout, null, null, false, true, true);

        Assert.AreEqual(SshExecutionStatus.TimedOut, decision.Status);
        Assert.AreEqual(SshErrorCodes.Timeout, decision.ErrorCode);
    }

    // ---------- 清理状态是结构化字段 ----------

    [TestMethod]
    public async Task CleanupState_IsCompletedOnNormalClose_AndPendingWhenQuarantined()
    {
        var healthy = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(healthy) };
        await using (var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), new SshCoordinatorOptions()))
        {
            var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
            Assert.AreEqual(SshCleanupState.Completed, result.CleanupState);
        }

        var stuck = new FakeSession { DisposeNeverCompletes = true };
        var stuckFactory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(stuck) };
        await using (var coordinator = new SshOperationCoordinator(
            stuckFactory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(120), MaxQuarantinedResources = 4 }))
        {
            var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
            Assert.AreEqual(SshCleanupState.Pending, result.CleanupState, "清理未结束必须是结构化 Pending，而不是只写在诊断里");
        }
    }

    // ---------- 公开诊断有界、不透传载荷 ----------

    [TestMethod]
    public void KnownHostsUnsupportedReason_CarriesCodeAndLineNumber_NotTheRawLine()
    {
        const string sentinel = "SENTINEL-HOSTNAME-DO-NOT-LEAK";
        var content = "# comment\n" + $"health-check-ok ssh-ed25519 {Convert.ToBase64String(new byte[51])}\n" +
                      $"*{sentinel} ssh-ed25519 {Convert.ToBase64String(new byte[51])}\n";

        var parsed = SshKnownHostsParser.Parse(content);

        Assert.IsFalse(parsed.IsUsable);
        StringAssert.Contains(parsed.UnsupportedReason!, "code=wildcard_pattern");
        StringAssert.Contains(parsed.UnsupportedReason!, "line=3");
        Assert.DoesNotContain(sentinel, parsed.UnsupportedReason!, "解析诊断不得携带原始行内容");
    }

    [TestMethod]
    public void TrustSnapshotError_DoesNotLeakTheRawKnownHostsLine()
    {
        const string sentinel = "SENTINEL-KEY-MATERIAL";
        var content = $"|2|{sentinel}|hash ssh-ed25519 {Convert.ToBase64String(new byte[51])}\n";

        var error = Assert.ThrowsExactly<SshComponentException>(() => SshTrustSnapshot.FromKnownHosts(content));

        Assert.AreEqual(SshErrorCodes.KnownHostsUnsupported, error.ErrorCode);
        Assert.DoesNotContain(sentinel, error.Message, "公开错误不得携带 known_hosts 原始内容");
        StringAssert.Contains(error.Message, "code=hash_version");
    }

    [TestMethod]
    public async Task SessionExceptionMessage_IsNotCopiedIntoPublicDiagnostics()
    {
        const string sentinel = "SENTINEL-THIRD-PARTY-MESSAGE";
        var session = new FakeSession
        {
            OnExecute = (_, _, _, _) => throw new SshConnectionException(sentinel),
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), new SshCoordinatorOptions());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.DoesNotContain(sentinel, result.Diagnostic ?? string.Empty, "诊断只能给稳定因果类别与异常类型名");
        StringAssert.Contains(result.Diagnostic!, "exception=SshConnectionException");
        Assert.AreEqual(SshErrorCodes.ConnectionLost, result.ErrorCode);
    }

    [TestMethod]
    public void DiagnosticText_IsBounded_AndMarksTruncation()
    {
        var longText = new string('x', SshDiagnosticText.MaxLength * 3);

        var bounded = SshDiagnosticText.Bound(longText);

        Assert.IsNotNull(bounded);
        Assert.IsLessThanOrEqualTo(SshDiagnosticText.MaxLength, bounded!.Length);
        StringAssert.EndsWith(bounded, "…(truncated)");
        Assert.IsNull(SshDiagnosticText.Bound(null));
    }

    /// <summary>第一次返回数据、第二次抛 IOException 的流（复刻审阅 F4 的反例形状）。</summary>
    private sealed class FailingStream(byte[] payload) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads++;
            if (_reads == 1)
            {
                var take = Math.Min(buffer.Length, payload.Length);
                payload.AsMemory(0, take).CopyTo(buffer);
                return ValueTask.FromResult(take);
            }

            throw new IOException("channel torn down mid-stream");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>读取即抛 ObjectDisposedException 的流：代表本端中止/关闭。</summary>
    private sealed class DisposedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new ObjectDisposedException("stream");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
