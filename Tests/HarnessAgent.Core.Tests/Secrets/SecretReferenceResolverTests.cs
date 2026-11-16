using System.Text;
using HarnessAgent.Core.Secrets;

namespace HarnessAgent.Core.Tests.Secrets;

/// <summary>
/// 密钥引用解析的用例。
///
/// <para>两条语义必须被钉住：引用占满整个值；看起来像引用但格式不对时**不许原样返回**。
/// 后者是"静默降级"的典型入口——值照常流到下游，报出来的错与病因无关。</para>
/// </summary>
[TestClass]
public sealed class SecretReferenceResolverTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-secret-ref", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<SecretReferenceResolver> ResolverWithAsync(string key, string value)
    {
        var store = new SecretStore(_root, new PassthroughProtector());
        await store.SetAsync(key, value);
        return new SecretReferenceResolver(store);
    }

    [TestMethod]
    public async Task PlainValuePassesThroughUntouched()
    {
        var resolver = await ResolverWithAsync("unused", "x");

        var result = await resolver.ResolveAsync("https://example.test");

        Assert.IsFalse(result.IsReference);
        Assert.IsTrue(result.Found);
        Assert.AreEqual("https://example.test", result.Value);
    }

    [TestMethod]
    public async Task WholeValueReferenceResolves()
    {
        var resolver = await ResolverWithAsync("llm.deepseek.apiKey", "sk-abc123");

        var result = await resolver.ResolveAsync("${secret:llm.deepseek.apiKey}");

        Assert.IsTrue(result.IsReference);
        Assert.IsTrue(result.Found);
        Assert.AreEqual("sk-abc123", result.Value);
    }

    [TestMethod]
    public async Task SurroundingWhitespaceIsTolerated()
    {
        var resolver = await ResolverWithAsync("k1", "v1");

        var result = await resolver.ResolveAsync("  ${secret:k1}  ");

        Assert.AreEqual("v1", result.Value);
    }

    [TestMethod]
    public async Task MissingSecretIsReportedNotInvented()
    {
        var resolver = await ResolverWithAsync("present", "x");

        var result = await resolver.ResolveAsync("${secret:absent}");

        Assert.IsTrue(result.IsReference);
        Assert.IsFalse(result.Found);
        Assert.IsNull(result.Value);
        StringAssert.Contains(result.Reason, "could not be resolved");
    }

    [TestMethod]
    public async Task PartialInterpolationIsRefusedInsteadOfLeakingALiteral()
    {
        var resolver = await ResolverWithAsync("k1", "v1");

        var result = await resolver.ResolveAsync("prefix-${secret:k1}-suffix");

        Assert.IsTrue(result.IsReference);
        Assert.IsFalse(result.Found, "局部插值必须拒绝，不能把字面量传下去");
        Assert.AreEqual(null, result.Value);
    }

    [DataTestMethod]
    [DataRow("${secret:}")]
    [DataRow("${secret:   }")]
    [DataRow("${secret:abc")]
    [DataRow("${secret:../escape}")]
    public async Task MalformedReferencesAreRefused(string raw)
    {
        var resolver = await ResolverWithAsync("abc", "v");

        var result = await resolver.ResolveAsync(raw);

        if (!raw.Contains(SecretReferenceResolver.Prefix, StringComparison.Ordinal))
            return;

        Assert.IsTrue(result.IsReference);
        Assert.IsFalse(result.Found, raw + " 必须被拒绝");
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task RequireValueThrowsWhenUnresolved()
    {
        var resolver = await ResolverWithAsync("present", "x");
        var result = await resolver.ResolveAsync("${secret:absent}");

        var threw = false;
        try { _ = result.RequireValue(); }
        catch (InvalidOperationException) { threw = true; }

        Assert.IsTrue(threw, "解不开时必须抛异常，不能返回 null 去连服务器");
    }

    [TestMethod]
    public async Task RequireValueReturnsPlainValuesToo()
    {
        var resolver = await ResolverWithAsync("present", "x");
        var result = await resolver.ResolveAsync("plain-value");

        Assert.AreEqual("plain-value", result.RequireValue());
    }

    /// <summary>恒等保护器：这些用例只验证解析逻辑，不验证加密。</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => protectedPayload.ToArray();
    }
}
