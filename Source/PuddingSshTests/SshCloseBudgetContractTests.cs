using System.Security.Cryptography;
using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Execution;
using PuddingSsh.Transport;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// F5 回归（2026-10-18 独立审阅）：中止请求只登记意图，**关闭预算必须由带预算的释放决定**；
/// 配置收窄到 50 ms 时，真实 <see cref="SshNetTransportSession"/> 的关闭状态机必须真的用 50 ms，
/// 而不是在 <c>RequestAbort</c> 里先以默认 2 s 启动关闭。
/// </summary>
[TestClass]
public sealed class SshCloseBudgetContractTests
{
    private static (SshNetTransportSession Session, List<IDisposable> Owned) RealSession()
    {
        var owned = new List<IDisposable>();
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var keyBytes = Encoding.ASCII.GetBytes(pem);
        var keyStream = new MemoryStream(keyBytes);
        owned.Add(keyStream);
        var privateKey = new PrivateKeyFile(keyStream);
        owned.Add(privateKey);
        var identity = new SshIdentityMaterial(Path.GetTempPath(), "id_ed25519", keyBytes);
        owned.Add(identity);
        var target = new SshTargetRef("host-1", "rev-1", "127.0.0.1", 1, "root", "trust-1");
        var connectionInfo = new ConnectionInfo(
            "127.0.0.1",
            1,
            "root",
            new PrivateKeyAuthenticationMethod("root", privateKey));
        var client = new SshClient(connectionInfo);
        owned.Add(client);
        var session = new SshNetTransportSession(client, target, identity, privateKey, keyStream, "SHA256:test", "ssh-ed25519");
        return (session, owned);
    }

    [TestMethod]
    public async Task RealSession_AbortThenDisposeWithBudget_AppliesTheConfiguredBudget()
    {
        var (session, owned) = RealSession();
        try
        {
            // 旧实现：RequestAbort 立刻以默认 2 s 启动关闭；随后 DisposeAsync(50 ms) 只能复用。
            session.RequestAbort();
            await session.DisposeAsync(TimeSpan.FromMilliseconds(50));

            Assert.IsTrue(session.Closed);
            Assert.IsLessThan(
                500,
                (int)session.CloseBudget.TotalMilliseconds,
                "关闭预算必须是带预算释放传入的额度，而不是 RequestAbort 的默认 2 s");
            Assert.IsTrue(session.CloseBudget >= TimeSpan.FromMilliseconds(30), "预算不得被截断为 0");
        }
        finally
        {
            foreach (var item in owned)
            {
                item.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Coordinator_NarrowedCleanupGrace_ReachesRealSessionClose()
    {
        // R6 的原形状：协调器配置 CleanupGrace=50 ms，真实会话关闭后读到的预算必须是 ~50 ms。
        var (session, owned) = RealSession();
        try
        {
            var factory = new FakeTransportFactory
            {
                OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session),
            };
            await using var coordinator = new SshOperationCoordinator(
                factory,
                new FakeIdentityProvider(),
                new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(50) });

            var host = FakeSsh.Host();
            var result = await coordinator.TestAsync(host, new SshTestRequest(host.Target), CancellationToken.None);

            // 结果本身不重要（未连接的客户端必然失败）；关键是**关闭预算合同**。
            Assert.IsTrue(session.Closed);
            Assert.IsLessThan(
                500,
                (int)session.CloseBudget.TotalMilliseconds,
                "协调器收窄的 CleanupGrace 必须传到真实会话的关闭状态机");
            Assert.IsTrue(session.CloseBudget >= TimeSpan.FromMilliseconds(30), "预算不得被截断为 0");
            Assert.IsNotNull(result, "结果必须返回（不得抛异常外溢）");
        }
        finally
        {
            foreach (var item in owned)
            {
                item.Dispose();
            }
        }
    }
}
