namespace PuddingPathFilteringTests;

/// <summary>D2 canonical 名单：必须显式包含任务书点名的 8 项，且语义与既有契约一致。</summary>
[TestClass]
public sealed class PathNoiseRulesTests
{
    [DataTestMethod]
    [DataRow(".pudding")]
    [DataRow(".tmp-build")]
    [DataRow(".pnpm-store")]
    [DataRow(".tmp-test-out")]
    [DataRow(".tmp")]
    [DataRow(".firecrawl")]
    [DataRow("publish")]
    [DataRow("artifacts")]
    public void Required_noise_directories_are_present(string name)
    {
        Assert.Contains(name, PathNoiseRules.DirectoryNames, $"D2 requires '{name}'");
        Assert.IsTrue(PathNoiseRules.IsNoisePath($"repo/{name}/file.cs"));
        Assert.IsTrue(PathNoiseRules.IsNoisePath($"{name}/file.cs"));
    }

    [TestMethod]
    public void Union_of_the_three_documented_rule_sets_is_covered()
    {
        // ADR-089 §2.2 记录的三套现存清单的并集（NoiseDirectoryRules.Segments 的 46 项）必须仍在册，
        // 否则本刀会静默放宽既有排除面。
        string[] documented =
        [
            "$outputWwwroot", "dist", "node_modules", "bin", "obj", ".git", ".pudding", "TestResults",
            "artifacts", "publish", ".venv", ".tmp",
            ".vs", ".idea", ".vscode", "packages", "build", ".next", "out", "__pycache__", "venv",
            ".tox", ".eggs", "coverage", ".nyc_output", ".pytest_cache", ".pudding-code", "Debug",
            "Release", "x64", "x86", "ARM", "ARM64",
            ".svn", ".hg", "target", ".mypy_cache", "vendor", "bower_components", ".nuxt", ".output",
            ".angular", ".cache", ".turbo", "tmp", "temp",
        ];

        var missing = documented.Where(n => !PathNoiseRules.DirectoryNames.Contains(n)).ToArray();
        Assert.IsEmpty(missing, "missing from the canonical set: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void Canonical_set_is_exactly_the_union_plus_the_declared_additions()
    {
        // 防止 «顺手多加» / «顺手少加»：条数必须可解释。
        // 46（三套已文档化清单的并集）+ .tmp-build + .pnpm-store + .tmp-test-out + .firecrawl（任务书 §D2 点名）
        //                                + pub + .codex-out（仓库 .gitignore 已有的制成品/工具产物目录）
        Assert.HasCount(52, PathNoiseRules.DirectoryNames, "46 documented union + 6 declared additions");
        Assert.HasCount(6, PathNoiseRules.FileNames);
    }

    [TestMethod]
    public void SegmentsAreMatchedExactlyNotByPrefix()
    {
        Assert.IsFalse(PathNoiseRules.IsNoisePath("repo/src/bin.cs"), "a prefix match would be wrong");
        Assert.IsFalse(PathNoiseRules.IsNoisePath("repo/src/obj.json"));
        Assert.IsFalse(PathNoiseRules.IsNoisePath("repo/src/binder/Foo.cs"));
        Assert.IsFalse(PathNoiseRules.IsNoisePath("repo/src/nodes_modules/x"));
    }

    [TestMethod]
    public void LastSegmentCountsToo()
    {
        // 与 FullTextIndexOptions.IsExcludedPath 的既有语义一致：名为 bin 的「文件」也算噪声。
        Assert.IsTrue(PathNoiseRules.IsNoisePath("repo/src/bin"));
        Assert.IsTrue(PathNoiseRules.IsNoisePath("package-lock.json"));
        Assert.IsTrue(PathNoiseRules.IsNoiseSegment("Thumbs.db"));
        Assert.IsFalse(PathNoiseRules.IsNoiseSegment("Program.cs"));
    }

    [TestMethod]
    public void MatchingIsCaseInsensitiveAndSeparatorAgnostic()
    {
        Assert.IsTrue(PathNoiseRules.IsNoisePath(@"repo\BIN\Debug\x.dll"));
        Assert.IsTrue(PathNoiseRules.IsNoisePath("repo/TestResults/x.trx"));
        Assert.IsTrue(PathNoiseRules.IsNoisePath("repo/testresults/x.trx"));
    }

    /// <summary>
    /// 回归锁：把<b>绝对路径</b>直接喂给 <see cref="PathNoiseRules.IsNoisePath"/> 会把宿主自身的目录名当噪声。
    /// 这个 bug 在 U4-4 接入时被 <c>PuddingCodeIntelligenceTests</c> 的两个用例当场抓到。
    /// </summary>
    [TestMethod]
    public void AbsolutePathsMustBeRebasedBeforeTheNoiseCheck()
    {
        const string hostTempFile = @"C:\Users\x\AppData\Local\Temp\pudding-tests\abc\Real.cs";
        const string scanRoot = @"C:\Users\x\AppData\Local\Temp\pudding-tests\abc";

        // 反例：绝对路径直接判定 → 段名 Temp 命中 temp
        Assert.IsTrue(PathNoiseRules.IsNoisePath(hostTempFile));

        // 正解：先折算到扫描根之下
        Assert.IsFalse(PathNoiseRules.IsNoisePathBelow(scanRoot, hostTempFile));
        Assert.IsTrue(PathNoiseRules.IsNoisePathBelow(scanRoot, scanRoot + @"\bin\Generated.cs"));

        // 不在根之下时降级为只看最后一段 —— 不会因为上游目录名而误判整棵树
        Assert.IsFalse(PathNoiseRules.IsNoisePathBelow(scanRoot, @"D:\other\Temp\Real.cs"));
        Assert.IsTrue(PathNoiseRules.IsNoisePathBelow(scanRoot, @"D:\other\bin"));

        // 空根 = 原样判定
        Assert.IsTrue(PathNoiseRules.IsNoisePathBelow(null, "obj/x.cs"));
    }

    [TestMethod]
    public void EmptyAndNullAreNotNoise()
    {
        Assert.IsFalse(PathNoiseRules.IsNoisePath(null));
        Assert.IsFalse(PathNoiseRules.IsNoisePath(string.Empty));
        Assert.IsFalse(PathNoiseRules.IsNoisePath("./"));
        Assert.IsFalse(PathNoiseRules.IsNoiseSegment(null));
    }
}
