using System.Net.Sockets;
using PuddingSsh.Diagnostics;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// 错误分类器（设计 §10）：输入**异常类别 + 执行事实**，输出稳定错误码与重试安全性。
/// 断言刻意覆盖「同一种异常在提交前后语义不同」与「重试安全性矩阵」两条最容易退化的契约。
/// </summary>
[TestClass]
public sealed class SshFailureClassifierTests
{
    private static SshFailure Classify(Exception exception, bool submitted, SshPhase phase = SshPhase.Executing) =>
        SshFailureClassifier.Classify(exception, new SshFailureFacts(phase, submitted));

    [TestMethod]
    public void ComponentException_KeepsItsStableCode()
    {
        var failure = Classify(new SshComponentException(SshErrorCodes.PassphraseRequired), submitted: false);

        Assert.AreEqual(SshErrorCodes.PassphraseRequired, failure.ErrorCode);
        Assert.IsFalse(failure.RetrySafe);
        Assert.AreEqual(SshPhase.Executing, failure.Phase);
    }

    [TestMethod]
    public void HostKeyRejection_IsNeverRetrySafe()
    {
        foreach (var code in new[]
                 {
                     SshErrorCodes.HostKeyUntrusted,
                     SshErrorCodes.HostKeyMismatch,
                     SshErrorCodes.HostKeyRevoked,
                 })
        {
            var failure = Classify(new SshHostKeyRejectedException(code), submitted: false);
            Assert.AreEqual(code, failure.ErrorCode);
            Assert.IsFalse(failure.RetrySafe, $"{code} 需要用户核对，不得标记为可重试");
        }
    }

    [TestMethod]
    public void Authentication_And_Passphrase_HaveTheirOwnCodes()
    {
        Assert.AreEqual(
            SshErrorCodes.AuthenticationFailed,
            Classify(new SshAuthenticationException("denied"), submitted: false).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.PassphraseRequired,
            Classify(new SshPassPhraseNullOrEmptyException("encrypted"), submitted: false).ErrorCode);
    }

    [TestMethod]
    public void Timeout_And_Cancellation_AreDistinct()
    {
        Assert.AreEqual(
            SshErrorCodes.Timeout,
            Classify(new SshOperationTimeoutException("slow"), submitted: true).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.Timeout,
            Classify(new TimeoutException(), submitted: true).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.Cancelled,
            Classify(new OperationCanceledException(), submitted: true).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.Cancelled,
            Classify(new TaskCanceledException(), submitted: true).ErrorCode);
    }

    [TestMethod]
    public void ConnectionFailure_DependsOnWhetherTheCommandWasSubmitted()
    {
        var before = Classify(new SshConnectionException("refused"), submitted: false);
        Assert.AreEqual(SshErrorCodes.ConnectFailed, before.ErrorCode);
        Assert.IsTrue(before.RetrySafe, "确定未提交的连通失败可安全再次尝试");

        var after = Classify(new SshConnectionException("reset"), submitted: true);
        Assert.AreEqual(SshErrorCodes.ConnectionLost, after.ErrorCode);
        Assert.IsFalse(after.RetrySafe, "命令已提交后断线可能已执行，不得自动重试");
    }

    [TestMethod]
    [DataRow(SocketError.HostNotFound, SshErrorCodes.DnsFailed)]
    [DataRow(SocketError.TryAgain, SshErrorCodes.DnsFailed)]
    [DataRow(SocketError.NoData, SshErrorCodes.DnsFailed)]
    [DataRow(SocketError.ConnectionRefused, SshErrorCodes.ConnectFailed)]
    [DataRow(SocketError.TimedOut, SshErrorCodes.ConnectFailed)]
    public void SocketErrors_SplitDnsFromConnect(SocketError socketError, string expected)
    {
        var exception = new SocketException((int)socketError);

        var failure = Classify(exception, submitted: false);

        Assert.AreEqual(expected, failure.ErrorCode);
        Assert.IsTrue(failure.RetrySafe, "解析/连通失败在未提交时可安全再次尝试");
    }

    [TestMethod]
    public void CredentialPhase_IoAndFormatProblems_AreCredentialErrors()
    {
        Assert.AreEqual(
            SshErrorCodes.CredentialUnavailable,
            Classify(new FormatException("bad key"), submitted: false, SshPhase.Credentials).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.CredentialUnavailable,
            Classify(new IOException("denied"), submitted: false, SshPhase.Credentials).ErrorCode);
    }

    [TestMethod]
    public void NonCredentialPhase_IoProblems_AreTransportProblems()
    {
        Assert.AreEqual(
            SshErrorCodes.TransportFault,
            Classify(new IOException("pipe broken"), submitted: false, SshPhase.Connecting).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.ConnectionLost,
            Classify(new IOException("pipe broken"), submitted: true, SshPhase.Collecting).ErrorCode);
    }

    [TestMethod]
    public void GenericSshFailure_And_UnknownException_MapToTransportFault()
    {
        Assert.AreEqual(
            SshErrorCodes.TransportFault,
            Classify(new SshException("weird"), submitted: false).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.ConnectionLost,
            Classify(new SshException("weird"), submitted: true).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.TransportFault,
            Classify(new InvalidOperationException("unexpected"), submitted: false).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.TransportFault,
            Classify(new ObjectDisposedException("client"), submitted: false).ErrorCode);
    }

    [TestMethod]
    public void RetrySafetyMatrix_IsClosedOverTheKnownCodes()
    {
        // 只有「未提交 + 解析/连通/忙」这三类可以安全重试，其余一律否。
        foreach (var code in new[]
                 {
                     SshErrorCodes.ConfigInvalid,
                     SshErrorCodes.RequestInvalid,
                     SshErrorCodes.TargetChanged,
                     SshErrorCodes.CredentialUnavailable,
                     SshErrorCodes.DirectoryUnavailable,
                     SshErrorCodes.PassphraseRequired,
                     SshErrorCodes.KnownHostsUnsupported,
                     SshErrorCodes.HostKeyUntrusted,
                     SshErrorCodes.HostKeyMismatch,
                     SshErrorCodes.HostKeyRevoked,
                     SshErrorCodes.AuthenticationFailed,
                     SshErrorCodes.CommandDenied,
                     SshErrorCodes.Timeout,
                     SshErrorCodes.Cancelled,
                     SshErrorCodes.ConnectionLost,
                     SshErrorCodes.ExitStatusMissing,
                     SshErrorCodes.RemoteExitNonzero,
                     SshErrorCodes.TransportFault,
                 })
        {
            Assert.IsFalse(
                SshFailureClassifier.IsRetrySafe(code, commandSubmitted: false),
                $"{code} 不应被标记为可安全重试");
        }

        Assert.IsTrue(SshFailureClassifier.IsRetrySafe(SshErrorCodes.DnsFailed, commandSubmitted: false));
        Assert.IsTrue(SshFailureClassifier.IsRetrySafe(SshErrorCodes.ConnectFailed, commandSubmitted: false));
        Assert.IsTrue(SshFailureClassifier.IsRetrySafe(SshErrorCodes.Busy, commandSubmitted: false));

        // 已提交后即使是连通类错误也不可重试。
        Assert.IsFalse(SshFailureClassifier.IsRetrySafe(SshErrorCodes.ConnectFailed, commandSubmitted: true));
    }

    [TestMethod]
    public void A1CompatibilityOverload_StillReturnsTheCode()
    {
        Assert.AreEqual(
            SshErrorCodes.Timeout,
            SshFailureClassifier.Classify(new SshOperationTimeoutException("slow"), commandSubmitted: false));
        Assert.IsTrue(SshFailureClassifier.IsRetrySafe(new SocketException((int)SocketError.ConnectionRefused), false));
        Assert.IsFalse(SshFailureClassifier.IsRetrySafe(new SshConnectionException("x"), true));
    }
}
