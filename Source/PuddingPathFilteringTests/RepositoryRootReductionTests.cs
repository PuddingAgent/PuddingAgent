namespace PuddingPathFilteringTests;

/// <summary>
/// <b>「−84%」判据的可失效断言</b>（任务书 §6 变异轮 2 要求的靶子）。
/// <para>
/// 度量口径与判据来自 U4-0 设施（<c>PuddingRetrievalEvalProbe --mode count</c>，与
/// <c>FullTextIndexOptions.IsExcludedPath</c> 同一套谓词），P3/D5 的原始输出分别在
/// <c>temp/u4-4-P3-baseline.txt</c> 与 <c>temp/u4-4-D5-after.txt</c>：
/// </para>
/// <list type="bullet">
/// <item>改前：仓库根可索引 <b>28,519</b> 个文件；其中四个头部噪声目录
///       <c>.pudding</c> 21,106 + <c>.tmp-build</c> 1,335 + <c>.pnpm-store</c> 1,075 +
///       <c>.tmp-test-out</c> 240 = <b>23,756（83.3%）</b>。</item>
/// <item>改后：<b>4,413</b> 个文件（<b>−84.5%</b>）。</item>
/// </list>
/// <para>
/// 本断言把「这四家必须在册」钉成可失效条件：从 canonical 集合里移除其中<b>任意一家</b>
/// （尤其是 <c>.pudding</c>）都会使覆盖率跌破 80% 而变红。
/// </para>
/// <para>
/// ⚠️ 诚实的边界：这是**冻结基线的覆盖断言**，不是每次运行的实时重测（实时重测要扫 490 万文件、
/// 约 212 s，不适合放进单元测试）。实时重测的原始输出见报告 §D5 引用的两个 temp 文件。
/// </para>
/// </summary>
[TestClass]
public sealed class RepositoryRootReductionTests
{
    /// <summary>P3 实测：改前仓库根可索引文件数（U4-0 口径）。</summary>
    private const long BaselineIndexableFiles = 28_519;

    /// <summary>P3 实测：四个头部噪声目录各自吞掉的可索引文件数（相对仓库根）。</summary>
    private static readonly (string Directory, long IndexableFiles)[] DominantNoiseDirectories =
    [
        (".pudding", 21_106),
        (".tmp-build", 1_335),
        (".pnpm-store", 1_075),
        (".tmp-test-out", 240),
    ];

    [TestMethod]
    public void Canonical_set_covers_at_least_eighty_percent_of_the_indexable_corpus()
    {
        var covered = DominantNoiseDirectories
            .Where(entry => PathNoiseRules.DirectoryNames.Contains(entry.Directory))
            .Sum(entry => entry.IndexableFiles);

        var coveredPercent = covered * 100.0 / BaselineIndexableFiles;

        // 单位必须一致：这里是百分比（83.3），不是 0.833 —— 第一版写成 `IsGreaterThanOrEqualTo(0.80, ratio)`
        // 时量纲不同，变异也不会变红（实际占位 83.3 与 9.3 都 ≥ 0.80）。改用显式比较。
        Assert.IsTrue(coveredPercent >= 80.0,
            $"the canonical set only covers {covered}/{BaselineIndexableFiles} indexable files " +
            $"({coveredPercent:F1}%); U4-4 requires ≈ −84%, so at least the four dominant directories " +
            "must stay in the set");
    }

    [TestMethod]
    public void Every_dominant_noise_directory_is_excluded_at_the_repository_root()
    {
        foreach (var (directory, indexable) in DominantNoiseDirectories)
        {
            Assert.IsTrue(PathNoiseRules.IsNoisePath($"{directory}/probe/file.cs"),
                $"'{directory}' carries {indexable} indexable files and must be excluded");
        }
    }

    /// <summary>四个目录合计占改前语料的 83.3% —— 冻结数字本身也要可核对。</summary>
    [TestMethod]
    public void Dominant_noise_directories_account_for_the_recorded_share_of_the_baseline()
    {
        var sum = DominantNoiseDirectories.Sum(entry => entry.IndexableFiles);

        Assert.AreEqual(23_756, sum, "P3 baseline: the four directories together carried 23,756 indexable files");
        Assert.IsTrue(sum * 100.0 / BaselineIndexableFiles is > 83 and < 84,
            $"recorded share was 83.3%, got {sum * 100.0 / BaselineIndexableFiles:F1}%");
    }
}
