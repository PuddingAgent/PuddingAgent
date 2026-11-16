using System.Text;
using HarnessAgent.Core.Secrets;

namespace HarnessAgent.Core.Tests.Secrets;

/// <summary>
/// 密钥保管箱的用例。
///
/// <para>用可控的替身而不是真 DPAPI：只有这样，"落盘字节里不含明文" 这条断言
/// 才是真的在检查保管箱的行为，而不是在检查 DPAPI 的实现。</para>
/// </summary>
[TestClass]
public sealed class SecretStoreTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "pudding-secrets",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SecretStore Store(ISecretProtector? protector = null) =>
        new(_root, protector ?? new ReversingProtector());

    [TestMethod]
    public async Task RoundTripsAValue()
    {
        var store = Store();

        await store.SetAsync("mail.password", "s3cr3t-value");

        var read = await store.TryGetAsync("mail.password");
        Assert.IsTrue(read.Found);
        Assert.AreEqual("s3cr3t-value", read.Value);
    }

    [TestMethod]
    public async Task FileOnDiskNeverContainsThePlaintext()
    {
        var store = Store();
        const string secret = "authorization-code-1234567890";

        await store.SetAsync("mail.password", secret);

        var path = Path.Combine(_root, "mail.password.secret");
        var bytes = await File.ReadAllBytesAsync(path);
        var asText = Encoding.UTF8.GetString(bytes);

        Assert.IsFalse(
            asText.Contains(secret, StringComparison.Ordinal),
            "落盘内容绝不能包含明文");
        Assert.IsFalse(
            Encoding.ASCII.GetString(bytes).Contains(secret, StringComparison.Ordinal),
            "按 ASCII 看也不能包含明文");
    }

    [TestMethod]
    public async Task OverwritesAnExistingSecret()
    {
        var store = Store();

        await store.SetAsync("token", "first");
        await store.SetAsync("token", "second");

        Assert.AreEqual("second", (await store.TryGetAsync("token")).Value);
        Assert.AreEqual(1, store.ListKeys().Count);
    }

    [TestMethod]
    public async Task UnknownKeyIsNotFound()
    {
        var read = await Store().TryGetAsync("missing");

        Assert.IsFalse(read.Found);
        StringAssert.Contains(read.Reason, "not found");
    }

    [TestMethod]
    public async Task FileWithoutMagicIsRejectedInsteadOfTreatedAsASecret()
    {
        var store = Store();
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "dropped.secret"),
            Encoding.UTF8.GetBytes("not our format"));

        var read = await store.TryGetAsync("dropped");

        Assert.IsFalse(read.Found);
        StringAssert.Contains(read.Reason, "expected format");
    }

    [TestMethod]
    public async Task UndecryptablePayloadIsReportedNotThrown()
    {
        var store = Store();
        // 魔数正确、内容不是本保护器产出的 ⇒ 模拟换机器/密文被改。
        var envelope = new byte[] { (byte)'P', (byte)'D', (byte)'G', (byte)'1', 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(_root, "broken.secret"), envelope);

        var read = await store.TryGetAsync("broken");

        Assert.IsFalse(read.Found);
        StringAssert.Contains(read.Reason, "could not be decrypted");
    }

    [DataTestMethod]
    [DataRow("../escape")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow(".hidden")]
    [DataRow("..")]
    [DataRow("")]
    [DataRow("has space")]
    [DataRow("non-ascii-密码")]
    public void UnsafeKeysAreRejected(string key)
    {
        var decision = SecretStore.ValidateKey(key);

        Assert.IsFalse(decision.Allowed, key + " 必须被拒绝");
    }

    [TestMethod]
    public void KeysAreNormalizedToLowercaseAndTrimmed()
    {
        var decision = SecretStore.ValidateKey("  Mail.Password  ");

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("mail.password", decision.NormalizedKey);
    }

    [TestMethod]
    public async Task WritingWithAnUnsafeKeyThrowsInsteadOfEscapingTheDirectory()
    {
        var store = Store();

        var threw = false;
        try
        {
            await store.SetAsync("../escape", "value");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "不安全的键名必须抛 ArgumentException");
    }

    [TestMethod]
    public async Task RemoveDeletesAndReportsWhetherItExisted()
    {
        var store = Store();
        await store.SetAsync("temporary", "x");

        Assert.IsTrue(store.Remove("temporary"));
        Assert.IsFalse(store.Remove("temporary"));
        Assert.IsFalse((await store.TryGetAsync("temporary")).Found);
    }

    [TestMethod]
    public async Task ListKeysIsSortedAndIgnoresTemporaryFiles()
    {
        var store = Store();
        await store.SetAsync("b.key", "1");
        await store.SetAsync("a.key", "2");
        await File.WriteAllBytesAsync(Path.Combine(_root, "c.key.secret.tmp-abc"), [1, 2, 3]);

        CollectionAssert.AreEqual(new[] { "a.key", "b.key" }, store.ListKeys().ToArray());
    }

    [TestMethod]
    public async Task EmptyProtectorOutputFailsLoudly()
    {
        var store = Store(new EmptyProtector());

        var threw = false;
        try
        {
            await store.SetAsync("k", "v");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "保护器返回空载荷时必须显式失败，不能落明文");
    }

    /// <summary>把字节反过来，并在前面加一个标记字节——足够区分"保护过"和"没保护过"。</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        private const byte Marker = 0x7F;

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var result = new byte[plaintext.Length + 1];
            result[0] = Marker;
            for (var i = 0; i < plaintext.Length; i++)
                result[i + 1] = plaintext[plaintext.Length - 1 - i];
            return result;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload)
        {
            if (protectedPayload.Length == 0 || protectedPayload[0] != Marker)
                throw new InvalidOperationException("not produced by this protector");

            var body = protectedPayload[1..];
            var result = new byte[body.Length];
            for (var i = 0; i < body.Length; i++)
                result[i] = body[body.Length - 1 - i];
            return result;
        }
    }

    private sealed class EmptyProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => [];

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => [];
    }
}
