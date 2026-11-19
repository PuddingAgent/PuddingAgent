using PuddingTokenization;

namespace PuddingTokenizationTests;

/// <summary>
/// 词表资产目录的解析策略。
/// </summary>
/// <remarks>
/// <para>
/// 这组用例的核心是**否定判定**：「目录存在」不等于「已配置」。
/// 部署目录里天然有一个空的 <c>tokenizers/</c>，若把它当成已配置，诊断会报
/// <c>beside-application</c> + <c>AssetMissing</c>，读起来像「文件被删了」，
/// 而真相是「从没往里放过文件」。两类问题的处置完全不同，必须能从诊断区分。
/// </para>
/// <para>
/// 解析函数接收环境变量值与基准目录，而不是自己读进程状态——这正是为了能在
/// 这里断言「空目录」这种真实但难复现的现场。
/// </para>
/// </remarks>
[TestClass]
public sealed class TokenizerAssetDirectoryTests
{
    private string _sandbox = null!;

    [TestInitialize]
    public void CreateSandbox()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "pudding-tok-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    [TestCleanup]
    public void RemoveSandbox()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [TestMethod]
    public void EnvironmentValue_WinsAndIsTrimmed()
    {
        var beside = Path.Combine(_sandbox, "tokenizers");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "any.tokenizer.json"), "{}");

        var resolved = TokenizerAssetDirectory.Resolve("  D:\\assets\\tokenizers  ", _sandbox);

        Assert.AreEqual(TokenizerAssetDirectory.EnvironmentSource, resolved.Source);
        Assert.AreEqual(@"D:\assets\tokenizers", resolved.Directory);
    }

    [TestMethod]
    public void WhitespaceEnvironmentValue_IsTreatedAsUnset()
    {
        var resolved = TokenizerAssetDirectory.Resolve("   ", _sandbox);

        Assert.AreEqual(TokenizerAssetDirectory.NoneSource, resolved.Source);
        Assert.IsNull(resolved.Directory);
    }

    [TestMethod]
    public void MissingBesideDirectory_ReportsNone()
    {
        var resolved = TokenizerAssetDirectory.Resolve(null, _sandbox);

        Assert.AreEqual(TokenizerAssetDirectory.NoneSource, resolved.Source);
        Assert.IsNull(
            resolved.Directory,
            "「放弃精确计数」必须表现为 Directory 为 null，否则 TokenizerProvider 会去空目录找文件、报 AssetMissing。");
    }

    /// <summary>
    /// 回归用例：部署目录里的空 <c>tokenizers/</c> 不得被当成已配置。
    /// </summary>
    /// <remarks>
    /// 旧判据用 <c>Directory.Exists</c>，这条用例当时会红：真实部署形态就是
    /// 「目录在、文件 0 个」，于是诊断把「没配」报成了 <c>beside-application</c>。
    /// </remarks>
    [TestMethod]
    public void EmptyBesideDirectory_IsNotConfigured()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "tokenizers"));

        var resolved = TokenizerAssetDirectory.Resolve(null, _sandbox);

        Assert.AreEqual(
            TokenizerAssetDirectory.NoneSource,
            resolved.Source,
            "空目录等于没配。报 beside-application 会让「从没放过文件」看起来像「文件被删了」。");
        Assert.IsNull(resolved.Directory);
    }

    [TestMethod]
    public void BesideDirectoryWithUnrelatedFilesOnly_IsNotConfigured()
    {
        var beside = Path.Combine(_sandbox, "tokenizers");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "readme.txt"), "assets land here");
        File.WriteAllText(Path.Combine(beside, "tokenizer.json"), "{}");

        var resolved = TokenizerAssetDirectory.Resolve(null, _sandbox);

        // 注意第二条：裸的 tokenizer.json 不算资产，因为 TokenizerProvider 按
        // <identity>.tokenizer.json 查找，只有裸名的目录一样拿不到任何词表。
        Assert.AreEqual(TokenizerAssetDirectory.NoneSource, resolved.Source);
    }

    [TestMethod]
    public void BesideDirectoryWithIdentityAsset_ReportsBesideApplication()
    {
        var beside = Path.Combine(_sandbox, "tokenizers");
        Directory.CreateDirectory(beside);
        File.WriteAllText(
            Path.Combine(beside, ModelTokenizerCatalog.DeepSeekV41Flash + ".tokenizer.json"),
            "{}");

        var resolved = TokenizerAssetDirectory.Resolve(null, _sandbox);

        Assert.AreEqual(TokenizerAssetDirectory.BesideApplicationSource, resolved.Source);
        Assert.AreEqual(beside, resolved.Directory);
    }

    [TestMethod]
    public void AssetSuffixMatch_IsCaseInsensitive()
    {
        var beside = Path.Combine(_sandbox, "tokenizers");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "SOMETHING.TOKENIZER.JSON"), "{}");

        var resolved = TokenizerAssetDirectory.Resolve(string.Empty, _sandbox);

        Assert.AreEqual(TokenizerAssetDirectory.BesideApplicationSource, resolved.Source);
    }

    [TestMethod]
    public void BlankBaseDirectory_IsRejected()
    {
        // 空白基准目录若被放行，会静默解析成当前工作目录旁的 tokenizers，
        // 于是「调用方传错了」变成「资产不见了」。
        Assert.ThrowsExactly<ArgumentException>(() => TokenizerAssetDirectory.Resolve(null, "   "));
    }
}
