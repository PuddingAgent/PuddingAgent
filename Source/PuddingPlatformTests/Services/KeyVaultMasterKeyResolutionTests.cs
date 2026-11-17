using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 锁住 <see cref="KeyVaultService.ResolveMasterKey"/> 的取值顺序。
///
/// <para>这个顺序决定了保管箱里的密文下次启动还能不能解开：
/// 先读持久化来源，读不到才生成并落盘。顺序若被改成"先生成"，
/// 每次启动都会铸一把新主密钥，历史密文当场失联——而单次运行完全看不出异常。</para>
///
/// <para>所以这里的核心不是"能不能拿到 32 字节"，而是
/// <b>复用路径下 Persist 一次都不许被调用</b>。</para>
/// </summary>
[TestClass]
public sealed class KeyVaultMasterKeyResolutionTests
{
    private const string EnvName = "PUDDING_KEYVAULT_MASTER_KEY";

    private string? _originalEnv;

    [TestInitialize]
    public void ClearEnvironmentOverride()
    {
        _originalEnv = Environment.GetEnvironmentVariable(EnvName);
        Environment.SetEnvironmentVariable(EnvName, null, EnvironmentVariableTarget.Process);
    }

    [TestCleanup]
    public void RestoreEnvironmentOverride()
    {
        Environment.SetEnvironmentVariable(EnvName, _originalEnv, EnvironmentVariableTarget.Process);
    }

    private static byte[] Key(int seed) =>
        Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    private sealed class FakeProvider : IMasterKeyProvider
    {
        public byte[]? Stored { get; init; }

        public Exception? PersistFailure { get; init; }

        public int TryResolveCalls { get; private set; }

        public int PersistCalls { get; private set; }

        public byte[]? Written { get; private set; }

        public byte[]? TryResolve()
        {
            TryResolveCalls++;
            return Stored;
        }

        public void Persist(ReadOnlySpan<byte> masterKey)
        {
            PersistCalls++;
            Written = masterKey.ToArray();
            if (PersistFailure is not null)
                throw PersistFailure;
        }
    }

    [TestMethod]
    public void EnvironmentVariableWinsOverPersistedProvider()
    {
        var fromEnv = Key(1);
        Environment.SetEnvironmentVariable(EnvName, Convert.ToBase64String(fromEnv), EnvironmentVariableTarget.Process);
        var provider = new FakeProvider { Stored = Key(101) };

        var resolved = KeyVaultService.ResolveMasterKey(NullLogger.Instance, provider);

        CollectionAssert.AreEqual(fromEnv, resolved);
        Assert.AreEqual(0, provider.TryResolveCalls, "显式配置了环境变量时不应再询问持久化来源。");
        Assert.AreEqual(0, provider.PersistCalls);
    }

    /// <summary>
    /// 核心用例：已有持久化主密钥时必须复用，且绝不覆盖写盘。
    /// 若实现改成"先生成再落盘"，PersistCalls 会变成 1，此用例立即失败。
    /// </summary>
    [TestMethod]
    public void PersistedKeyIsReusedAndNeverRewritten()
    {
        var persisted = Key(201);
        var provider = new FakeProvider { Stored = persisted };

        var resolved = KeyVaultService.ResolveMasterKey(NullLogger.Instance, provider);

        CollectionAssert.AreEqual(persisted, resolved);
        Assert.AreEqual(1, provider.TryResolveCalls);
        Assert.AreEqual(0, provider.PersistCalls, "复用已有主密钥时绝不能回写磁盘。");
        Assert.IsNull(provider.Written);
    }

    /// <summary>
    /// 首次启动：没有持久化来源时生成 32 字节，并把同一份字节交给 Persist。
    /// 断言"交给 Persist 的就是返回的那份"——否则写盘的和内存用的不是同一把钥匙。
    /// </summary>
    [TestMethod]
    public void MissingKeyIsGeneratedThenPersistedWithTheSameBytes()
    {
        var provider = new FakeProvider { Stored = null };

        var resolved = KeyVaultService.ResolveMasterKey(NullLogger.Instance, provider);

        Assert.AreEqual(32, resolved.Length);
        Assert.IsTrue(resolved.Any(b => b != 0), "生成的密钥不应全零。");
        Assert.AreEqual(1, provider.TryResolveCalls);
        Assert.AreEqual(1, provider.PersistCalls);
        CollectionAssert.AreEqual(resolved, provider.Written);
    }

    [TestMethod]
    public void PersistFailureRefusesToUseAnEphemeralKey()
    {
        var provider = new FakeProvider
        {
            Stored = null,
            PersistFailure = new IOException("disk unavailable"),
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => KeyVaultService.ResolveMasterKey(NullLogger.Instance, provider));
        Assert.AreEqual(1, provider.PersistCalls);
        Assert.IsTrue(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvName)));
    }

    [TestMethod]
    public void MissingProviderStillProducesAKeyThroughProcessEnvironment()
    {
        var resolved = KeyVaultService.ResolveMasterKey(NullLogger.Instance, masterKeyProvider: null);

        Assert.AreEqual(32, resolved.Length);
        var fallback = Environment.GetEnvironmentVariable(EnvName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fallback));
        CollectionAssert.AreEqual(resolved, Convert.FromBase64String(fallback!));
    }

    [TestMethod]
    public void InvalidBase64EnvironmentValueIsRejectedInsteadOfIgnored()
    {
        Environment.SetEnvironmentVariable(EnvName, "not-base64!!", EnvironmentVariableTarget.Process);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => KeyVaultService.ResolveMasterKey(NullLogger.Instance, new FakeProvider()));

        StringAssert.Contains(ex.Message, EnvName);
    }

    [TestMethod]
    public void WrongLengthEnvironmentValueIsRejected()
    {
        Environment.SetEnvironmentVariable(EnvName, Convert.ToBase64String(Key(1)[..16]), EnvironmentVariableTarget.Process);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => KeyVaultService.ResolveMasterKey(NullLogger.Instance, new FakeProvider()));

        StringAssert.Contains(ex.Message, "32");
    }
}
