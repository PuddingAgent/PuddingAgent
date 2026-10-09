using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 交付事实采集（B7 交付链入口）：从磁盘上的目录读出产物/检查/日志，汇总成可自检的清单。
/// 用临时目录做真实文件系统验证（这是本环境里能跑到的最接近"真跑一遍"的方式）。
/// </summary>
public sealed class DeliveryFactCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pudding-delivery-" + Guid.NewGuid().ToString("n"));

    public DeliveryFactCollectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    private string Publish(string fileName, string content = "binary")
    {
        var dir = Path.Combine(_root, "publish");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    private string Results(string fileName, string trx)
    {
        var dir = Path.Combine(_root, "results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), trx);
        return dir;
    }

    private string Changelogs(params (string FileName, string Content)[] files)
    {
        var dir = Path.Combine(_root, "changelog");
        Directory.CreateDirectory(dir);
        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, fileName), content);
        }

        return dir;
    }

    private static string Trx(int passed, int failed) =>
        $"""<TestRun><ResultSummary><Counters passed="{passed}" failed="{failed}" /></ResultSummary></TestRun>""";

    [Fact]
    public void CollectsArtifactsChecksAndUnverifiedItemsFromDisk()
    {
        var publish = Publish("PuddingDesktop.exe");
        Publish("Pudding.DesktopService.dll");
        File.WriteAllText(Path.Combine(publish, "notes.txt"), "not an artifact");   // 非产物不该进清单

        var results = Results("PuddingBrowser.AutomationTests.trx", Trx(276, 0));
        Results("PuddingHost.Tests.trx", Trx(258, 0));

        var changelogs = Changelogs(
            ("2026-10-08-B3原生拖拽.md", "- **真机未验证**：CDP 序列需外部 smoke 复核。\n- 其余检查通过。"),
            ("2026-10-08-无关文档.md", "- 这一行与验证无关。"));

        Assert.True(
            DeliveryFactCollector.TryCollect(
                new DeliveryCollectionRequest(_root, publish, results, changelogs, "build-2026-10-08", "552345c"),
                out var manifest,
                out var problems),
            string.Join("; ", problems));

        Assert.NotNull(manifest);
        Assert.Equal(["Pudding.DesktopService.dll", "PuddingDesktop.exe"], manifest!.Artifacts);
        Assert.Equal(2, manifest.Checks.Count);
        Assert.Equal(534, manifest.Checks.Sum(check => check.Passed));
        Assert.Contains(manifest.KnownUnverified!, item => item.Contains("真机未验证", StringComparison.Ordinal));
        Assert.Equal(DeliveryManifest.RequiredLifecycleSteps, manifest.RequiredExternalSteps);

        // 交付物里不该出现本机路径（只写文件名）。
        Assert.All(manifest.Artifacts, artifact => Assert.DoesNotContain(_root, artifact));
    }

    [Fact]
    public void MissingDirectoriesAreReportedSpecifically()
    {
        var request = new DeliveryCollectionRequest(
            Path.Combine(_root, "nope"),
            Path.Combine(_root, "nope-publish"),
            Path.Combine(_root, "nope-results"),
            Path.Combine(_root, "nope-changelog"),
            "build",
            "abc123");

        Assert.False(DeliveryFactCollector.TryCollect(request, out var manifest, out var problems));
        Assert.Null(manifest);
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("repo root", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("publish directory", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("results directory", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("changelog directory", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyPublishOrResultsDirectoriesAreRefusedNotGuessed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "publish"));
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        var changelogs = Changelogs(("a.md", "- 无未验证项。"));

        Assert.False(DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(
                _root, Path.Combine(_root, "publish"), Path.Combine(_root, "results"), changelogs, "b", "c"),
            out _,
            out var problems));

        Assert.Contains(problems, problem => problem.Contains("no .exe/.dll", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("no TRX", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingSuiteMakesTheCollectionRefuseToProduceAManifest()
    {
        var publish = Publish("app.exe");
        var results = Results("Host.Tests.trx", Trx(257, 1));
        var changelogs = Changelogs(("a.md", "- 真机未验证：需外部 smoke。"));

        Assert.False(DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(_root, publish, results, changelogs, "b", "c"),
            out var manifest,
            out var problems));

        Assert.Null(manifest);
        Assert.Contains(problems, problem => problem.Contains("failing checks", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableTrxIsTreatedAsMissingFact()
    {
        var publish = Publish("app.exe");
        var results = Results("Broken.Tests.trx", "not xml <<<");
        var changelogs = Changelogs(("a.md", "- 真机未验证：需外部 smoke。"));

        Assert.False(DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(_root, publish, results, changelogs, "b", "c"),
            out _,
            out var problems));

        Assert.Contains(problems, problem => problem.Contains("valid XML", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangelogsAreBoundedSoTheManifestStaysReadable()
    {
        var publish = Publish("app.exe");
        var results = Results("A.trx", Trx(1, 0));
        var changelogs = Changelogs(Enumerable.Range(0, 30)
            .Select(index => ($"{index:000}.md", $"- 真机未验证：第 {index} 项"))
            .ToArray());

        Assert.True(DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(_root, publish, results, changelogs, "b", "c"),
            out var manifest,
            out var problems),
            string.Join("; ", problems));

        // 提取器自身也有上限（20），因此清单不会变成日志转储；但**语料规模必须如实报出**。
        Assert.True(manifest!.KnownUnverified!.Count <= KnownUnverifiedExtractor.MaxItems);
        Assert.Equal(30, manifest.ChangelogFilesScanned);
        Assert.Equal(30, manifest.ChangelogFilesTotal);
        Assert.False(manifest.ChangelogCorpusIsTruncated);
    }

    [Fact]
    public void ATruncatedChangelogCorpusIsRefusedInsteadOfSilentlyUnderReporting()
    {
        // 取红用例（round-125 修的真实缺陷）：日志语料原先**静默截断**到常量上限（当时仓库已有 319 个文件、
        // 只扫 120 个），清单对此只字未提 ⇒ "未验证项"是从被截断的语料里提取的。
        // 现在的语义是：语料超过本次上限就**明确失败**，绝不静默少报。
        var publish = Publish("app.exe");
        var results = Results("A.trx", Trx(1, 0));
        var changelogs = Changelogs(Enumerable.Range(0, 12)
            .Select(index => ($"{index:000}.md", $"- 真机未验证：第 {index} 项"))
            .ToArray());

        var ok = DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(_root, publish, results, changelogs, "b", "c", ChangelogFileLimit: 20),
            out var manifest,
            out var problems);

        Assert.True(ok, string.Join("; ", problems));
        Assert.Equal(12, manifest!.ChangelogFilesScanned);

        // 上限调小 ⇒ 语料被截断 ⇒ 拒绝产出（而不是给出一份"看起来完整"的清单）。
        var refused = DeliveryFactCollector.TryCollect(
            new DeliveryCollectionRequest(_root, publish, results, changelogs, "b", "c", ChangelogFileLimit: 5),
            out var none,
            out var refusalProblems);

        Assert.False(refused, "语料被截断时必须拒绝，而不是静默少报未验证项");
        Assert.Null(none);
        Assert.Contains(refusalProblems, problem => problem.Contains("changelog corpus", StringComparison.Ordinal));
    }
    [Fact]
    public void NullRequestIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DeliveryFactCollector.TryCollect(null!, out _, out _));
    }
}
