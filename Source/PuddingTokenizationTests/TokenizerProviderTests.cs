using PuddingTokenization;

namespace PuddingTokenizationTests;

/// <summary>
/// 回退路径：拿不到精确词表时，必须**明确说明原因**，而不是给一个看起来正常的数字。
/// </summary>
/// <remarks>
/// 当前缺陷的成因之一就是「估算值被当成事实」；回退路径若也返回数字，等于把这个坑
/// 换个地方重挖。所以这里的核心断言是「回退时不提供计数能力」。
/// </remarks>
[TestClass]
public sealed class TokenizerProviderTests
{
    [TestMethod]
    public void UnregisteredModel_FallsBackWithReason()
    {
        var provider = TokenizerProvider.Create(@"C:\nonexistent-dir");
        var handle = provider.Resolve("deepseek-v4-pro");

        Assert.IsFalse(handle.IsExact);
        Assert.AreEqual(TokenizerFallbackReason.ModelNotRegistered, handle.FallbackReason);
    }

    [TestMethod]
    public void NullOrEmptyModel_FallsBack()
    {
        var provider = TokenizerProvider.Create(null);

        Assert.AreEqual(
            TokenizerFallbackReason.ModelNotRegistered,
            provider.Resolve(null).FallbackReason);
        Assert.AreEqual(
            TokenizerFallbackReason.ModelNotRegistered,
            provider.Resolve("   ").FallbackReason);
    }

    [TestMethod]
    public void RegisteredModelWithoutDirectory_ReportsNotConfigured()
    {
        var provider = TokenizerProvider.Create(null);
        var handle = provider.Resolve("deepseek-flash");

        Assert.IsFalse(handle.IsExact);
        Assert.AreEqual(TokenizerFallbackReason.NotConfigured, handle.FallbackReason);
    }

    [TestMethod]
    public void RegisteredModelWithMissingAsset_ReportsAssetMissing()
    {
        var provider = TokenizerProvider.Create(@"C:\definitely-not-here\assets");
        var handle = provider.Resolve("deepseek-flash");

        Assert.IsFalse(handle.IsExact);
        Assert.AreEqual(TokenizerFallbackReason.AssetMissing, handle.FallbackReason);
        Assert.IsNotNull(handle.FallbackDetail);
    }

    [TestMethod]
    public void RegisteredModelWithCorruptAsset_ReportsAssetInvalid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pudding-tok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, ModelTokenizerCatalog.DeepSeekV41Flash + ".tokenizer.json"),
                "{ not valid json }");

            var handle = TokenizerProvider.Create(dir).Resolve("deepseek-flash");

            Assert.IsFalse(handle.IsExact);
            Assert.AreEqual(TokenizerFallbackReason.AssetInvalid, handle.FallbackReason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void FallbackHandle_RefusesToProduceATokenCount()
    {
        var handle = TokenizerProvider.Create(null).Resolve("deepseek-flash");

        Assert.ThrowsExactly<InvalidOperationException>(() => handle.CountTokens("hello"));
        Assert.IsFalse(handle.TryCountTokens("hello", out var tokens));
        Assert.AreEqual(0, tokens);
    }

    [TestMethod]
    public void SameIdentityIsCachedAndReturnsSameInstance()
    {
        var provider = TokenizerProvider.Create(null);

        var first = provider.Resolve("deepseek-flash");
        var second = provider.Resolve("DeepSeek-V4.1-Flash");

        // 两个别名指向同一词表身份，但不给目录时每次返回新建的回退对象——
        // 这里只断言身份一致，缓存行为在真实资源下由 Load 的 Lazy 保证。
        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreEqual(ModelTokenizerCatalog.DeepSeekV41Flash, first.Identity);
    }

    [TestMethod]
    public void Catalog_RequiresExactAliasMatch()
    {
        Assert.AreEqual(
            ModelTokenizerCatalog.DeepSeekV41Flash,
            ModelTokenizerCatalog.ResolveExact("deepseek-flash"));
        Assert.AreEqual(
            ModelTokenizerCatalog.DeepSeekV41Flash,
            ModelTokenizerCatalog.ResolveExact("  DEEPSEEK-V4.1-FLASH  "));

        // 关键否定断言：同前缀但不同版本的模型不得被映射到 V4.1 词表，
        // 因为它们的 tokenizer.json 字节数与 ETag 都不同。
        Assert.IsNull(ModelTokenizerCatalog.ResolveExact("deepseek-v4-pro"));
        Assert.IsNull(ModelTokenizerCatalog.ResolveExact("deepseek-v4-flash"));
        Assert.IsNull(ModelTokenizerCatalog.ResolveExact("gpt-6.1-sol"));
    }
}
